using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkUsageMonitor.Monitoring;

/// <summary>
/// Повністю офлайновий резолвер IP -> ISO код країни.
/// Таблиця IPv4-діапазонів тримається в пам'яті у вигляді відсортованих масивів,
/// пошук — Array.BinarySearch (наносекунди, без мережі, без алокацій),
/// тому метод можна безпечно викликати прямо з потоку ETW.
/// </summary>
public sealed class GeoIpResolver
{
    /// <summary>Приватна / loopback / зарезервована адреса.</summary>
    public const string LocalCode = "local";

    /// <summary>Країну визначити не вдалося (база ще не завантажена, IPv6, адреси немає в базі).</summary>
    public const string UnknownCode = "unk";

    private const string DatabaseUrl =
        "https://raw.githubusercontent.com/sapics/ip-location-db/main/dbip-country/dbip-country-ipv4.csv";

    private const string DatabaseFileName = "geoip-ipv4.csv";

    // Загальний таймаут завантаження бази (заголовки + тіло).
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(15);

    // Якщо при старті (наприклад, автозапуск разом з Windows) мережі ще немає —
    // робимо кілька спроб із наростаючою паузою. Якщо файл уже є на диску, мережа не потрібна взагалі.
    private static readonly TimeSpan[] RetryDelays =
    {
        TimeSpan.Zero,
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10)
    };

    // Таймаут керується через CancellationTokenSource у DownloadAsync.
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly string _dataDirectory;
    private readonly string _filePath;

    // Три масиви публікуються як один незмінний знімок, щоб читач ніколи
    // не побачив _startIps від нової бази разом з _endIps від старої.
    private volatile GeoTable? _table;

    public GeoIpResolver(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        _dataDirectory = dataDirectory;
        _filePath = Path.Combine(dataDirectory, DatabaseFileName);

        // Усе важке (читання файлу / завантаження) — у фоні, конструктор не блокується.
        _ = Task.Run(InitializeAsync);
    }

    /// <summary>true, коли база завантажена в пам'ять і резолвер видає реальні коди країн.</summary>
    public bool IsReady => _table is not null;

    // ───────────────────────── Публічний API ─────────────────────────

    public string? ResolveCountryCode(string remoteAddress)
    {
        return IPAddress.TryParse(remoteAddress, out var ip)
            ? ResolveCountryCode(ip)
            : null;
    }

    /// <summary>
    /// Повертає дволітерний код країни в нижньому регістрі ("ua", "us", "de"),
    /// "local" для приватних/локальних адрес, "unk" якщо країну визначити неможливо,
    /// та null, якщо адреси немає.
    /// </summary>
    public string? ResolveCountryCode(IPAddress? remoteAddress)
    {
        if (remoteAddress is null)
        {
            return null;
        }

        if (TryGetIPv4(remoteAddress, out var ipv4))
        {
            return ResolveCountryCode(ipv4);
        }

        // Нативний IPv6: база лише IPv4, тож максимум що можемо сказати — локальна адреса чи ні.
        return IsPrivateOrLoopback(remoteAddress) ? LocalCode : UnknownCode;
    }

    /// <summary>Основний гарячий шлях: бінарний пошук по big-endian uint.</summary>
    public string ResolveCountryCode(uint ipv4)
    {
        if (IsPrivateOrReservedIPv4(ipv4))
        {
            return LocalCode;
        }

        var table = _table;
        if (table is null)
        {
            return UnknownCode;
        }

        var starts = table.StartIps;
        var index = Array.BinarySearch(starts, ipv4);

        // Точного збігу з початком діапазону немає — беремо діапазон, що починається перед адресою.
        if (index < 0)
        {
            index = ~index - 1;
        }

        if (index >= 0 && ipv4 <= table.EndIps[index])
        {
            return table.Countries[index];
        }

        return UnknownCode;
    }

    public static bool IsPrivateOrLoopback(IPAddress ip)
    {
        if (TryGetIPv4(ip, out var ipv4))
        {
            return IsPrivateOrReservedIPv4(ipv4);
        }

        return IPAddress.IsLoopback(ip)
            || ip.Equals(IPAddress.IPv6Any)
            || ip.IsIPv6LinkLocal
            || ip.IsIPv6SiteLocal
            || ip.IsIPv6UniqueLocal
            || ip.IsIPv6Multicast;
    }

    /// <summary>
    /// IPv4 (або IPv4-mapped IPv6) -> uint у big-endian порядку. Без алокацій.
    /// Для нативного IPv6 повертає false.
    /// </summary>
    public static bool TryGetIPv4(IPAddress? address, out uint value)
    {
        value = 0;
        if (address is null)
        {
            return false;
        }

        Span<byte> buffer = stackalloc byte[16];
        if (!address.TryWriteBytes(buffer, out var written))
        {
            return false;
        }

        if (written == 4)
        {
            value = BinaryPrimitives.ReadUInt32BigEndian(buffer);
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            value = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(12, 4));
            return true;
        }

        return false;
    }

    // ───────────────────────── Приватні діапазони ─────────────────────────

    private static bool IsPrivateOrReservedIPv4(uint ip)
    {
        var b0 = (byte)(ip >> 24);
        var b1 = (byte)(ip >> 16);

        return b0 == 0                                   // 0.0.0.0/8
            || b0 == 10                                  // 10.0.0.0/8
            || b0 == 127                                 // loopback
            || b0 >= 224                                 // multicast, reserved, broadcast
            || (b0 == 100 && b1 is >= 64 and <= 127)     // CGNAT 100.64.0.0/10
            || (b0 == 169 && b1 == 254)                  // link-local
            || (b0 == 172 && b1 is >= 16 and <= 31)      // 172.16.0.0/12
            || (b0 == 192 && b1 == 168);                 // 192.168.0.0/16
    }

    // ───────────────────────── Ініціалізація бази ─────────────────────────

    private async Task InitializeAsync()
    {
        try
        {
            Directory.CreateDirectory(_dataDirectory);

            if (File.Exists(_filePath))
            {
                var existing = LoadTable(_filePath);
                if (existing is not null)
                {
                    _table = existing;
                    return;
                }

                // Файл пошкоджений або порожній — видаляємо й качаємо наново.
                TryDelete(_filePath);
            }

            var tempPath = _filePath + ".tmp";

            foreach (var delay in RetryDelays)
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay).ConfigureAwait(false);
                }

                try
                {
                    await DownloadAsync(tempPath).ConfigureAwait(false);

                    // Спочатку перевіряємо, що завантажене — валідна база, і лише потім підміняємо файл.
                    var table = LoadTable(tempPath)
                        ?? throw new InvalidDataException("Downloaded GeoIP file contains no valid IPv4 ranges.");

                    File.Move(tempPath, _filePath, overwrite: true);
                    _table = table;
                    return;
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[GeoIpResolver] Database download attempt failed: {ex.Message}");
                    TryDelete(tempPath);
                }
            }

            Trace.WriteLine("[GeoIpResolver] GeoIP database unavailable; all public IPs will resolve to 'unk'.");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[GeoIpResolver] Initialization failed: {ex}");
        }
    }

    private static async Task DownloadAsync(string destinationPath)
    {
        using var cts = new CancellationTokenSource(DownloadTimeout);

        using var response = await Http
            .GetAsync(DatabaseUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        await using var network = await response.Content
            .ReadAsStreamAsync(cts.Token)
            .ConfigureAwait(false);

        await using var file = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);

        await network.CopyToAsync(file, cts.Token).ConfigureAwait(false);
    }

    private static GeoTable? LoadTable(string path)
    {
        try
        {
            var ranges = new List<IpRange>(capacity: 400_000);
            var countryPool = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var line in File.ReadLines(path))
            {
                if (TryParseLine(line, countryPool, out var range))
                {
                    ranges.Add(range);
                }
            }

            if (ranges.Count == 0)
            {
                return null;
            }

            // Бінарний пошук вимагає сортування за початком діапазону.
            // Файл зазвичай уже відсортований, тоді Sort відпрацьовує дуже швидко.
            ranges.Sort(static (a, b) => a.Start.CompareTo(b.Start));

            var starts = new uint[ranges.Count];
            var ends = new uint[ranges.Count];
            var countries = new string[ranges.Count];

            for (var i = 0; i < ranges.Count; i++)
            {
                starts[i] = ranges[i].Start;
                ends[i] = ranges[i].End;
                countries[i] = ranges[i].Country;
            }

            return new GeoTable(starts, ends, countries);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[GeoIpResolver] Failed to load '{path}': {ex.Message}");
            return null;
        }
    }

    /// <summary>Формат рядка: start_ip,end_ip,country_code</summary>
    private static bool TryParseLine(string line, Dictionary<string, string> countryPool, out IpRange range)
    {
        range = default;

        var span = line.AsSpan().Trim();
        if (span.IsEmpty)
        {
            return false;
        }

        var firstComma = span.IndexOf(',');
        if (firstComma < 0)
        {
            return false;
        }

        var rest = span[(firstComma + 1)..];
        var secondComma = rest.IndexOf(',');
        if (secondComma < 0)
        {
            return false;
        }

        if (!TryParseIPv4(span[..firstComma].Trim(), out var start)
            || !TryParseIPv4(rest[..secondComma].Trim(), out var end)
            || end < start)
        {
            return false;
        }

        var code = rest[(secondComma + 1)..].Trim().Trim('"');
        if (code.Length != 2)
        {
            return false;
        }

        var lower = code.ToString().ToLowerInvariant();

        // "zz" у DB-IP — невизначена / зарезервована країна: не додаємо, такі адреси отримають "unk".
        if (lower == "zz")
        {
            return false;
        }

        // Інтернуємо рядки країн, щоб 400k діапазонів не тримали 400k окремих string.
        if (!countryPool.TryGetValue(lower, out var pooled))
        {
            countryPool[lower] = pooled = lower;
        }

        range = new IpRange(start, end, pooled);
        return true;
    }

    private static bool TryParseIPv4(ReadOnlySpan<char> text, out uint value)
    {
        value = 0;

        uint result = 0;
        var octets = 0;
        var current = 0;
        var digits = 0;

        foreach (var ch in text)
        {
            if (ch == '.')
            {
                if (digits == 0 || octets == 3)
                {
                    return false;
                }

                result = (result << 8) | (uint)current;
                octets++;
                current = 0;
                digits = 0;
            }
            else if (ch is >= '0' and <= '9')
            {
                current = current * 10 + (ch - '0');
                digits++;

                if (digits > 3 || current > 255)
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }

        if (digits == 0 || octets != 3)
        {
            return false;
        }

        value = (result << 8) | (uint)current;
        return true;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Файл може бути тимчасово заблокований — не критично.
        }
    }

    // ───────────────────────── Структури даних ─────────────────────────

    private readonly record struct IpRange(uint Start, uint End, string Country);

    private sealed class GeoTable
    {
        public GeoTable(uint[] startIps, uint[] endIps, string[] countries)
        {
            StartIps = startIps;
            EndIps = endIps;
            Countries = countries;
        }

        public uint[] StartIps { get; }
        public uint[] EndIps { get; }
        public string[] Countries { get; }
    }
}