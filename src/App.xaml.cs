using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
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
    private const string SingleInstanceMutexName = @"Local\NetworkUsageMonitor_SingleInstance_Mutex";
    private static readonly object ErrorLogLock = new();

    private Mutex? _instanceMutex;
    private bool _ownsMutex;
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
    private Drawing.Icon? _trayDrawingIcon;
    private ContextMenu? _trayMenu;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out _ownsMutex);
        if (!_ownsMutex)
        {
            MessageBox.Show(
                "Network Usage Monitor is already running in the system tray.",
                "Network Usage Monitor",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            Shutdown();
            return;
        }

        _settingsStore = new SettingsStore();

        RegisterGlobalExceptionHandlers();

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

        try
        {
            _repository = new SqliteUsageRepository(_settingsStore.DataDirectory);
            var resolver = new ProcessInfoResolver();
            _adapterResolver = new NetworkAdapterResolver();
            _monitor = new EtwNetworkMonitor(_adapterResolver);
            _geoIpResolver = new GeoIpResolver(_settingsStore.DataDirectory);
            _aggregator = new UsageAggregator(_monitor, resolver, _repository, _geoIpResolver);
            _dashboardServer = new DashboardServer(
                _repository,
                _aggregator,
                _settingsStore,
                _geoIpResolver,
                _settingsStore.Current.DashboardPort);

            _aggregator.Start();
            _dashboardServer.Start();

            _flyoutViewModel = new FlyoutViewModel(_aggregator, _dashboardServer);
            _flyout = new MainFlyout(_flyoutViewModel);

            _trayIcon = new NativeTrayIcon();
            _trayDrawingIcon = LoadTrayIcon();
            _trayIcon.Initialize(_trayDrawingIcon, "Network Usage Monitor");
            _trayIcon.LeftClicked += OnTrayLeftClicked;
            _trayIcon.MenuRequested += OnTrayMenuRequested;

            _trayMenu = new ContextMenu
            {
                Placement = PlacementMode.MousePoint
            };

            var statisticsItem = new MenuItem { Header = "Statistics" };
            statisticsItem.Click += (_, _) => _dashboardServer?.OpenInBrowser();
            _trayMenu.Items.Add(statisticsItem);

            var settingsItem = new MenuItem { Header = "Open settings file" };
            settingsItem.Click += (_, _) => OpenSettingsFile();
            _trayMenu.Items.Add(settingsItem);

            _trayMenu.Items.Add(new Separator());

            var exitItem = new MenuItem { Header = "Exit program" };
            exitItem.Click += (_, _) => Shutdown();
            _trayMenu.Items.Add(exitItem);

            _trayMenu.PlacementTarget = _flyout;
            _trayMenu.Closed += OnTrayMenuClosed;
        }
        catch (Exception ex)
        {
            LogUnhandledError("OnStartup", ex);

            MessageBox.Show(
                $"Failed to start Network Usage Monitor:\n\n{ex.Message}",
                "Startup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown();
        }
    }

    private void RegisterGlobalExceptionHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void UnregisterGlobalExceptionHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception
                        ?? new Exception($"Non-Exception object thrown: {e.ExceptionObject}");

        LogUnhandledError($"AppDomain.UnhandledException (IsTerminating={e.IsTerminating})", exception);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogUnhandledError("DispatcherUnhandledException", e.Exception);
        e.Handled = true;
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogUnhandledError("TaskScheduler.UnobservedTaskException", e.Exception);
        e.SetObserved();
    }

    private void LogUnhandledError(string source, Exception? ex)
    {
        try
        {
            var dataDir = _settingsStore?.DataDirectory
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NetworkUsageMonitor");

            Directory.CreateDirectory(dataDir);

            var errorLogPath = Path.Combine(dataDir, "error.log");
            var logEntry =
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] [{source}]{Environment.NewLine}" +
                $"{ex}{Environment.NewLine}" +
                new string('-', 80) + Environment.NewLine;

            Trace.WriteLine(logEntry);

            lock (ErrorLogLock)
            {
                File.AppendAllText(errorLogPath, logEntry);
            }
        }
        catch
        {
        }
    }

    private static Drawing.Icon LoadTrayIcon()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("NetworkUsageMonitor.UI.app.ico");

        return stream is not null
            ? new Drawing.Icon(stream)
            : new Drawing.Icon(Drawing.SystemIcons.Application, Drawing.SystemIcons.Application.Size);
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

        if (_flyout.WasRecentlyDeactivated())
        {
            return;
        }

        _flyout.ShowNear(_trayIcon.GetTrayGeometry());
    }

    private void OnTrayMenuRequested()
    {
        if (_flyout is null || _trayMenu is null)
        {
            return;
        }

        _trayMenu.PlacementTarget = _flyout;
        _trayMenu.IsOpen = true;
    }

    private void OnTrayMenuClosed(object sender, RoutedEventArgs e)
    {
        _trayIcon?.NudgeMessageLoop();
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
        try
        {
            if (_trayMenu is not null)
            {
                _trayMenu.Closed -= OnTrayMenuClosed;
                _trayMenu.IsOpen = false;
                _trayMenu.PlacementTarget = null;
                _trayMenu = null;
            }

            if (_trayIcon is not null)
            {
                _trayIcon.LeftClicked -= OnTrayLeftClicked;
                _trayIcon.MenuRequested -= OnTrayMenuRequested;
                _trayIcon.Dispose();
                _trayIcon = null;
            }

            _trayDrawingIcon?.Dispose();
            _trayDrawingIcon = null;

            _flyoutViewModel?.Dispose();
            _dashboardServer?.Dispose();
            _aggregator?.Dispose();
            _adapterResolver?.Dispose();
            _repository?.Dispose();
        }
        catch (Exception ex)
        {
            LogUnhandledError("OnExit", ex);
        }
        finally
        {
            UnregisterGlobalExceptionHandlers();

            if (_instanceMutex is not null)
            {
                if (_ownsMutex)
                {
                    try
                    {
                        _instanceMutex.ReleaseMutex();
                    }
                    catch
                    {
                    }
                }

                _instanceMutex.Dispose();
                _instanceMutex = null;
            }
        }

        base.OnExit(e);
    }
}