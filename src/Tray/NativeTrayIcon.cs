using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Drawing = System.Drawing;

namespace NetworkUsageMonitor.Tray;

/// <summary>
/// Tray-icon rect and containing-monitor work area, both already converted
/// to WPF DIPs using that monitor's own DPI scale. Consumers (MainFlyout)
/// should never need to touch SystemParameters.WorkArea or do their own
/// DPI math on top of this.
/// </summary>
public readonly record struct MonitorGeometry(Rect IconRectDips, Rect WorkAreaDips);

public sealed class NativeTrayIcon : IDisposable
{
    private const int IconId = 1;
    private const int WmTrayIcon = 0x8000 + 1;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonUp = 0x0205;
    private const int WmContextMenu = 0x007B;
    private const int WmNull = 0x0000;

    private const uint NimAdd = 0x00000000;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;

    private const uint MonitorDefaultToNearest = 2;
    private const int MdtEffectiveDpi = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public uint uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public Guid guidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int cbSize;
        public Win32Rect rcMonitor;
        public Win32Rect rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NotifyIconData data);

    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out Win32Rect rect);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref Win32Rect lprc, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    private HwndSource? _hwndSource;
    private NotifyIconData _iconData;
    private bool _added;

    public event Action? LeftClicked;
    public event Action? MenuRequested;

    public void Initialize(Drawing.Icon icon, string tooltip)
    {
        var parameters = new HwndSourceParameters("NetworkUsageMonitorTrayHost")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = IntPtr.Zero // Standard hidden window, so ContextMenu focusing behaves correctly.
        };

        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(WndProc);

        _iconData = new NotifyIconData
        {
            cbSize = Marshal.SizeOf<NotifyIconData>(),
            hWnd = _hwndSource.Handle,
            uID = IconId,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = WmTrayIcon,
            hIcon = icon.Handle,
            szTip = tooltip
        };

        _added = Shell_NotifyIcon(NimAdd, ref _iconData);
    }

    private DateTime _lastMenuTrigger = DateTime.MinValue;
    private static readonly TimeSpan MenuTriggerDedupWindow = TimeSpan.FromMilliseconds(250);

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmTrayIcon)
        {
            return IntPtr.Zero;
        }

        var mouseMessage = (int)lParam.ToInt64();
        if (mouseMessage == WmLButtonUp)
        {
            LeftClicked?.Invoke();
            handled = true;
        }
        else if (mouseMessage == WmRButtonUp || mouseMessage == WmContextMenu)
        {
            var now = DateTime.UtcNow;
            if (now - _lastMenuTrigger > MenuTriggerDedupWindow)
            {
                _lastMenuTrigger = now;
                SetForegroundWindow(_hwndSource!.Handle);
                MenuRequested?.Invoke();
            }
            handled = true;
        }

        return IntPtr.Zero;
    }

    public void NudgeMessageLoop()
    {
        if (_hwndSource is not null)
        {
            PostMessage(_hwndSource.Handle, WmNull, IntPtr.Zero, IntPtr.Zero);
        }
    }

    /// <summary>
    /// Returns the tray icon's screen rect and its monitor's work area, both
    /// converted to DIPs using that monitor's own DPI.
    ///
    /// Shell_NotifyIconGetRect always reports physical screen pixels,
    /// regardless of the calling process's DPI-awareness mode. Treating that
    /// rect as if it were already in WPF's logical units (as a naive
    /// implementation would) is exactly what makes a flyout drift sideways
    /// on anything other than 100% scaling, and get placed on the wrong
    /// monitor's work area entirely in mixed-DPI multi-monitor setups. Here
    /// we find the monitor that actually contains the icon via
    /// MonitorFromRect, read its real DPI via GetDpiForMonitor, and divide
    /// both the icon rect and that monitor's work area by the same scale
    /// factor before handing them back - so everything downstream is in one
    /// consistent, correct coordinate space.
    /// </summary>
    public MonitorGeometry GetTrayGeometry()
    {
        Win32Rect iconRectPhysical = new() { Left = 0, Top = 0, Right = 32, Bottom = 32 };
        bool haveIconRect = false;

        if (_hwndSource is not null)
        {
            var identifier = new NotifyIconIdentifier
            {
                cbSize = Marshal.SizeOf<NotifyIconIdentifier>(),
                hWnd = _hwndSource.Handle,
                uID = IconId
            };

            haveIconRect = Shell_NotifyIconGetRect(ref identifier, out iconRectPhysical) == 0;
        }

        var monitor = MonitorFromRect(ref iconRectPhysical, MonitorDefaultToNearest);

        var monitorInfo = new MonitorInfoEx { cbSize = Marshal.SizeOf<MonitorInfoEx>() };
        bool haveMonitorInfo = monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref monitorInfo);

        double scale = 1.0;
        if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpiX, out _) == 0 && dpiX > 0)
        {
            scale = dpiX / 96.0;
        }

        Rect ToDips(Win32Rect r) =>
            new(r.Left / scale, r.Top / scale, (r.Right - r.Left) / scale, (r.Bottom - r.Top) / scale);

        Rect workAreaDips;
        if (haveMonitorInfo)
        {
            workAreaDips = ToDips(monitorInfo.rcWork);
        }
        else
        {
            var fallback = SystemParameters.WorkArea;
            workAreaDips = new Rect(fallback.Left, fallback.Top, fallback.Width, fallback.Height);
        }

        var iconRectDips = haveIconRect
            ? ToDips(iconRectPhysical)
            : new Rect(workAreaDips.Right - 40, workAreaDips.Bottom - 40, 32, 32);

        return new MonitorGeometry(iconRectDips, workAreaDips);
    }

    public void Dispose()
    {
        if (_added)
        {
            Shell_NotifyIcon(NimDelete, ref _iconData);
        }

        _hwndSource?.Dispose();
    }
}
