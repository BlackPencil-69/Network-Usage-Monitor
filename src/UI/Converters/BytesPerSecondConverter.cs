using System.Globalization;
using System.Windows.Data;

namespace NetworkUsageMonitor.UI.Converters;

public sealed class BytesPerSecondConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var bytesPerSecond = value switch
        {
            long l => l,
            int i => i,
            double d => (long)d,
            _ => 0L
        };

        return Format(bytesPerSecond);
    }

    public static string Format(long bytesPerSecond)
    {
        if (bytesPerSecond < 1024)
        {
            return $"{bytesPerSecond} B/s";
        }

        string[] units = { "KB/s", "MB/s", "GB/s" };
        double value = bytesPerSecond;
        int unitIndex = -1;

        do
        {
            value /= 1024.0;
            unitIndex++;
        } while (value >= 1024.0 && unitIndex < units.Length - 1);

        return $"{value.ToString(value >= 10 ? "F0" : "F1", CultureInfo.InvariantCulture)} {units[unitIndex]}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
