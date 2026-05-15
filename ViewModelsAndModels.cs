using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiteDB;
using SvchostMonitor.Engine;
using SvchostMonitor.Models;

// ──────────────────────────────────────────
//  ENUMS
// ──────────────────────────────────────────
namespace SvchostMonitor.Models
{
    public enum EnforcedState { None, EnforceDisabled, Ignore }

    public enum LogAction { StateChanged, Error, NetworkDetected, Enforced }

    // ──────────────────────────────────────────
    //  LiteDB PERSISTENCE MODELS
    // ──────────────────────────────────────────
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

    // ──────────────────────────────────────────
    //  UI VIEW MODELS (Observable wrappers)
    // ──────────────────────────────────────────

    /// <summary>Represents one active svchost network connection row in the Dashboard DataGrid.</summary>
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

    /// <summary>Represents a tracked service with its log history for the Logs Tab.</summary>
    public partial class TrackedServiceViewModel : ObservableObject
    {
        [ObservableProperty] private string _serviceName = string.Empty;
        [ObservableProperty] private EnforcedState _desiredState;
        [ObservableProperty] private int _logCount;
        [ObservableProperty] private DateTime _lastAction;
        public ObservableCollection<ServiceLogEntry> LogEntries { get; } = new();
    }
}

// ──────────────────────────────────────────
//  MAIN VIEW MODEL
// ──────────────────────────────────────────
namespace SvchostMonitor.ViewModels
{
    using SvchostMonitor.Models;
    using SvchostMonitor.Engine;

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

        public ObservableCollection<NetworkConnectionViewModel> ActiveConnections { get; } = new();
        public ObservableCollection<TrackedServiceViewModel> TrackedServices { get; } = new();

        partial void OnIsPausedChanged(bool value)
        {
            if (_enforcer != null)
                _enforcer.IsPaused = value;
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

                Application.Current.Dispatcher.Invoke(() =>
                {
                    lock (_uiLock)
                    {
                        ActiveConnections.Clear();
                        var disabledServices = DatabaseHelper.Instance.GetTrackedServices()
                            .Where(s => s.DesiredState == EnforcedState.EnforceDisabled)
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
                                IsDisabled = disabledServices.Contains(conn.ServiceName)
                            });
                        }

                        ActiveCount = ActiveConnections.Count;
                        EnforcedCount = disabledServices.Count;

                        RefreshTrackedServicesView();
                        LastRefreshTime = DateTime.Now;
                        StatusMessage = $"Found {ActiveCount} active svchost connection(s).";
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

            if (!conn.IsDisabled)
            {
                // Re-enable: remove from enforcer
                await Task.Run(() =>
                {
                    DatabaseHelper.Instance.RemoveTrackedService(conn.ServiceName);
                    DatabaseHelper.Instance.AddLog(new ServiceLogEntry
                    {
                        ServiceName = conn.ServiceName,
                        Action = LogAction.StateChanged,
                        Message = $"Service '{conn.ServiceName}' removed from enforcement list."
                    });
                });
                conn.IsDisabled = false;
            }
            else
            {
                // Disable: stop + enforce
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
                    conn.IsDisabled = true;
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

            Application.Current.Dispatcher.Invoke(RefreshTrackedServicesView);
        }

        private void OnBreachDetected(string serviceName)
        {
            Application.Current.Dispatcher.Invoke(() =>
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
                    foreach (var log in logs)
                        newVm.LogEntries.Add(log);
                    TrackedServices.Add(newVm);
                }
                else
                {
                    existing.DesiredState = svc.DesiredState;
                    existing.LogCount = logs.Count;
                    existing.LastAction = logs.FirstOrDefault()?.Timestamp ?? svc.DateAdded;
                    existing.LogEntries.Clear();
                    foreach (var log in logs)
                        existing.LogEntries.Add(log);
                }
            }

            // Remove stale entries
            var toRemove = TrackedServices
                .Where(t => !dbServices.Any(s => s.ServiceName.Equals(t.ServiceName, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            foreach (var t in toRemove)
                TrackedServices.Remove(t);
        }
    }
}
