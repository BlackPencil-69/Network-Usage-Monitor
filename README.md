# 📡 Network Usage Monitor

A lightweight, self-contained Windows tray utility (C#, .NET 8, WPF) that shows **real-time per-application bandwidth usage**, tracks **traffic by country** with a fully **offline GeoIP database**, separates traffic by network type (home/office vs. mobile hotspot), enforces mobile data caps with toast notifications, and ships a **local, fully offline analytics dashboard**.

> Everything runs on your machine. No cloud, no accounts, no external API calls during packet analysis.

---

## 📌 Table of Contents

- [Features](#-features)
- [Requirements](#-requirements)
- [Quick Start (pre-built .exe)](#-quick-start-pre-built-exe)
- [Run automatically at Windows startup (Task Scheduler)](#-run-automatically-at-windows-startup-task-scheduler)
- [Build & run (development)](#-build--run-development)
- [Publish a single, self-contained .exe](#-publish-a-single-self-contained-exe)
- [Dashboard API](#-dashboard-api)
- [Architecture](#-architecture)
- [How it works](#-how-it-works)
- [Data storage](#-data-storage)
- [Intentionally simplified](#-intentionally-simplified-and-how-to-extend-it)
- [Troubleshooting](#-troubleshooting)

---

## ✨ Features

### 🔵 Real-time monitoring (tray flyout)

- **Per-process speeds** via kernel ETW events — grouped by application, with expandable child PIDs.
- **Total speed badge** in the flyout header showing live system-wide download (↓) and upload (↑).
- **Both directions per PID**: expanded child processes show download (↓) and upload (↑) speeds.
- **Smart icon cache** (`ProcessIconCache`): high-quality system icons for `.exe` files, with colored initials as a fallback when the file is missing or inaccessible.
- **DPI-aware positioning**: the flyout snaps to the bottom-right corner of the work area (next to the clock) using `MonitorFromRect` / `GetDpiForMonitor` — correct at 125%/150% scaling and on multi-monitor setups with different DPI.
- **Dark custom scrollbar** and consistent row alignment (system services like `System` line up with all other apps).
- **Sleep/Hibernate resilient**: the ETW session is restarted automatically after resume (`PowerModeChanged`).
- **Deadlock-safe UI updates**: metrics are pushed from the background thread with asynchronous `BeginInvoke`.

### 🌍 Offline GeoIP & Traffic by Country

- **100% local country resolution** (`GeoIpResolver`): public IPv4 and IPv4-mapped IPv6 addresses are resolved to two-letter ISO country codes (`ua`, `us`, `de`, …) with **no HTTP requests** during packet analysis.
- **Zero-allocation lookups** in the hot ETW path: IP ranges are stored in aligned in-memory arrays and searched with `Array.BinarySearch` — no GC pressure.
- **Background database download/refresh**: `geoip-ipv4.csv` is fetched on a background thread with exponential retry delays and integrity validation before the file is replaced.
- **Service-traffic filtering**: private, loopback and multicast addresses go into a separate `local` category so they don't pollute external traffic statistics.
- **Traffic by Country card** on the dashboard:
  - **Top List** — flags (flagcdn.com), localized country names, relative-volume bars, separate Upload/Download metrics and active connection counts.
  - **World Map** — Canvas bubble map with color-coded intensity and country-code labels.
  - **Global country filter** — click any country (list or map) to switch the whole dashboard (charts, KPIs, app list) to that region only. A badge lets you reset the filter in one click.

### 📊 Web dashboard & statistics

- **Flexible time ranges**
  - Rolling: **24 Hours**, **7 Days**, **30 Days**, **Year** (365 days)
  - Calendar: **Today** (from 00:00 local), **This Week** (from Monday 00:00), **This Month**, **This Year**
  - **All-time** — the entire stored history
  - **Month picker** — any month that has data in the DB (`/api/months`), boundaries computed in local time
- **Zero-filling** of timelines: gaps (sleep, powered-off laptop) are filled with zeros and future time is trimmed, so there are no fake diagonal lines or artificial drops.
- **Compare Months** (`/api/compare`): pick any two months and get
  - KPI cards (Total, Download, Upload, daily average) with **±% delta badges** (`+X%` / `-X%` / `flat`),
  - a comparison table of top applications with exact byte differences and trend.
- **Interactive Canvas charts**
  - Line modes: **Overlay** (Upload and Download separately), **Download only**, **Upload only**, **Two Combined** (sum).
  - **Wheel zoom** centered on the cursor, **drag-to-zoom** rectangle selection, **double-click** to reset zoom.
  - Floating **tooltip** with exact Upload, Download, total and formatted time, plus a **crosshair** line.
  - **Weekly activity heatmap** — 7 days × 24 hours with automatic color normalization.
- **KPI strip**: Total Traffic (Upload / Download split), Daily Average (over real active days), Peak Activity (date + exact volume), Top Country / Active Apps.
- **Application list controls**: sort by Total, Download, Upload or Name, with a ↑ / ↓ direction toggle; switch between absolute bytes and percentage of total.
- **Application Details modal**
  - Process total, its share of overall system traffic, and its peak day.
  - Personal mini-chart on Canvas.
  - **Top countries** the process talked to.
  - **Live Connections** via `GetExtendedTcpTable` / `GetExtendedUdpTable`: protocol (TCP/UDP), destination country, IP, remote and local ports.
  - **Reveal in Explorer** (`explorer.exe /select,`) with safe argument passing.
- **Export** raw traffic history for the selected period as **.json** or **.csv** (special characters correctly escaped).
- **Light and dark themes**, saved automatically in `localStorage`.

### 🗄️ Database (SQLite) optimization & maintenance

- **Hourly rollup**: detailed 15-second rows older than 48 hours are compacted into hourly buckets in `usage_samples` and `country_samples`. A multi-month history can shrink from hundreds of megabytes to ~20 MB with no loss of retrospective accuracy.
- **Short-lived process aggregation**: junk processes that exit before their name can be resolved (`Pid-XXXX`) are merged into a single `Background / Short-lived` group instead of creating thousands of unique rows.
- **Scheduled maintenance** every 6 hours via a background timer, plus an extra check on batch write in case the PC was asleep.
- **Physical space reclamation**: `PRAGMA wal_checkpoint(TRUNCATE)` and `VACUUM` run automatically when more than 5000 rows were removed or aggregated.
- **Reliability settings**: `journal_mode=WAL`, `synchronous=NORMAL`, `busy_timeout=5000` — the web server can read while the writer is active without mutual locking.
- **Automatic schema migration**: data from the old `country_usage` table is moved into `country_samples` and obsolete tables are dropped inside a single transaction.

### 🔔 Other

- Separates **Home/Office** vs. **Mobile Hotspot** traffic.
- **80% / 100% data-cap toast alerts** for the mobile hotspot.
- Single self-contained `.exe` — nothing needs to be installed on the target machine.

---

## 🧰 Requirements

- Windows 10 (build 19041+) or Windows 11, **x64**
- **.NET 8 SDK** — only to build from source (the published app is self-contained)
- **Administrator rights at runtime** — see [Why Administrator?](#why-administrator)

---

## 🚀 Quick Start (pre-built .exe)

1. Download `NetworkUsageMonitor.exe` from the [Releases](../../releases) page.
2. Put it in a permanent folder, for example `C:\Tools\NetworkUsageMonitor\`.
   (Don't run it from *Downloads* or a temp folder if you plan to enable autostart.)
3. Double-click it and confirm the UAC prompt.
4. Find the icon in the system tray (click `^` if it's hidden). Click it to open the flyout; use the tray menu to open the dashboard.

---

## 🔁 Run automatically at Windows startup (Task Scheduler)

The app needs **Administrator rights** (kernel ETW sessions). Regular autostart methods — the `Startup` folder or the registry `Run` key — start programs *without* elevation, so Windows would either show a UAC prompt on every login or the app would fail to start. **Task Scheduler** can launch a program with the highest privileges *silently*, which is why it is the right tool here.

### Option A — Using the Task Scheduler GUI

1. Press **Win + R**, type `taskschd.msc`, press **Enter**.
2. In the right-hand **Actions** panel click **Create Task…** (not *Create Basic Task* — it lacks the elevation checkbox).
3. **General** tab:
   - **Name:** `NetworkUsageMonitor`
   - Select **Run only when user is logged on**.
   - ✅ Check **Run with highest privileges**. *(This is the key setting — it skips the UAC prompt.)*
   - **Configure for:** Windows 10 / Windows 11.
4. **Triggers** tab → **New…**
   - **Begin the task:** `At log on`
   - **Specific user:** your account.
   - ✅ **Delay task for:** `30 seconds` (lets the network stack and tray finish loading).
   - ✅ **Enabled** → **OK**.
5. **Actions** tab → **New…**
   - **Action:** `Start a program`
   - **Program/script:** browse to your exe, e.g. `C:\Tools\NetworkUsageMonitor\NetworkUsageMonitor.exe`
   - **Start in (optional):** the exe's folder, e.g. `C:\Tools\NetworkUsageMonitor\` (without quotes)
   - **OK**.
6. **Conditions** tab:
   - ❌ **Uncheck** *Start the task only if the computer is on AC power* (otherwise it won't start on battery).
   - ❌ **Uncheck** *Stop if the computer switches to battery power*.
7. **Settings** tab:
   - ❌ **Uncheck** *Stop the task if it runs longer than* (the default 3 days would kill the monitor).
   - ✅ *Allow task to be run on demand*.
   - **If the task is already running:** `Do not start a new instance`.
8. Click **OK** (enter your Windows password if asked).
9. **Test it:** right-click the task in the list → **Run**. The tray icon should appear without any UAC prompt. Then sign out and back in (or reboot) to confirm autostart works.

### Option B — One command (Command Prompt as Administrator)

Open **Command Prompt as Administrator** and run (adjust the path):

```bat
schtasks /Create /TN "NetworkUsageMonitor" /TR "\"C:\Tools\NetworkUsageMonitor\NetworkUsageMonitor.exe\"" /SC ONLOGON /RL HIGHEST /DELAY 0000:30 /F
```

| Flag | Meaning |
|------|---------|
| `/TN` | Task name |
| `/TR` | Program to run (note the escaped quotes — they are needed if the path contains spaces) |
| `/SC ONLOGON` | Trigger: at user logon |
| `/RL HIGHEST` | Run with highest privileges (no UAC prompt) |
| `/DELAY 0000:30` | Wait 30 seconds after logon (`mmmm:ss`) |
| `/F` | Overwrite the task if it already exists |

> ⚠️ This command creates the task with default conditions, which on laptops includes *"start only on AC power"* and a 3-day execution limit. To fix both, use Option C or open the task in `taskschd.msc` and apply steps 6–7 above.

### Option C — PowerShell (Administrator), with laptop-friendly settings

```powershell
$exe     = "C:\Tools\NetworkUsageMonitor\NetworkUsageMonitor.exe"
$action  = New-ScheduledTaskAction -Execute $exe -WorkingDirectory (Split-Path $exe)
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$trigger.Delay = "PT30S"
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Highest
$settings  = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -MultipleInstances IgnoreNew

Register-ScheduledTask -TaskName "NetworkUsageMonitor" `
    -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force
```

### Managing the task

```bat
:: Run it now
schtasks /Run /TN "NetworkUsageMonitor"

:: Stop it
schtasks /End /TN "NetworkUsageMonitor"

:: Disable autostart (keeps the task)
schtasks /Change /TN "NetworkUsageMonitor" /DISABLE

:: Remove autostart completely
schtasks /Delete /TN "NetworkUsageMonitor" /F
```

### Updating the app later

If you replace the `.exe` **at the same path**, the task keeps working. If you move the exe, edit the task (**Actions** tab) or re-create it with the new path. Close the running instance from the tray first, otherwise the file will be locked.

### Autostart troubleshooting

| Problem | Fix |
|---------|-----|
| Nothing starts at logon | Check the task's **History** tab (enable it via *Actions → Enable All Tasks History*). Verify the exe path in the **Actions** tab. |
| UAC prompt appears every time | *Run with highest privileges* is not checked, or you used the Startup folder / registry instead of a scheduled task. |
| Doesn't start on battery | Uncheck the AC-power conditions (step 6). |
| Stops after a few days | Uncheck *Stop the task if it runs longer than…* (step 7). |
| Starts twice | Set *If the task is already running* → `Do not start a new instance`. |
| Tray icon missing after login | Increase the logon delay to 45–60 seconds. |

---

## 🛠️ Build & run (development)

```
cd src
dotnet restore
dotnet run
```

Visual Studio will also prompt for elevation automatically because of `app.manifest` (`requireAdministrator`). If you run from the terminal, start that terminal as Administrator.

## 📦 Publish a single, self-contained .exe

```
cd src
dotnet publish -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true
```

The result is `src\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\NetworkUsageMonitor.exe` — a single file with no external DLLs, config files, or asset folders. All these properties are already set in the `.csproj`, so a plain `dotnet publish -c Release -r win-x64` produces the same result.

---

## 🔌 Dashboard API

The dashboard is served by an `HttpListener` bound to `127.0.0.1` only (not reachable from other machines).

| Endpoint | Purpose |
|----------|---------|
| `/api/apps` | Per-application totals for the selected range (supports country filter) |
| `/api/timeline` | Time series for the main chart (zero-filled on the client) |
| `/api/heatmap` | Weekly 7×24 activity matrix |
| `/api/months` | List of months that have data in the DB |
| `/api/compare` | Comparison of two selected months |
| `/api/connections` | Live connections of a process (remote IP/port, protocol, country) |

---

## 🏗️ Architecture

```
App.xaml.cs               composition root: wires everything together, owns the app lifecycle
Tray/NativeTrayIcon.cs    Win32 Shell_NotifyIcon tray icon (no WinForms dependency)
UI/                       WPF flyout window (MainFlyout) + view models (MVVM, no external library)
Monitoring/
  EtwNetworkMonitor.cs        ETW consumer -> per-PID byte events (+ remote IP for GeoIP)
  GeoIpResolver.cs            offline IPv4 -> country code, binary search over in-memory ranges
  NetworkInterfaceClassifier  home/office vs. mobile hotspot vs. other
  ProcessInfoResolver         PID -> friendly name / metadata, with caching
  ProcessIconCache            process icons with colored-initials fallback
  UsageAggregator             raw byte events -> 1s live speeds + 15s DB batches
  NativeNetworkTables.cs      GetExtendedTcpTable/UdpTable for the live "connections" drill-down
Persistence/SqliteUsageRepository.cs   schema, migrations, rollup, VACUUM, aggregate queries
Notifications/DataCapNotifier.cs       80%/100% toast alerts for the mobile hotspot cap
Dashboard/
  DashboardServer.cs      HttpListener on 127.0.0.1, serves the embedded UI + JSON API
  wwwroot/                dashboard HTML/CSS/JS, embedded into the exe as resources
Settings/                 JSON settings under %AppData%\NetworkUsageMonitor
```

## ⚙️ How it works

### Per-process bandwidth

`GetExtendedTcpTable`/`GetExtendedUdpTable` only expose *which* process owns a connection, not how many bytes it has moved. To get real byte counts with low overhead, the app consumes the **Microsoft-Windows-Kernel-Network** ETW provider through `Microsoft.Diagnostics.Tracing.TraceEvent` (`EtwNetworkMonitor.cs`). It opens a kernel trace session with `KernelTraceEventParser.Keywords.NetworkTCPIP` and subscribes to `TcpIpSend`/`TcpIpRecv`/`TcpIpSendIPV6`/`TcpIpRecvIPV6`/`UdpIpSend`/`UdpIpRecv` events, each carrying the owning PID, transfer size and the remote address.

<a id="why-administrator"></a>
**Why Administrator?** Kernel ETW sessions are only available to elevated processes. `App.xaml.cs` checks this on startup and shows a message instead of silently failing; `app.manifest` also requests `requireAdministrator` so Windows elevates automatically.

`UsageAggregator` fans each raw event into two independent counters: one reset every second for the live tray speed, and one reset every 15 seconds and flushed to SQLite in a single transaction — low disk and CPU usage with 1-second UI responsiveness.

### Offline GeoIP

`GeoIpResolver` loads `geoip-ipv4.csv` into sorted, aligned arrays of range starts/ends/country codes. A lookup is a single `Array.BinarySearch` on the range start followed by a bounds check — no allocations, so it is safe to call directly from the ETW event handler. The database is downloaded and refreshed on a background thread; the new file replaces the old one only after it passes validation. Private, loopback and multicast addresses are short-circuited to `local`.

### Network interface detection

`NetworkInterfaceClassifier.cs` uses the WinRT connectivity APIs (`Windows.Networking.Connectivity.NetworkInformation`), available from the `net8.0-windows10.0.19041.0` target framework:

- A profile with `IsWwanConnectionProfile == true`, or an adapter with `IanaInterfaceType == 243` (cellular), is classified as **Mobile Hotspot** automatically (USB/Bluetooth tethering, cellular modems).
- Wi-Fi is read via `ConnectionProfile.WlanConnectionProfileDetails.GetConnectedSsid()`. Windows does not reliably flag "this Wi-Fi is a phone hotspot", so Wi-Fi tethering is classified as Mobile Hotspot only when its SSID is in the user-maintained allow-list (`Known hotspot SSIDs` in `settings.json`). If Windows reports the network as metered but the SSID isn't listed, the UI suggests adding it.
- Ethernet and everything else fall back to Home/Office or Other.

### Embedded dashboard assets

Everything under `Dashboard/wwwroot/` is marked `<EmbeddedResource Include="Dashboard\wwwroot\**\*" />`, so `dotnet publish` bakes it into the single `.exe`. `DashboardServer.cs` serves it from `Assembly.GetManifestResourceStream(...)` — no disk I/O and no external dependencies at runtime.

Charts (timeline, heatmap, world map) are small, dependency-free `<canvas>` renderers in `dashboard.js`, keeping the "100% offline, single exe" promise with zero vendor files. Country flags are the only external resource (`flagcdn.com` images); everything else works without internet.

### Data model

History is stored per **display name** (e.g. "Google Chrome"), not per PID — PIDs are recycled and meaningless across time, so multiple `chrome.exe` instances become one row. Country traffic is stored in `country_samples` by adapter, session and process. Old detailed rows are rolled up into hourly buckets (see [Database maintenance](#️-database-sqlite-optimization--maintenance)). The live PID breakdown comes from the in-memory sample stream, not from SQLite.

---

## 💾 Data storage

- `%AppData%\NetworkUsageMonitor\settings.json` — caps, SSID allow-list, category overrides
- `%AppData%\NetworkUsageMonitor\usage.db` — SQLite (WAL mode), indexed on timestamp, process name, and (interface, timestamp)
- `geoip-ipv4.csv` — offline country database (downloaded automatically in the background)

---

## 🧩 Intentionally simplified (and how to extend it)

- **Connection hostnames**: `/api/connections` returns raw remote IP/port pairs plus the country. Hostname labels would need reverse DNS (`Dns.GetHostEntryAsync`, cached) or an ASN database (e.g. MaxMind GeoLite2-ASN); neither is wired in.
- **IPv6 in the connection table**: `NativeNetworkTables.cs` covers IPv4 TCP/UDP only. Add the `AF_INET6` (`23`) variants and the `MIB_TCP6ROW_OWNER_PID` / `MIB_UDP6ROW_OWNER_PID` structs the same way if you need IPv6.
- **GeoIP is IPv4-only** (IPv4-mapped IPv6 is supported); native IPv6 addresses are not resolved to countries.
- **Settings UI**: there is no dedicated settings window yet — the tray menu's "Open settings file" opens `settings.json` directly. A small WPF dialog bound to `AppSettings` would be a natural next step.

---

## 🩺 Troubleshooting

| Problem | Solution |
|---------|----------|
| "Administrator rights required" message | Run the exe as Administrator, or use the [Task Scheduler autostart](#-run-automatically-at-windows-startup-task-scheduler). |
| Countries are empty / all `local` | Wait for the background download of `geoip-ipv4.csv` to finish (needs internet once). |
| No data after waking from sleep | The ETW session restarts automatically on resume; if it doesn't, restart the app. |
| Database file is large | Rollup and `VACUUM` run automatically every 6 hours; space is reclaimed when more than 5000 rows are compacted. |
| Dashboard doesn't open | Open it from the tray menu; make sure the app is running (check the tray). |

---

## 🤝 Contributing

Issues and pull requests are welcome. For larger changes please open an issue first to discuss what you'd like to change.
