using System.IO;
using Microsoft.Data.Sqlite;

using NetworkUsageMonitor.Models;

namespace NetworkUsageMonitor.Persistence;

public enum TrafficDirection { Download, Upload, Total }

public sealed record AppTotal(string ProcessName, long BytesSent, long BytesReceived, string? ExecPath);
public sealed record TimelinePoint(DateTimeOffset BucketStart, long BytesSent, long BytesReceived);
public sealed record HeatmapCell(int DayOfWeek, int Hour, long TotalBytes);
public sealed record CountryStat(string CountryCode, long BytesSent, long BytesReceived, long TotalConnections);
public sealed record AppDetails(
    string ProcessName,
    List<TimelinePoint> Timeline,
    string PeakDayDate,
    long PeakDayBytes,
    List<CountryStat> TopCountries);

public sealed class SqliteUsageRepository : IDisposable
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(6);

    private readonly string _connectionString;
    private readonly object _writeLock = new();
    private readonly Timer _cleanupTimer;
    private DateTimeOffset _lastCleanupUtc = DateTimeOffset.MinValue;
    private bool _disposed;

    public SqliteUsageRepository(string dataDirectory)
    {
        var dbPath = Path.Combine(dataDirectory, "usage.db");
        _connectionString = $"Data Source={dbPath};Pooling=True;Default Timeout=5";
        Initialize();

        _cleanupTimer = new Timer(
            callback: _ => TryRunScheduledCleanup(),
            state: null,
            dueTime: CleanupInterval,
            period: CleanupInterval);
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private void Initialize()
    {
        lock (_writeLock)
        {
            using (var connection = OpenConnection())
            {
                MigrateLegacySchemaIfNeeded(connection);

                using var cmd = connection.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS usage_samples (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        timestamp INTEGER NOT NULL,
                        process_name TEXT NOT NULL,
                        exec_path TEXT,
                        bytes_sent INTEGER NOT NULL,
                        bytes_received INTEGER NOT NULL,
                        adapter_name TEXT NOT NULL,
                        session_id TEXT NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS idx_usage_timestamp ON usage_samples(timestamp);
                    CREATE INDEX IF NOT EXISTS idx_usage_process ON usage_samples(process_name);
                    CREATE INDEX IF NOT EXISTS idx_usage_adapter_ts ON usage_samples(adapter_name, timestamp);
                    CREATE INDEX IF NOT EXISTS idx_usage_session ON usage_samples(session_id);
                    """;
                cmd.ExecuteNonQuery();

                using var countryCmd = connection.CreateCommand();
                countryCmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS country_samples (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        timestamp INTEGER NOT NULL,
                        process_name TEXT NOT NULL,
                        country_code TEXT NOT NULL,
                        bytes_sent INTEGER NOT NULL,
                        bytes_received INTEGER NOT NULL,
                        connection_count INTEGER NOT NULL,
                        adapter_name TEXT NOT NULL,
                        session_id TEXT NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS idx_country_ts ON country_samples(timestamp);
                    CREATE INDEX IF NOT EXISTS idx_country_code ON country_samples(country_code, timestamp);
                    CREATE INDEX IF NOT EXISTS idx_country_process ON country_samples(process_name, timestamp);
                    """;
                countryCmd.ExecuteNonQuery();

                MigrateLegacyCountryUsageIfNeeded(connection);
            }

            CompressAndCleanupDatabase();
        }
    }

    private void TryRunScheduledCleanup()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            CompressAndCleanupDatabase();
        }
        catch
        {
            // Уникаємо падіння фонового потоку таймера у разі тимчасової помилки I/O.
        }
    }

    /// <summary>
    /// Очищує спам-записи `Pid-XXXX` та агрегує детальні 15-секундні семпли,
    /// старші за 48 годин, у погодинні бакети без втрати точності статистики.
    /// </summary>
    public void CompressAndCleanupDatabase()
    {
        lock (_writeLock)
        {
            if (_disposed)
            {
                return;
            }

            _lastCleanupUtc = DateTimeOffset.UtcNow;

            using var connection = OpenConnection();
            // Вирівнюємо межу 48 годин до початку повної години ((ts / 3600) * 3600),
            // щоб ніколи не ділити годину навпіл під час агрегації.
            long cutoff = ((DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (48 * 3600)) / 3600) * 3600;

            int renamedRows = 0;
            int deletedRows = 0;
            int insertedRows = 0;

            using (var beginCmd = connection.CreateCommand())
            {
                beginCmd.CommandText = "BEGIN IMMEDIATE TRANSACTION;";
                beginCmd.ExecuteNonQuery();
            }

            try
            {
                // а) Перейменовуємо всі старі сміттєві записи Pid-% / pid-% у єдину групу
                using (var updateCmd = connection.CreateCommand())
                {
                    updateCmd.CommandText = """
                        UPDATE usage_samples
                        SET process_name = 'Background / Short-lived',
                            exec_path = NULL
                        WHERE process_name LIKE 'Pid-%' OR process_name LIKE 'pid-%';
                        """;
                    renamedRows = updateCmd.ExecuteNonQuery();
                }

                using (var countryRenameCmd = connection.CreateCommand())
                {
                    countryRenameCmd.CommandText = """
                        UPDATE country_samples
                        SET process_name = 'Background / Short-lived'
                        WHERE process_name LIKE 'Pid-%' OR process_name LIKE 'pid-%';
                        """;
                    renamedRows += countryRenameCmd.ExecuteNonQuery();
                }

                // б) Агрегуємо всі записи, старші за 48 годин, у погодинні бакети у тимчасову таблицю
                using (var rollupCmd = connection.CreateCommand())
                {
                    rollupCmd.CommandText = """
                        DROP TABLE IF EXISTS temp.hourly_rollup;

                        CREATE TEMP TABLE hourly_rollup AS
                        SELECT
                            (timestamp / 3600) * 3600 AS hour_ts,
                            process_name,
                            MAX(exec_path) AS exec_path,
                            SUM(bytes_sent) AS bytes_sent,
                            SUM(bytes_received) AS bytes_received,
                            adapter_name,
                            session_id
                        FROM usage_samples
                        WHERE timestamp < $cutoff
                        GROUP BY (timestamp / 3600) * 3600, process_name, adapter_name, session_id;
                        """;
                    rollupCmd.Parameters.AddWithValue("$cutoff", cutoff);
                    rollupCmd.ExecuteNonQuery();
                }

                // Видаляємо старі дрібні записи за цей період
                using (var deleteCmd = connection.CreateCommand())
                {
                    deleteCmd.CommandText = "DELETE FROM usage_samples WHERE timestamp < $cutoff;";
                    deleteCmd.Parameters.AddWithValue("$cutoff", cutoff);
                    deletedRows = deleteCmd.ExecuteNonQuery();
                }

                // Вставляємо згруповані погодинні записи назад у таблицю
                using (var insertCmd = connection.CreateCommand())
                {
                    insertCmd.CommandText = """
                        INSERT INTO usage_samples
                            (timestamp, process_name, exec_path, bytes_sent, bytes_received, adapter_name, session_id)
                        SELECT
                            hour_ts, process_name, exec_path, bytes_sent, bytes_received, adapter_name, session_id
                        FROM temp.hourly_rollup;
                        """;
                    insertedRows = insertCmd.ExecuteNonQuery();
                }

                // б-2) Погодинний rollup для country_samples: та сама межа cutoff і та сама транзакція
                using (var countryRollupCmd = connection.CreateCommand())
                {
                    countryRollupCmd.CommandText = """
                        DROP TABLE IF EXISTS temp.country_rollup;

                        CREATE TEMP TABLE country_rollup AS
                        SELECT
                            (timestamp / 3600) * 3600 AS hour_ts,
                            process_name,
                            country_code,
                            SUM(bytes_sent) AS bytes_sent,
                            SUM(bytes_received) AS bytes_received,
                            SUM(connection_count) AS connection_count,
                            adapter_name,
                            session_id
                        FROM country_samples
                        WHERE timestamp < $cutoff
                        GROUP BY (timestamp / 3600) * 3600, process_name, country_code, adapter_name, session_id;
                        """;
                    countryRollupCmd.Parameters.AddWithValue("$cutoff", cutoff);
                    countryRollupCmd.ExecuteNonQuery();
                }

                using (var countryDeleteCmd = connection.CreateCommand())
                {
                    countryDeleteCmd.CommandText = "DELETE FROM country_samples WHERE timestamp < $cutoff;";
                    countryDeleteCmd.Parameters.AddWithValue("$cutoff", cutoff);
                    deletedRows += countryDeleteCmd.ExecuteNonQuery();
                }

                using (var countryInsertCmd = connection.CreateCommand())
                {
                    countryInsertCmd.CommandText = """
                        INSERT INTO country_samples
                            (timestamp, process_name, country_code, bytes_sent, bytes_received, connection_count, adapter_name, session_id)
                        SELECT
                            hour_ts, process_name, country_code, bytes_sent, bytes_received, connection_count, adapter_name, session_id
                        FROM temp.country_rollup;
                        """;
                    insertedRows += countryInsertCmd.ExecuteNonQuery();
                }

                using (var countryDropCmd = connection.CreateCommand())
                {
                    countryDropCmd.CommandText = "DROP TABLE IF EXISTS temp.country_rollup;";
                    countryDropCmd.ExecuteNonQuery();
                }

                using (var commitCmd = connection.CreateCommand())
                {
                    commitCmd.CommandText = """
                        DROP TABLE IF EXISTS temp.hourly_rollup;
                        COMMIT;
                        """;
                    commitCmd.ExecuteNonQuery();
                }
            }
            catch
            {
                using var rollbackCmd = connection.CreateCommand();
                rollbackCmd.CommandText = "ROLLBACK;";
                try
                {
                    rollbackCmd.ExecuteNonQuery();
                }
                catch
                {
                    // Ігноруємо помилку відкату, якщо транзакція вже закрита.
                }

                throw;
            }

            // в) Якщо реально вивільнено більше 5000 рядків (або перейменовано понад 5000 спам-записів),
            // виконуємо TRUNCATE WAL-журналу та VACUUM для фізичного зменшення файлу usage.db на диску.
            int netRemovedRows = deletedRows - insertedRows;
            if (netRemovedRows > 5000 || renamedRows > 5000)
            {
                try
                {
                    using var maintenanceCmd = connection.CreateCommand();
                    maintenanceCmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); VACUUM;";
                    maintenanceCmd.ExecuteNonQuery();
                }
                catch (SqliteException)
                {
                    try
                    {
                        using var passiveCmd = connection.CreateCommand();
                        passiveCmd.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
                        passiveCmd.ExecuteNonQuery();
                    }
                    catch
                    {
                        // Ігноруємо помилку пасивного чекпойнта, якщо база зайнята паралельними читачами.
                    }
                }
            }
        }
    }

    /// <summary>
    /// Попередня версія зберігала країни у таблиці `country_usage`. Переносимо
    /// її рядки в `country_samples` і видаляємо стару таблицю в одній транзакції,
    /// щоб уже накопичена статистика не загубилась.
    /// </summary>
    private static void MigrateLegacyCountryUsageIfNeeded(SqliteConnection connection)
    {
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'country_usage' LIMIT 1;";
            if (check.ExecuteScalar() is null)
            {
                return;
            }
        }

        using var transaction = connection.BeginTransaction();
        using var migrate = connection.CreateCommand();
        migrate.Transaction = transaction;
        migrate.CommandText = """
            INSERT INTO country_samples
                (timestamp, process_name, country_code, bytes_sent, bytes_received, connection_count, adapter_name, session_id)
            SELECT
                timestamp, process_name, country_code, bytes_sent, bytes_received, connection_count, adapter_name, session_id
            FROM country_usage
            ORDER BY id;

            DROP TABLE country_usage;
            """;
        migrate.ExecuteNonQuery();
        transaction.Commit();
    }

    /// <summary>
    /// Older builds stored a numeric `interface_category` column plus a separate
    /// `app_categories` table for the data-cap and app-category features that
    /// this refactor removes. That layout can't be mapped onto the new
    /// adapter-name schema, so if it's detected we drop and rebuild the table
    /// rather than fail every query with "no such column".
    /// </summary>
    private static void MigrateLegacySchemaIfNeeded(SqliteConnection connection)
    {
        bool tableExists = false;
        bool hasLegacyColumn = false;

        using (var check = connection.CreateCommand())
        {
            check.CommandText = "PRAGMA table_info(usage_samples);";
            using var reader = check.ExecuteReader();
            while (reader.Read())
            {
                tableExists = true;
                if (string.Equals(reader.GetString(1), "interface_category", StringComparison.OrdinalIgnoreCase))
                {
                    hasLegacyColumn = true;
                }
            }
        }

        if (tableExists && hasLegacyColumn)
        {
            using var drop = connection.CreateCommand();
            drop.CommandText = "DROP TABLE IF EXISTS usage_samples; DROP TABLE IF EXISTS app_categories;";
            drop.ExecuteNonQuery();
        }
    }

    public void InsertBatch(IReadOnlyList<UsageBucket> buckets)
    {
        if (buckets.Count == 0)
        {
            return;
        }

        lock (_writeLock)
        {
            if (_disposed)
            {
                return;
            }

            // Додатковий захист: якщо ПК перебував у режимі сну і пропустив тік таймера,
            // виконуємо планове очищення перед або після запису батчу.
            if (DateTimeOffset.UtcNow - _lastCleanupUtc >= CleanupInterval)
            {
                CompressAndCleanupDatabase();
            }

            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT INTO usage_samples
                    (timestamp, process_name, exec_path, bytes_sent, bytes_received, adapter_name, session_id)
                VALUES
                    ($ts, $name,$path, $sent,$recv, $adapter,$session);
                """;

            var pTs = cmd.CreateParameter(); pTs.ParameterName = "$ts"; cmd.Parameters.Add(pTs);
            var pName = cmd.CreateParameter(); pName.ParameterName = "$name"; cmd.Parameters.Add(pName);
            var pPath = cmd.CreateParameter(); pPath.ParameterName = "$path"; cmd.Parameters.Add(pPath);
            var pSent = cmd.CreateParameter(); pSent.ParameterName = "$sent"; cmd.Parameters.Add(pSent);
            var pRecv = cmd.CreateParameter(); pRecv.ParameterName = "$recv"; cmd.Parameters.Add(pRecv);
            var pAdapter = cmd.CreateParameter(); pAdapter.ParameterName = "$adapter"; cmd.Parameters.Add(pAdapter);
            var pSession = cmd.CreateParameter(); pSession.ParameterName = "$session"; cmd.Parameters.Add(pSession);

            foreach (var bucket in buckets)
            {
                pTs.Value = bucket.Timestamp.ToUnixTimeSeconds();
                pName.Value = bucket.ProcessName;
                pPath.Value = (object?)bucket.ExecutablePath ?? DBNull.Value;
                pSent.Value = bucket.BytesSent;
                pRecv.Value = bucket.BytesReceived;
                pAdapter.Value = bucket.AdapterName;
                pSession.Value = bucket.SessionId;
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    public void InsertCountryBatch(IReadOnlyList<CountryUsageBucket> buckets)
    {
        if (buckets.Count == 0)
        {
            return;
        }

        lock (_writeLock)
        {
            if (_disposed)
            {
                return;
            }

            if (DateTimeOffset.UtcNow - _lastCleanupUtc >= CleanupInterval)
            {
                CompressAndCleanupDatabase();
            }

            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT INTO country_samples
                    (timestamp, process_name, country_code, bytes_sent, bytes_received, connection_count, adapter_name, session_id)
                VALUES
                    ($ts, $name, $country, $sent, $recv, $conns, $adapter, $session);
                """;

            var pTs = cmd.CreateParameter(); pTs.ParameterName = "$ts"; cmd.Parameters.Add(pTs);
            var pName = cmd.CreateParameter(); pName.ParameterName = "$name"; cmd.Parameters.Add(pName);
            var pCountry = cmd.CreateParameter(); pCountry.ParameterName = "$country"; cmd.Parameters.Add(pCountry);
            var pSent = cmd.CreateParameter(); pSent.ParameterName = "$sent"; cmd.Parameters.Add(pSent);
            var pRecv = cmd.CreateParameter(); pRecv.ParameterName = "$recv"; cmd.Parameters.Add(pRecv);
            var pConns = cmd.CreateParameter(); pConns.ParameterName = "$conns"; cmd.Parameters.Add(pConns);
            var pAdapter = cmd.CreateParameter(); pAdapter.ParameterName = "$adapter"; cmd.Parameters.Add(pAdapter);
            var pSession = cmd.CreateParameter(); pSession.ParameterName = "$session"; cmd.Parameters.Add(pSession);

            foreach (var bucket in buckets)
            {
                pTs.Value = bucket.Timestamp.ToUnixTimeSeconds();
                pName.Value = bucket.ProcessName;
                pCountry.Value = bucket.CountryCode;
                pSent.Value = bucket.BytesSent;
                pRecv.Value = bucket.BytesReceived;
                pConns.Value = bucket.ConnectionCount;
                pAdapter.Value = bucket.AdapterName;
                pSession.Value = bucket.SessionId;
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    /// <summary>
    /// Підсумки по застосунках. Якщо передано <paramref name="countryCode"/>, дані беруться
    /// з country_samples (де country_code = $country), інакше - з usage_samples.
    /// </summary>
    public List<AppTotal> GetAppTotals(
        DateTimeOffset from,
        DateTimeOffset to,
        string? sessionId = null,
        string? adapterName = null,
        int limit = 500,
        string? countryCode = null)
    {
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();

        if (string.IsNullOrWhiteSpace(countryCode))
        {
            cmd.CommandText = """
                SELECT process_name, SUM(bytes_sent) AS total_sent, SUM(bytes_received) AS total_recv, MAX(exec_path)
                FROM usage_samples
                WHERE timestamp >= $from AND timestamp <= $to
                  AND ($session IS NULL OR session_id = $session)
                  AND ($adapter IS NULL OR adapter_name = $adapter)
                GROUP BY process_name
                HAVING (total_sent + total_recv) > 0
                ORDER BY (total_sent + total_recv) DESC
                LIMIT $limit;
                """;
        }
        else
        {
            // country_samples не зберігає exec_path - підтягуємо його з usage_samples
            // вже після групування, щоб кореляційний підзапит виконувався лише для підсумкових рядків.
            cmd.CommandText = """
                SELECT g.process_name,
                       g.total_sent,
                       g.total_recv,
                       (SELECT MAX(u.exec_path) FROM usage_samples u WHERE u.process_name = g.process_name)
                FROM (
                    SELECT process_name, SUM(bytes_sent) AS total_sent, SUM(bytes_received) AS total_recv
                    FROM country_samples
                    WHERE timestamp >= $from AND timestamp <= $to
                      AND country_code = $country
                      AND ($session IS NULL OR session_id = $session)
                      AND ($adapter IS NULL OR adapter_name = $adapter)
                    GROUP BY process_name
                    HAVING (total_sent + total_recv) > 0
                ) g
                ORDER BY (g.total_sent + g.total_recv) DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$country", countryCode);
        }

        cmd.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$to", to.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$session", (object?)sessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$adapter", (object?)adapterName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$limit", limit);

        var results = new List<AppTotal>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new AppTotal(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }
        return results;
    }

    public List<TimelinePoint> GetTimeline(
        DateTimeOffset from,
        DateTimeOffset to,
        int bucketSeconds,
        string? sessionId = null,
        string? adapterName = null,
        string? countryCode = null)
        => QueryTimeline(from, to, bucketSeconds, sessionId, adapterName, countryCode, processName: null);

    /// <summary>
    /// Спільна реалізація таймлайна: обирає таблицю за наявністю фільтра країни
    /// та за потреби звужує вибірку до одного процесу.
    /// Ім'я таблиці та додаткові умови - лише константи, користувацькі значення йдуть параметрами.
    /// </summary>
    private List<TimelinePoint> QueryTimeline(
        DateTimeOffset from,
        DateTimeOffset to,
        int bucketSeconds,
        string? sessionId,
        string? adapterName,
        string? countryCode,
        string? processName)
    {
        bool byCountry = !string.IsNullOrWhiteSpace(countryCode);
        string table = byCountry ? "country_samples" : "usage_samples";
        string countryClause = byCountry ? "AND country_code = $country" : "";
        string processClause = processName is null ? "" : "AND process_name = $name";

        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT (timestamp / $bucket) * $bucket AS bucket_start, SUM(bytes_sent), SUM(bytes_received)
            FROM {table}
            WHERE timestamp >= $from AND timestamp <= $to
              AND ($session IS NULL OR session_id = $session)
              AND ($adapter IS NULL OR adapter_name = $adapter)
              {countryClause}
              {processClause}
            GROUP BY bucket_start
            ORDER BY bucket_start ASC;
            """;
        cmd.Parameters.AddWithValue("$bucket", bucketSeconds);
        cmd.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$to", to.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$session", (object?)sessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$adapter", (object?)adapterName ?? DBNull.Value);
        if (byCountry)
        {
            cmd.Parameters.AddWithValue("$country", countryCode!);
        }
        if (processName is not null)
        {
            cmd.Parameters.AddWithValue("$name", processName);
        }

        var results = new List<TimelinePoint>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new TimelinePoint(
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)),
                reader.GetInt64(1),
                reader.GetInt64(2)));
        }
        return results;
    }

    public List<HeatmapCell> GetHeatmap(
        DateTimeOffset from, DateTimeOffset to, string? adapterName = null, string? countryCode = null)
    {
        bool byCountry = !string.IsNullOrWhiteSpace(countryCode);
        string table = byCountry ? "country_samples" : "usage_samples";
        string countryClause = byCountry ? "AND country_code = $country" : "";

        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        // SQLite's strftime('%w', ...) returns 0=Sunday..6=Saturday. Remap to
        // 0=Monday..6=Sunday at the source so every consumer of this data
        // (currently just the frontend heatmap) can treat index 0 as Monday
        // without having to know about SQLite's convention.
        cmd.CommandText = $"""
            SELECT (CAST(strftime('%w', timestamp, 'unixepoch', 'localtime') AS INTEGER) + 6) % 7 AS dow,
                   CAST(strftime('%H', timestamp, 'unixepoch', 'localtime') AS INTEGER) AS hour,
                   SUM(bytes_sent + bytes_received)
            FROM {table}
            WHERE timestamp >= $from AND timestamp <= $to
              AND ($adapter IS NULL OR adapter_name = $adapter)
              {countryClause}
            GROUP BY dow, hour;
            """;
        cmd.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$to", to.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$adapter", (object?)adapterName ?? DBNull.Value);
        if (byCountry)
        {
            cmd.Parameters.AddWithValue("$country", countryCode!);
        }

        var results = new List<HeatmapCell>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new HeatmapCell(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetInt64(2)));
        }
        return results;
    }

    /// <summary>
    /// Трафік за країнами, відсортований за сумою байтів (sent + received) за спаданням.
    /// Якщо задано <paramref name="processName"/>, повертає розбивку лише для цього застосунку.
    /// TotalConnections - сума connection_count по всіх семплах (не унікальні з'єднання).
    /// </summary>
    public List<CountryStat> GetCountryTotals(
        DateTimeOffset from,
        DateTimeOffset to,
        string? sessionId,
        string? adapterName,
        string? processName = null)
    {
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT country_code,
                   SUM(bytes_sent) AS total_sent,
                   SUM(bytes_received) AS total_recv,
                   SUM(connection_count) AS total_conns
            FROM country_samples
            WHERE timestamp >= $from AND timestamp <= $to
              AND ($session IS NULL OR session_id = $session)
              AND ($adapter IS NULL OR adapter_name = $adapter)
              AND ($process IS NULL OR process_name = $process)
            GROUP BY country_code
            ORDER BY (total_sent + total_recv) DESC, total_conns DESC;
            """;
        cmd.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$to", to.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$session", (object?)sessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$adapter", (object?)adapterName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$process", (object?)processName ?? DBNull.Value);

        var results = new List<CountryStat>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new CountryStat(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3)));
        }
        return results;
    }

    /// <summary>
    /// Усі місяці ("YYYY-MM", за локальним часом), для яких у usage_samples є дані. Найновіші - першими.
    /// </summary>
    public List<string> GetAvailableMonths()
    {
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT strftime('%Y-%m', timestamp, 'unixepoch', 'localtime') AS month
            FROM usage_samples
            ORDER BY month DESC;
            """;

        var results = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(reader.GetString(0));
        }
        return results;
    }

    /// <summary>
    /// Деталі по одному застосунку: власний таймлайн, піковий день (за локальним часом)
    /// та топ-5 країн. Таймлайн повертається «як є» (без zero-filling).
    /// </summary>
    public AppDetails GetAppDetails(
        string processName,
        DateTimeOffset from,
        DateTimeOffset to,
        int bucketSeconds,
        string? adapterName)
    {
        var timeline = QueryTimeline(
            from, to, bucketSeconds,
            sessionId: null,
            adapterName: adapterName,
            countryCode: null,
            processName: processName);

        string peakDayDate = string.Empty;
        long peakDayBytes = 0;

        using (var connection = OpenConnection())
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT strftime('%Y-%m-%d', timestamp, 'unixepoch', 'localtime') AS day,
                       SUM(bytes_sent + bytes_received) AS total
                FROM usage_samples
                WHERE process_name = $name
                  AND timestamp >= $from AND timestamp <= $to
                  AND ($adapter IS NULL OR adapter_name = $adapter)
                GROUP BY day
                ORDER BY total DESC, day ASC
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$name", processName);
            cmd.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$to", to.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$adapter", (object?)adapterName ?? DBNull.Value);

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                peakDayDate = reader.GetString(0);
                peakDayBytes = reader.GetInt64(1);
            }
        }

        var topCountries = GetCountryTotals(from, to, null, adapterName, processName)
            .Take(5)
            .ToList();

        return new AppDetails(processName, timeline, peakDayDate, peakDayBytes, topCountries);
    }

    /// <summary>
    /// Distinct adapter names that have ever recorded traffic, for the
    /// dashboard's adapter filter toggle.
    /// </summary>
    public List<string> GetKnownAdapters()
    {
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT adapter_name
            FROM usage_samples
            ORDER BY adapter_name ASC;
            """;

        var results = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(reader.GetString(0));
        }
        return results;
    }

    public IEnumerable<(long Timestamp, string ProcessName, long Sent, long Received, string AdapterName)> ExportRaw(
        DateTimeOffset from, DateTimeOffset to)
    {
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT timestamp, process_name, bytes_sent, bytes_received, adapter_name
            FROM usage_samples
            WHERE timestamp >= $from AND timestamp <=$to
            ORDER BY timestamp ASC;
            """;
        cmd.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$to", to.ToUnixTimeSeconds());

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            yield return (
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetString(4));
        }
    }

    public void Dispose()
    {
        lock (_writeLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cleanupTimer.Dispose();
        }

        SqliteConnection.ClearAllPools();
    }
}