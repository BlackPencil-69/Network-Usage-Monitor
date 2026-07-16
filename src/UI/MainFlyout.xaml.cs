using System.Windows;

using NetworkUsageMonitor.Tray;

namespace NetworkUsageMonitor.UI;

public partial class MainFlyout : Window
{
    public MainFlyout(FlyoutViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>
    /// Positions the flyout next to the tray icon using geometry that has
    /// already been converted to DIPs for the specific monitor the icon is
    /// on (see NativeTrayIcon.GetTrayGeometry). Both the icon rect and the
    /// work area come from that same monitor, so there's no mixing of
    /// physical pixels from one DPI space with logical coordinates from
    /// another - which was the root cause of the flyout drifting sideways
    /// on scaled or multi-monitor setups.
    /// </summary>
    public void ShowNear(MonitorGeometry geometry)
    {
        var workArea = geometry.WorkAreaDips;
        var iconRect = geometry.IconRectDips;

        double left = iconRect.Left + iconRect.Width / 2 - Width / 2;
        double top;

        bool taskbarOnBottom = iconRect.Top > workArea.Top + workArea.Height / 2;
        if (taskbarOnBottom)
        {
            top = workArea.Bottom - Height;
        }
        else
        {
            top = workArea.Top;
        }

        left = Math.Max(workArea.Left + 8, Math.Min(left, workArea.Right - Width - 8));

        Left = left;
        Top = top;

        Show();
        Activate();
        Focus();
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        Hide();
    }
}
