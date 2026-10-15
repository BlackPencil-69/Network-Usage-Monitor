using System;
using System.Windows;
using NetworkUsageMonitor.Tray;

namespace NetworkUsageMonitor.UI;

public partial class MainFlyout : Window
{
    private DateTimeOffset _lastDeactivatedUtc = DateTimeOffset.MinValue;

    public MainFlyout(FlyoutViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    public void ShowNear(MonitorGeometry geometry)
    {
        var workArea = geometry.WorkAreaDips;
        var iconRect = geometry.IconRectDips;

        double left = workArea.Right - Width + 4;

        if (iconRect.Right < workArea.Left + 100)
        {
            left = workArea.Left - 4;
        }

        left = Math.Max(workArea.Left, Math.Min(left, workArea.Right - Width + 12));

        double top;
        bool taskbarOnBottom = iconRect.Top > workArea.Top + workArea.Height / 2;

        if (taskbarOnBottom)
        {
            top = workArea.Bottom - Height + 4;
        }
        else
        {
            top = workArea.Top - 4;
        }

        Left = left;
        Top = top;
        _lastDeactivatedUtc = DateTimeOffset.MinValue;

        Show();
        Activate();
        Focus();
    }

    public bool WasRecentlyDeactivated(int thresholdMs = 250) =>
        (DateTimeOffset.UtcNow - _lastDeactivatedUtc).TotalMilliseconds < thresholdMs;

    private void OnDeactivated(object? sender, EventArgs e)
    {
        _lastDeactivatedUtc = DateTimeOffset.UtcNow;
        Hide();
    }
}