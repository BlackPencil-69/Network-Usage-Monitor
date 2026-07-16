using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;

using NetworkUsageMonitor.Monitoring;

namespace NetworkUsageMonitor.UI;

public sealed class ProcessItemViewModel : ObservableObject
{
    public int Pid { get; init; }
    public string? ExecutablePath { get; init; }

    private long _sent;
    public long BytesSentPerSecond
    {
        get => _sent;
        set => SetField(ref _sent, value);
    }

    private long _received;
    public long BytesReceivedPerSecond
    {
        get => _received;
        set => SetField(ref _received, value);
    }
}

public sealed class AppGroupViewModel : ObservableObject
{
    public string GroupKey { get; }
    public string DisplayName { get; }
    public string? ExecutablePath { get; }
    public string Initials { get; }
    public ImageSource? IconSource { get; }

    public ObservableCollection<ProcessItemViewModel> Children { get; } = new();

    private readonly Dictionary<int, ProcessItemViewModel> _childrenByPid = new();

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }

    private long _totalSent;
    public long TotalSentPerSecond
    {
        get => _totalSent;
        private set
        {
            if (SetField(ref _totalSent, value))
            {
                RaisePropertyChanged(nameof(TotalPerSecond));
            }
        }
    }

    private long _totalReceived;
    public long TotalReceivedPerSecond
    {
        get => _totalReceived;
        private set
        {
            if (SetField(ref _totalReceived, value))
            {
                RaisePropertyChanged(nameof(TotalPerSecond));
            }
        }
    }

    public long TotalPerSecond => TotalSentPerSecond + TotalReceivedPerSecond;

    public DateTimeOffset LastActive { get; private set; } = DateTimeOffset.UtcNow;

    public ICommand ToggleExpandCommand { get; }

    public AppGroupViewModel(string groupKey, string displayName, string? executablePath)
    {
        GroupKey = groupKey;
        DisplayName = displayName;
        ExecutablePath = executablePath;
        Initials = BuildInitials(displayName);
        IconSource = ProcessIconCache.GetIcon(executablePath);
        ToggleExpandCommand = new RelayCommand(_ => IsExpanded = !IsExpanded);
    }

    private static string BuildInitials(string displayName)
    {
        var trimmed = displayName.Trim();
        if (trimmed.Length == 0)
        {
            return "?";
        }

        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            return string.Concat(parts[0][0], parts[1][0]).ToUpperInvariant();
        }

        return trimmed.Length >= 2 ? trimmed[..2].ToUpperInvariant() : trimmed.ToUpperInvariant();
    }

    public void UpdateFrom(IEnumerable<LiveProcessSpeed> speeds)
    {
        long sentSum = 0, receivedSum = 0;
        var seenPids = new HashSet<int>();

        foreach (var speed in speeds)
        {
            seenPids.Add(speed.Process.Pid);
            sentSum += speed.BytesSentPerSecond;
            receivedSum += speed.BytesReceivedPerSecond;

            if (!_childrenByPid.TryGetValue(speed.Process.Pid, out var child))
            {
                child = new ProcessItemViewModel
                {
                    Pid = speed.Process.Pid,
                    ExecutablePath = speed.Process.ExecutablePath
                };
                _childrenByPid[speed.Process.Pid] = child;
                Children.Add(child);
            }

            child.BytesSentPerSecond = speed.BytesSentPerSecond;
            child.BytesReceivedPerSecond = speed.BytesReceivedPerSecond;
        }

        foreach (var staleKey in _childrenByPid.Keys.Where(k => !seenPids.Contains(k)).ToList())
        {
            if (_childrenByPid.TryGetValue(staleKey, out var stale))
            {
                Children.Remove(stale);
            }
            _childrenByPid.Remove(staleKey);
        }

        TotalSentPerSecond = sentSum;
        TotalReceivedPerSecond = receivedSum;
        LastActive = DateTimeOffset.UtcNow;
    }

    public void Decay()
    {
        TotalSentPerSecond = 0;
        TotalReceivedPerSecond = 0;
    }
}
