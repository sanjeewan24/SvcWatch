# SvcWatch — Svchost Network Monitor

> **Take back control of what Windows runs behind your back.**

SvcWatch is a lightweight, native Windows desktop application that maps `svchost.exe` processes to their real underlying services, shows you which ones are making active network connections, and lets you permanently disable them — with a background enforcer that re-disables any service that tries to restart itself.

![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-blue)
![Framework](https://img.shields.io/badge/.NET-8.0-purple)
![License](https://img.shields.io/badge/license-MIT-green)
![Version](https://img.shields.io/badge/version-1.0.0-cyan)

---

## Features

- **Live Network Mapping** — Resolves every `svchost.exe` PID to its real Windows service name and display name in real time via WMI and `iphlpapi.dll`
- **TCP & UDP Monitoring** — Shows active connections with remote IP, port, protocol, and local port
- **One-Click Disable** — Stops a service and sets its startup type to `Disabled` via `ChangeServiceConfig` (Win32 API)
- **Background Enforcer** — Runs silently in the system tray, polling every 10 seconds; if a disabled service restarts (e.g. after a Windows Update), it is automatically re-disabled
- **Toast Notifications** — Native Windows toast fires on breach detection with the offending service name
- **Persistent State** — All enforced services and logs are stored locally in a LiteDB embedded database
- **Per-Service Logs** — Expandable log panel per service showing timestamps, action types, and error details
- **Run at Startup** — Registers via Windows Task Scheduler with `HighestAvailable` elevation (UAC-safe)
- **System Tray** — Minimizes to tray; double-click to restore; right-click for full context menu

---

## Screenshots

> Dashboard showing 7 active svchost network connections with live enforcer status.

---

## Requirements

| Requirement | Detail |
|---|---|
| OS | Windows 10 (1903+) or Windows 11 |
| Runtime | [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8) (x64) |
| Privileges | Administrator (UAC elevation required) |

---

## Build from Source

```bash
# Clone the repo
git clone https://github.com/sanjeewan24/SvcWatch.git
cd SvcWatch

# Restore dependencies
dotnet restore

# Build release
dotnet build -r win-x64 -c Release
```

The executable will be at:
```
bin\Release\net8.0-windows10.0.19041.0\win-x64\SvchostMonitor.exe
```

> **Always run as Administrator** — right-click → Run as administrator, or the app will fail to read TCP/UDP tables and control services.

---

## Usage

| Action | How |
|---|---|
| Disable a service | Click the **ACTIVE** toggle in the Dashboard — it stops and disables the service |
| Re-enable monitoring only | Click the **DISABLED** toggle — removes it from the enforcer watchlist |
| Manual rescan | Click **⟳ REFRESH** or tray → **Run Diagnostics Now** |
| Pause enforcer | Header toggle or tray → **Pause Enforcement** |
| Run at startup | Tray → **Run at Windows Startup** (uses Task Scheduler) |
| View logs | Switch to the **LOGS** tab — expand any service for full history |
| Exit completely | Tray → **Exit Completely** (closing the window hides to tray) |

---

## Architecture

```
SvchostMonitor/
├── App.xaml / App.xaml.cs          # Startup, tray icon (WinForms NotifyIcon), Task Scheduler
├── MainWindow.xaml / .cs           # Single-page WPF UI (Dashboard + Logs tabs)
├── ViewModelsAndModels.cs          # MVVM (CommunityToolkit), LiteDB models, enums
├── SvchostEngine.cs                # P/Invoke (iphlpapi + advapi32), WMI, ServiceController,
│                                   # NetworkMapper, ServiceControlManager, DatabaseHelper,
│                                   # BackgroundEnforcer (PeriodicTimer)
├── SvchostMonitor.csproj           # .NET 8 WPF project
└── app.manifest                    # requireAdministrator UAC manifest
```

**Key dependencies:**

| Package | Purpose |
|---|---|
| `CommunityToolkit.Mvvm` | Source-generated MVVM bindings |
| `LiteDB` | Embedded NoSQL local storage (`%LocalAppData%\SvchostMonitor\Data.db`) |
| `Microsoft.Toolkit.Uwp.Notifications` | Native Windows toast notifications |
| `System.Management` | WMI queries (PID → Service Name mapping) |
| `System.ServiceProcess.ServiceController` | Service stop/start control |

---

## Data & Privacy

- All data is stored **locally only** at `%LocalAppData%\SvchostMonitor\Data.db`
- No telemetry, no network calls, no analytics
- Logs auto-prune at 500 entries per service

---

## License

MIT © [sanjeewan24](https://github.com/sanjeewan24)
