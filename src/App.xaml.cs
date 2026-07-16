using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Drawing = System.Drawing;

using NetworkUsageMonitor.Dashboard;
using NetworkUsageMonitor.Monitoring;
using NetworkUsageMonitor.Persistence;
using NetworkUsageMonitor.Settings;
using NetworkUsageMonitor.Tray;
using NetworkUsageMonitor.UI;

namespace NetworkUsageMonitor;

public partial class App : Application
{
    private SettingsStore? _settingsStore;
    private SqliteUsageRepository? _repository;
    private NetworkAdapterResolver? _adapterResolver;
    private EtwNetworkMonitor? _monitor;
    private UsageAggregator? _aggregator;
    private DashboardServer? _dashboardServer;
    private NativeTrayIcon? _trayIcon;
    private MainFlyout? _flyout;
    private FlyoutViewModel? _flyoutViewModel;
    private GeoIpResolver? _geoIpResolver;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!EtwNetworkMonitor.IsElevated())
        {
            MessageBox.Show(
                "Network Usage Monitor needs to run as Administrator to read per-process network traffic.\n\n" +
                "Please right-click the app and choose \"Run as administrator\".",
                "Administrator privileges required",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        _settingsStore = new SettingsStore();
        _repository = new SqliteUsageRepository(_settingsStore.DataDirectory);

        var resolver = new ProcessInfoResolver();
        _adapterResolver = new NetworkAdapterResolver();
        _monitor = new EtwNetworkMonitor(_adapterResolver);
        _aggregator = new UsageAggregator(_monitor, resolver, _repository);
        _geoIpResolver = new GeoIpResolver();
        _dashboardServer = new DashboardServer(_repository, _aggregator, _settingsStore, _geoIpResolver, _settingsStore.Current.DashboardPort);

        _aggregator.Start();
        _dashboardServer.Start();

        _flyoutViewModel = new FlyoutViewModel(_aggregator, _dashboardServer);
        _flyout = new MainFlyout(_flyoutViewModel);

        _trayIcon = new NativeTrayIcon();
        _trayIcon.Initialize(LoadTrayIcon(), "Network Usage Monitor");
        _trayIcon.LeftClicked += OnTrayLeftClicked;
        _trayIcon.MenuRequested += OnTrayMenuRequested;
    }

    private static Drawing.Icon LoadTrayIcon()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("NetworkUsageMonitor.UI.app.ico");
        return stream is not null ? new Drawing.Icon(stream) : Drawing.SystemIcons.Application;
    }

    private void OnTrayLeftClicked()
    {
        if (_flyout is null || _trayIcon is null)
        {
            return;
        }

        if (_flyout.IsVisible)
        {
            _flyout.Hide();
            return;
        }

        _flyout.ShowNear(_trayIcon.GetTrayGeometry());
    }

    private void OnTrayMenuRequested()
    {
        if (_flyout is null)
        {
            return;
        }

        var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };

        var statisticsItem = new MenuItem { Header = "Statistics" };
        statisticsItem.Click += (_, _) => _dashboardServer?.OpenInBrowser();
        menu.Items.Add(statisticsItem);

        var settingsItem = new MenuItem { Header = "Open settings file" };
        settingsItem.Click += (_, _) => OpenSettingsFile();
        menu.Items.Add(settingsItem);

        menu.Items.Add(new Separator());

        var exitItem = new MenuItem { Header = "Exit program" };
        exitItem.Click += (_, _) => Shutdown();
        menu.Items.Add(exitItem);

        menu.PlacementTarget = _flyout;
        menu.Closed += (_, _) => _trayIcon?.NudgeMessageLoop();
        menu.IsOpen = true;
    }

    private void OpenSettingsFile()
    {
        if (_settingsStore is null)
        {
            return;
        }

        _settingsStore.Save();
        Process.Start(new ProcessStartInfo(Path.Combine(_settingsStore.DataDirectory, "settings.json"))
        {
            UseShellExecute = true
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _flyoutViewModel?.Dispose();
        _dashboardServer?.Dispose();
        _aggregator?.Dispose();
        _adapterResolver?.Dispose();
        _repository?.Dispose();
        base.OnExit(e);
    }
}
