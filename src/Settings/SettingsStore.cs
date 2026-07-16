using System.IO;
using System.Text.Json;

namespace NetworkUsageMonitor.Settings;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly object _lock = new();

    public AppSettings Current { get; private set; }

    public SettingsStore()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NetworkUsageMonitor");
        Directory.CreateDirectory(root);
        _filePath = Path.Combine(root, "settings.json");
        Current = Load();
    }

    public string DataDirectory => Path.GetDirectoryName(_filePath)!;

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch (Exception)
        {
            // Corrupted settings file: fall back to defaults rather than crash on startup.
        }

        return new AppSettings();
    }

    public void Save()
    {
        lock (_lock)
        {
            var json = JsonSerializer.Serialize(Current, JsonOptions);
            var tempPath = _filePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Copy(tempPath, _filePath, overwrite: true);
            File.Delete(tempPath);
        }
    }
}
