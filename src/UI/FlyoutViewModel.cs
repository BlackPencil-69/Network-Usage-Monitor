using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;

using NetworkUsageMonitor.Dashboard;
using NetworkUsageMonitor.Monitoring;

namespace NetworkUsageMonitor.UI;

public sealed class FlyoutViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan RemovalGrace = TimeSpan.FromSeconds(6);

    private readonly UsageAggregator _aggregator;
    private readonly DashboardServer _dashboardServer;
    private readonly Dictionary<string, AppGroupViewModel> _groupsByKey = new();

    private long _totalReceivedPerSecond;
    private long _totalSentPerSecond;

    public ObservableCollection<AppGroupViewModel> Groups { get; } = new();

    public ICommand OpenStatisticsCommand { get; }

    /// <summary>Total download speed across all active processes, bytes per second.</summary>
    public long TotalReceivedPerSecond
    {
        get => _totalReceivedPerSecond;
        private set => SetField(ref _totalReceivedPerSecond, value);
    }

    /// <summary>Total upload speed across all active processes, bytes per second.</summary>
    public long TotalSentPerSecond
    {
        get => _totalSentPerSecond;
        private set => SetField(ref _totalSentPerSecond, value);
    }

    public FlyoutViewModel(UsageAggregator aggregator, DashboardServer dashboardServer)
    {
        _aggregator = aggregator;
        _dashboardServer = dashboardServer;

        OpenStatisticsCommand = new RelayCommand(_ => _dashboardServer.OpenInBrowser());

        _aggregator.LiveSampleReady += OnLiveSample;
    }

    private void OnLiveSample(IReadOnlyList<LiveProcessSpeed> speeds)
    {
        Application.Current?.Dispatcher.BeginInvoke(() => Update(speeds));
    }

    private void Update(IReadOnlyList<LiveProcessSpeed> speeds)
    {
        // Totals come straight from the incoming sample; an empty sample resets them to 0.
        long totalReceived = 0;
        long totalSent = 0;
        foreach (var speed in speeds)
        {
            totalReceived += speed.BytesReceivedPerSecond;
            totalSent += speed.BytesSentPerSecond;
        }

        TotalReceivedPerSecond = totalReceived;
        TotalSentPerSecond = totalSent;

        var byGroup = speeds.GroupBy(s => s.Process.GroupKey).ToList();
        var seenKeys = new HashSet<string>();

        foreach (var group in byGroup)
        {
            seenKeys.Add(group.Key);
            if (!_groupsByKey.TryGetValue(group.Key, out var vm))
            {
                var first = group.First().Process;
                vm = new AppGroupViewModel(group.Key, first.DisplayName, first.ExecutablePath);
                _groupsByKey[group.Key] = vm;
                Groups.Add(vm);
            }

            vm.UpdateFrom(group);
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var key in _groupsByKey.Keys.ToList())
        {
            if (seenKeys.Contains(key))
            {
                continue;
            }

            var vm = _groupsByKey[key];
            vm.Decay();

            if (now - vm.LastActive > RemovalGrace)
            {
                _groupsByKey.Remove(key);
                Groups.Remove(vm);
            }
        }

        Resort();
    }

    private void Resort()
    {
        var ordered = Groups.OrderByDescending(g => g.TotalPerSecond).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            var currentIndex = Groups.IndexOf(ordered[i]);
            if (currentIndex != i)
            {
                Groups.Move(currentIndex, i);
            }
        }
    }

    public void Dispose()
    {
        _aggregator.LiveSampleReady -= OnLiveSample;
    }
}