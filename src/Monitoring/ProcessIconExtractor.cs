using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace NetworkUsageMonitor.Monitoring;

public static class ProcessIconExtractor
{
    private static readonly ConcurrentDictionary<string, string> Cache = new();

    // Цей рядок забезпечує сумісність із викликом від Claude в DashboardServer.cs
    public static string GetIconAsBase64(string? exePath) => GetPngDataUrl(exePath);

    public static string GetPngDataUrl(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            return string.Empty;

        return Cache.GetOrAdd(exePath, path =>
        {
            try
            {
                using var icon = Icon.ExtractAssociatedIcon(path);
                if (icon == null) return string.Empty;

                using var bitmap = icon.ToBitmap();
                using var ms = new MemoryStream();
                bitmap.Save(ms, ImageFormat.Png);
                
                return $"data:image/png;base64,{Convert.ToBase64String(ms.ToArray())}";
            }
            catch
            {
                return string.Empty;
            }
        });
    }
}