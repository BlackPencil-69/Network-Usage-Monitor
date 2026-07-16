using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace NetworkUsageMonitor.Monitoring;

/// <summary>
/// Оптимізований резолвер IP -> ISO код країни з дедуплікацією запитів та збереженням кешу у файл.
/// </summary>
public sealed class GeoIpResolver
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };
    
    // Шлях до файлу кешу в папці з exe-файлом програми
    private static readonly string CacheFilePath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "ip_cache.json");

    // Основний кеш IP -> Код країни
    private readonly ConcurrentDictionary<string, string> _cache;
    
    // Словник для відстеження активних запитів (запобігає дублюванню паралельних викликів)
    private readonly ConcurrentDictionary<string, Task<string?>> _activeRequests = new();
    private readonly object _fileLock = new();

    public GeoIpResolver()
    {
        _cache = LoadCacheFromFile();
    }

    public string? ResolveCountryCode(string remoteAddress)
    {
        if (!IPAddress.TryParse(remoteAddress, out var ip) || IsPrivateOrLoopback(ip))
        {
            return null;
        }

        // 1. Якщо вже є в локальному кеші — повертаємо миттєво!
        if (_cache.TryGetValue(remoteAddress, out var cached))
        {
            return cached;
        }

        // 2. Якщо в кеші немає — запускаємо фоновий таск БЕЗ очікування.
        // Використовуємо GetOrAdd, щоб не запускати дублікати для одного й того самого IP.
        _ = _activeRequests.GetOrAdd(remoteAddress, _ => Task.Run(() => FetchCountryCodeAsync(remoteAddress)));

        // Повертаємо null відразу, щоб інтерфейс не «висів»
        return null; 
    }

    private async Task<string?> FetchCountryCodeAsync(string remoteAddress)
    {
        string? countryCode = null;
        try
        {
            var json = await Http.GetStringAsync($"http://ip-api.com/json/{remoteAddress}?fields=status,countryCode");
            using var doc = JsonDocument.Parse(json);
            
            if (doc.RootElement.GetProperty("status").GetString() == "success" &&
                doc.RootElement.TryGetProperty("countryCode", out var codeEl))
            {
                countryCode = codeEl.GetString()?.ToLowerInvariant();
            }
        }
        catch
        {
            // Якщо помилка мережі/ліміту — повертаємо null, але НЕ записуємо його в постійний кеш,
            // щоб дати програмі шанс отримати прапор при наступному оновленні.
            return null;
        }
        finally
        {
            // Після завершення прибираємо IP зі списку активних запитів
            _activeRequests.TryRemove(remoteAddress, out _);
        }

        // Якщо країну успішно визначено — записуємо в пам'ять та зберігаємо на диск
        if (countryCode != null)
        {
            _cache[remoteAddress] = countryCode;
            SaveCacheToFile();
        }

        return countryCode;
    }

    private static bool IsPrivateOrLoopback(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        var b = ip.GetAddressBytes();
        if (b.Length == 4)
        {
            return b[0] == 10
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254);
        }

        return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal;
    }

    private ConcurrentDictionary<string, string> LoadCacheFromFile()
    {
        try
        {
            if (File.Exists(CacheFilePath))
            {
                var json = File.ReadAllText(CacheFilePath);
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (dict != null)
                {
                    return new ConcurrentDictionary<string, string>(dict);
                }
            }
        }
        catch
        {
            // Якщо файл пошкоджений, ігноруємо помилку й створюємо новий порожній кеш
        }
        return new ConcurrentDictionary<string, string>();
    }

    private void SaveCacheToFile()
    {
        lock (_fileLock) // Запобігаємо конфліктам запису у файл з різних потоків
        {
            try
            {
                var json = JsonSerializer.Serialize(_cache);
                File.WriteAllText(CacheFilePath, json);
            }
            catch
            {
                // Оберігаємо програму від вильоту, якщо файл тимчасово заблоковано системою
            }
        }
    }
}