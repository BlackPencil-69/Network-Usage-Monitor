using System.IO;
using Microsoft.Data.Sqlite;

using NetworkUsageMonitor.Models;

namespace NetworkUsageMonitor.Persistence;

public enum TrafficDirection { Download, Upload, Total }

public sealed record AppTotal(string ProcessName, long BytesSent, long BytesReceived, string? ExecPath);
public sealed record TimelinePoint(DateTimeOffset BucketStart, long BytesSent, long BytesReceived);
public sealed record HeatmapCell(int DayOfWeek, int Hour, long TotalBytes);

public sealed class SqliteUsageRepository : IDisposable
{
    private readonly string _connectionString;
    private readonly object _writeLock = new();

    public SqliteUsageRepository(string dataDirectory)
    {
        var dbPath = Path.Combine(dataDirectory, "usage.db");
        _connectionString = $"Data Source={dbPath};Cache=Shared";
        Initialize();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private void Initialize()
    {
        using var connection = OpenConnection();
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
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT INTO usage_samples
                    (timestamp, process_name, exec_path, bytes_sent, bytes_received, adapter_name, session_id)
                VALUES
                    ($ts, $name, $path, $sent, $recv, $adapter, $session);
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

    public List<AppTotal> GetAppTotals(
        DateTimeOffset from, DateTimeOffset to, string? sessionId = null, string? adapterName = null)
    {
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT process_name, SUM(bytes_sent), SUM(bytes_received), MAX(exec_path)
            FROM usage_samples
            WHERE timestamp >= $from AND timestamp <= $to
              AND ($session IS NULL OR session_id = $session)
              AND ($adapter IS NULL OR adapter_name = $adapter)
            GROUP BY process_name
            ORDER BY (SUM(bytes_sent) + SUM(bytes_received)) DESC;
            """;
        cmd.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$to", to.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$session", (object?)sessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$adapter", (object?)adapterName ?? DBNull.Value);

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
        DateTimeOffset from, DateTimeOffset to, int bucketSeconds, string? sessionId = null, string? adapterName = null)
    {
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT (timestamp / $bucket) * $bucket AS bucket_start, SUM(bytes_sent), SUM(bytes_received)
            FROM usage_samples
            WHERE timestamp >= $from AND timestamp <= $to
              AND ($session IS NULL OR session_id = $session)
              AND ($adapter IS NULL OR adapter_name = $adapter)
            GROUP BY bucket_start
            ORDER BY bucket_start ASC;
            """;
        cmd.Parameters.AddWithValue("$bucket", bucketSeconds);
        cmd.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$to", to.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$session", (object?)sessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$adapter", (object?)adapterName ?? DBNull.Value);

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

    public List<HeatmapCell> GetHeatmap(DateTimeOffset from, DateTimeOffset to, string? adapterName = null)
    {
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        // SQLite's strftime('%w', ...) returns 0=Sunday..6=Saturday. Remap to
        // 0=Monday..6=Sunday at the source so every consumer of this data
        // (currently just the frontend heatmap) can treat index 0 as Monday
        // without having to know about SQLite's convention.
        cmd.CommandText = """
            SELECT (CAST(strftime('%w', timestamp, 'unixepoch', 'localtime') AS INTEGER) + 6) % 7 AS dow,
                   CAST(strftime('%H', timestamp, 'unixepoch', 'localtime') AS INTEGER) AS hour,
                   SUM(bytes_sent + bytes_received)
            FROM usage_samples
            WHERE timestamp >= $from AND timestamp <= $to
              AND ($adapter IS NULL OR adapter_name = $adapter)
            GROUP BY dow, hour;
            """;
        cmd.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$to", to.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$adapter", (object?)adapterName ?? DBNull.Value);

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
            WHERE timestamp >= $from AND timestamp <= $to
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
        SqliteConnection.ClearAllPools();
    }
}
