using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiteDB;
using SvchostMonitor.Engine;
using SvchostMonitor.Models;

namespace SvchostMonitor.Models
{
    public enum EnforcedState { None, EnforceDisabled, Ignore }
    public enum LogAction { StateChanged, Error, NetworkDetected, Enforced }

    public class TrackedService
    {
        public ObjectId Id { get; set; } = ObjectId.NewObjectId();
        public string ServiceName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public EnforcedState DesiredState { get; set; } = EnforcedState.None;
        public DateTime DateAdded { get; set; } = DateTime.UtcNow;
        public DateTime LastTriggered { get; set; } = DateTime.UtcNow;
    }

    public class ServiceLogEntry
    {
        public ObjectId Id { get; set; } = ObjectId.NewObjectId();
        public string ServiceName { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public LogAction Action { get; set; }
        public string Message { get; set; } = string.Empty;
        public string? ExceptionDetails { get; set; }
    }

    public class AppConfig
    {
        public ObjectId Id { get; set; } = ObjectId.NewObjectId();
        public bool RunAtStartup { get; set; } = false;
        public bool MinimizeToTray { get; set; } = true;
        public int PollingIntervalMs { get; set; } = 10000;
        public bool EnableNotifications { get; set; } = true;
    }

    public partial class NetworkConnectionViewModel : ObservableObject
    {
        [ObservableProperty] private string _serviceName = string.Empty;
        [ObservableProperty] private string _displayName = string.Empty;
        [ObservableProperty] private int _pid;
        [ObservableProperty] private string _protocol = string.Empty;
        [ObservableProperty] private string _remoteEndpoint = string.Empty;
        [ObservableProperty] private int _localPort;
        [ObservableProperty] private bool _isDisabled;
    }

    public partial class TrackedServiceViewModel : ObservableObject
    {
        [ObservableProperty] private string _serviceName = string.Empty;
        [ObservableProperty] private EnforcedState _desiredState;
        [ObservableProperty] private int _logCount;
        [ObservableProperty] private DateTime _lastAction;
        public ObservableCollection<ServiceLogEntry> LogEntries { get; } = new();
    }
}

namespace SvchostMonitor.ViewModels
{
    using SvchostMonitor.Models;
    using SvchostMonitor.Engine;
    using WpfApp = System.Windows.Application;

    public partial class MainViewModel : ObservableObject
    {
        private BackgroundEnforcer? _enforcer;
        private readonly object _uiLock = new();

        [ObservableProperty] private bool _isPaused;
        [ObservableProperty] private int _activeCount;
        [ObservableProperty] private int _enforcedCount;
        [ObservableProperty] private int _breachCount;
        [ObservableProperty] private string _statusMessage = "Initializing...";
        [ObservableProperty] private DateTime _lastRefreshTime = DateTime.Now;
        [ObservableProperty] private NetworkConnectionViewModel? _selectedConnection;

        // ── Re-enable progress state ─────────────────────────────────────
        [ObservableProperty] private bool _isReEnabling;
        [ObservableProperty] private double _reEnableProgress;
        [ObservableProperty] private string _reEnableServiceName = string.Empty;
        public ObservableCollection<string> ReEnableLog { get; } = new();

        public ObservableCollection<NetworkConnectionViewModel> ActiveConnections { get; } = new();
        public ObservableCollection<TrackedServiceViewModel> TrackedServices { get; } = new();

        partial void OnIsPausedChanged(bool value)
        {
            if (_enforcer != null) _enforcer.IsPaused = value;
            StatusMessage = value ? "Enforcer paused by user." : "Enforcer resumed.";
        }

        public async Task InitializeAsync()
        {
            StatusMessage = "Scanning network connections...";
            await RefreshAsync();
            _enforcer = new BackgroundEnforcer(OnBreachDetected);
            _enforcer.Start();
            StatusMessage = "Monitoring active. Background enforcer running.";
        }

