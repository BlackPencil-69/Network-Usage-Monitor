using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NetworkUsageMonitor.UI;

public static class ProcessIconCache
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? GetIcon(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        return Cache.GetOrAdd(executablePath, ExtractIcon);
    }

    private static ImageSource? ExtractIcon(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var icon = Icon.ExtractAssociatedIcon(path);
            if (icon is null)
            {
                return null;
            }

            var bitmapSource = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            bitmapSource.Freeze();
            return bitmapSource;
        }
        catch
        {
            // Locked files, missing permissions, or exotic PE formats can all throw here;
            // callers fall back to the initials placeholder when this returns null.
            return null;
        }
    }
}
