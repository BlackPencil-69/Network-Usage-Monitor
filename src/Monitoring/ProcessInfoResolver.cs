using System.IO;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace NetworkUsageMonitor.Monitoring;

using NetworkUsageMonitor.Models;

public sealed class ProcessInfoResolver
{
    private readonly ConcurrentDictionary<int, ResolvedProcessInfo> _cache = new();
    private readonly ConcurrentDictionary<int, DateTimeOffset> _lastSeen = new();

    public ResolvedProcessInfo Resolve(int pid)
    {
        _lastSeen[pid] = DateTimeOffset.UtcNow;

        if (_cache.TryGetValue(pid, out var cached))
        {
            return cached;
        }

        var info = ResolveUncached(pid);
        _cache[pid] = info;
        return info;
    }

    private static ResolvedProcessInfo ResolveUncached(int pid)
    {
        string processName = "Unknown";
        string? path = null;

        try
        {
            using var process = Process.GetProcessById(pid);
            processName = process.ProcessName;

            try
            {
                path = process.MainModule?.FileName;
            }
            catch
            {
                // Access to protected/system processes (PID 4, some services) can throw;
                // fall back to the process name only.
            }
        }
        catch
        {
            processName = pid == 4 ? "System" : $"pid-{pid}";
        }

        var displayName = TryGetFriendlyName(path) ?? HumanizeProcessName(processName);
        var groupKey = path is not null ? path.ToLowerInvariant() : processName.ToLowerInvariant();

        return new ResolvedProcessInfo
        {
            Pid = pid,
            ProcessName = processName,
            GroupKey = groupKey,
            DisplayName = displayName,
            ExecutablePath = path
        };
    }

    private static string? TryGetFriendlyName(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var candidate = info.FileDescription;
            return string.IsNullOrWhiteSpace(candidate) ? null : candidate.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static string HumanizeProcessName(string processName)
    {
        return processName.Length == 0 ? "Unknown" : char.ToUpperInvariant(processName[0]) + processName[1..];
    }

    public void EvictStale(TimeSpan olderThan)
    {
        var cutoff = DateTimeOffset.UtcNow - olderThan;
        foreach (var (pid, lastSeen) in _lastSeen)
        {
            if (lastSeen < cutoff)
            {
                _lastSeen.TryRemove(pid, out _);
                _cache.TryRemove(pid, out _);
            }
        }
    }
}