        [RelayCommand]
        private async Task RefreshAsync()
        {
            try
            {
                StatusMessage = "Refreshing...";
                var connections = await Task.Run(() => NetworkMapper.GetSvchostConnections());

                WpfApp.Current.Dispatcher.Invoke(() =>
                {
                    lock (_uiLock)
                    {
                        ActiveConnections.Clear();
                        var disabledServices = DatabaseHelper.Instance.GetTrackedServices()
                            .Where(s => s.DesiredState == EnforcedState.EnforceDisabled)
                            .ToList();

                        var disabledNames = disabledServices
                            .Select(s => s.ServiceName)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);

                        foreach (var conn in connections)
                        {
                            ActiveConnections.Add(new NetworkConnectionViewModel
                            {
                                ServiceName = conn.ServiceName,
                                DisplayName = conn.DisplayName,
                                Pid = conn.Pid,
                                Protocol = conn.Protocol,
                                RemoteEndpoint = conn.RemoteEndpoint,
                                LocalPort = conn.LocalPort,
                                IsDisabled = disabledNames.Contains(conn.ServiceName)
                            });
                        }

                        // Inject disabled services that are stopped (no active connections)
                        foreach (var svc in disabledServices)
                        {
                            bool alreadyShown = ActiveConnections.Any(c =>
                                c.ServiceName.Equals(svc.ServiceName, StringComparison.OrdinalIgnoreCase));
                            if (!alreadyShown)
                            {
                                ActiveConnections.Add(new NetworkConnectionViewModel
                                {
                                    ServiceName = svc.ServiceName,
                                    DisplayName = svc.DisplayName,
                                    Pid = 0,
                                    Protocol = "—",
                                    RemoteEndpoint = "—",
                                    LocalPort = 0,
                                    IsDisabled = true
                                });
                            }
                        }

                        ActiveCount = ActiveConnections.Count(c => !c.IsDisabled);
                        EnforcedCount = disabledNames.Count;
                        RefreshTrackedServicesView();
                        LastRefreshTime = DateTime.Now;
                        StatusMessage = $"Found {ActiveCount} active, {EnforcedCount} enforced disabled.";
                    }
                });
            }
            catch (Exception ex)
            {
                StatusMessage = $"Refresh error: {ex.Message}";
            }
        }

        [RelayCommand]
        private async Task ToggleServiceAsync(NetworkConnectionViewModel? conn)
        {
            if (conn == null) return;

            // After TwoWay binding fires, IsDisabled is already toggled.
            // IsDisabled=false means user wants to RE-ENABLE (was disabled, now wants active)
            // IsDisabled=true means user wants to DISABLE (was active, now wants disabled)

            if (conn.IsDisabled)
            {
                // ── DISABLE PATH ─────────────────────────────────────────
                var result = await Task.Run(() => ServiceControlManager.StopAndDisable(conn.ServiceName));
                if (result.Success)
                {
                    DatabaseHelper.Instance.UpsertTrackedService(new TrackedService
                    {
                        ServiceName = conn.ServiceName,
                        DisplayName = conn.DisplayName,
                        DesiredState = EnforcedState.EnforceDisabled
                    });
                    DatabaseHelper.Instance.AddLog(new ServiceLogEntry
                    {
                        ServiceName = conn.ServiceName,
                        Action = LogAction.StateChanged,
                        Message = $"Service '{conn.ServiceName}' stopped and set to Disabled."
                    });
                    EnforcedCount = DatabaseHelper.Instance.GetTrackedServices()
                        .Count(s => s.DesiredState == EnforcedState.EnforceDisabled);
                }
                else
                {
                    conn.IsDisabled = false;
                    DatabaseHelper.Instance.AddLog(new ServiceLogEntry
                    {
                        ServiceName = conn.ServiceName,
                        Action = LogAction.Error,
                        Message = $"Failed to disable '{conn.ServiceName}': {result.ErrorMessage}",
                        ExceptionDetails = result.ErrorMessage
                    });
                    StatusMessage = $"Error: {result.ErrorMessage}";
                }
            }
            else
            {
                // ── RE-ENABLE PATH with progress ─────────────────────────
                await ReEnableWithProgressAsync(conn);
            }

            WpfApp.Current.Dispatcher.Invoke(RefreshTrackedServicesView);
        }

        private async Task ReEnableWithProgressAsync(NetworkConnectionViewModel conn)
        {
            WpfApp.Current.Dispatcher.Invoke(() =>
            {
                IsReEnabling = true;
                ReEnableProgress = 0;
                ReEnableServiceName = conn.ServiceName;
                ReEnableLog.Clear();
                ReEnableLog.Add($"[{DateTime.Now:HH:mm:ss}] Starting re-enable for '{conn.ServiceName}'...");
            });

            void Log(string msg, double pct)
            {
                WpfApp.Current.Dispatcher.Invoke(() =>
                {
                    ReEnableLog.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
                    ReEnableProgress = pct;
                    StatusMessage = msg;
                });
            }

            var result = await Task.Run(() =>
                ServiceControlManager.ReEnableService(conn.ServiceName, (msg, pct) => Log(msg, pct)));

            WpfApp.Current.Dispatcher.Invoke(() =>
            {
                if (result.Success)
                {
                    ReEnableProgress = 100;
                    ReEnableLog.Add($"[{DateTime.Now:HH:mm:ss}] ✓ '{conn.ServiceName}' is now active and running.");
                    DatabaseHelper.Instance.RemoveTrackedService(conn.ServiceName);
                    DatabaseHelper.Instance.AddLog(new ServiceLogEntry
                    {
                        ServiceName = conn.ServiceName,
                        Action = LogAction.StateChanged,
                        Message = $"Service '{conn.ServiceName}' re-enabled and started successfully."
                    });
                    EnforcedCount = DatabaseHelper.Instance.GetTrackedServices()
                        .Count(s => s.DesiredState == EnforcedState.EnforceDisabled);
                    conn.IsDisabled = false;
                    StatusMessage = $"'{conn.ServiceName}' re-enabled successfully.";
                }
                else
                {
                    ReEnableLog.Add($"[{DateTime.Now:HH:mm:ss}] ✗ Failed: {result.ErrorMessage}");
                    conn.IsDisabled = true;
                    StatusMessage = $"Re-enable failed: {result.ErrorMessage}";
                    DatabaseHelper.Instance.AddLog(new ServiceLogEntry
                    {
                        ServiceName = conn.ServiceName,
                        Action = LogAction.Error,
                        Message = $"Re-enable failed: {result.ErrorMessage}",
                        ExceptionDetails = result.ErrorMessage
                    });
                }

                // Keep panel visible 2s then hide
                Task.Delay(2000).ContinueWith(_ =>
                    WpfApp.Current.Dispatcher.Invoke(() => IsReEnabling = false));
            });
        }

        private void OnBreachDetected(string serviceName)
        {
            WpfApp.Current.Dispatcher.Invoke(() =>
            {
                BreachCount++;
                StatusMessage = $"BREACH: '{serviceName}' restarted and was re-disabled at {DateTime.Now:HH:mm:ss}";
                RefreshTrackedServicesView();
            });
        }

        private void RefreshTrackedServicesView()
        {
            var dbServices = DatabaseHelper.Instance.GetTrackedServices();
            var dbLogs = DatabaseHelper.Instance.GetAllLogs();

            foreach (var svc in dbServices)
            {
                var existing = TrackedServices.FirstOrDefault(t =>
                    t.ServiceName.Equals(svc.ServiceName, StringComparison.OrdinalIgnoreCase));

                var logs = dbLogs
                    .Where(l => l.ServiceName.Equals(svc.ServiceName, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(l => l.Timestamp)
                    .ToList();

                if (existing == null)
                {
                    var newVm = new TrackedServiceViewModel
                    {
                        ServiceName = svc.ServiceName,
                        DesiredState = svc.DesiredState,
                        LogCount = logs.Count,
                        LastAction = logs.FirstOrDefault()?.Timestamp ?? svc.DateAdded
                    };
                    foreach (var log in logs) newVm.LogEntries.Add(log);
                    TrackedServices.Add(newVm);
                }
                else
                {
                    existing.DesiredState = svc.DesiredState;
                    existing.LogCount = logs.Count;
                    existing.LastAction = logs.FirstOrDefault()?.Timestamp ?? svc.DateAdded;
                    existing.LogEntries.Clear();
                    foreach (var log in logs) existing.LogEntries.Add(log);
                }
            }

            var toRemove = TrackedServices
                .Where(t => !dbServices.Any(s => s.ServiceName.Equals(t.ServiceName, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            foreach (var t in toRemove) TrackedServices.Remove(t);
        }
    }
}
