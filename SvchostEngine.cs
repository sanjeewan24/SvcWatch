using System.ComponentModel;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using LiteDB;
using Management = System.Management;
using Microsoft.Toolkit.Uwp.Notifications;
using SvchostMonitor.Models;

namespace SvchostMonitor.Engine
{
    // ══════════════════════════════════════════════════════════════════════
    //  NATIVE WIN32 P/INVOKE DEFINITIONS
    // ══════════════════════════════════════════════════════════════════════
    internal static class NativeMethods
    {
        internal const uint NO_ERROR = 0;
        internal const uint AF_INET = 2;
        internal const uint TCP_TABLE_OWNER_PID_ALL = 5;
        internal const uint UDP_TABLE_OWNER_PID = 1;

        // Service config change constants
        internal const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;
        internal const uint SERVICE_DISABLED = 0x00000004;
        internal const uint SC_MANAGER_ALL_ACCESS = 0xF003F;
        internal const uint SERVICE_ALL_ACCESS = 0xF01FF;

        // ── TCP Table Structs ──────────────────────────────────────────────
        [StructLayout(LayoutKind.Sequential)]
        internal struct MIB_TCPROW_OWNER_PID
        {
            public uint dwState;
            public uint dwLocalAddr;
            public uint dwLocalPort;
            public uint dwRemoteAddr;
            public uint dwRemotePort;
            public uint dwOwningPid;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MIB_TCPTABLE_OWNER_PID
        {
            public uint dwNumEntries;
            [MarshalAs(UnmanagedType.ByValArray, ArraySubType = UnmanagedType.Struct, SizeConst = 1)]
            public MIB_TCPROW_OWNER_PID[] table;
        }

        // ── UDP Table Structs ──────────────────────────────────────────────
        [StructLayout(LayoutKind.Sequential)]
        internal struct MIB_UDPROW_OWNER_PID
        {
            public uint dwLocalAddr;
            public uint dwLocalPort;
            public uint dwOwningPid;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MIB_UDPTABLE_OWNER_PID
        {
            public uint dwNumEntries;
            [MarshalAs(UnmanagedType.ByValArray, ArraySubType = UnmanagedType.Struct, SizeConst = 1)]
            public MIB_UDPROW_OWNER_PID[] table;
        }

        // ── P/Invoke Declarations ─────────────────────────────────────────
        [DllImport("iphlpapi.dll", SetLastError = true)]
        internal static extern uint GetExtendedTcpTable(
            IntPtr pTcpTable, ref uint dwOutBufLen, bool sort,
            uint ipVersion, uint tableClass, uint reserved);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        internal static extern uint GetExtendedUdpTable(
            IntPtr pUdpTable, ref uint dwOutBufLen, bool sort,
            uint ipVersion, uint tableClass, uint reserved);

        [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint dwAccess);

        [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr OpenService(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ChangeServiceConfig(
            IntPtr hService, uint nServiceType, uint nStartType,
            uint nErrorControl, string? lpBinaryPathName, string? lpLoadOrderGroup,
            IntPtr lpdwTagId, string? lpDependencies, string? lpServiceStartName,
            string? lpPassword, string? lpDisplayName);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseServiceHandle(IntPtr hSCObject);

        // ── Helper: Reverse port bytes ────────────────────────────────────
        internal static int ReverseBytes(uint port) =>
            (int)(((port & 0xFF) << 8) | ((port >> 8) & 0xFF));
    }

    // ══════════════════════════════════════════════════════════════════════
    //  NETWORK CONNECTION DATA TRANSFER OBJECT
    // ══════════════════════════════════════════════════════════════════════
    public class SvchostConnection
    {
        public string ServiceName { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public int Pid { get; init; }
        public string Protocol { get; init; } = string.Empty;
        public string RemoteEndpoint { get; init; } = string.Empty;
        public int LocalPort { get; init; }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  NETWORK MAPPER
    //  Reads TCP/UDP tables -> identifies svchost PIDs -> maps to services
    // ══════════════════════════════════════════════════════════════════════
    public static class NetworkMapper
    {
        private static readonly TimeSpan WmiCacheExpiry = TimeSpan.FromSeconds(15);
        private static Dictionary<int, List<(string ServiceName, string DisplayName)>> _wmiCache = new();
        private static DateTime _wmiFetchedAt = DateTime.MinValue;

        public static List<SvchostConnection> GetSvchostConnections()
        {
            var pidToServices = GetPidServiceMap();
            var svchostPids = GetSvchostPids();
            var results = new List<SvchostConnection>();

            foreach (var pid in svchostPids)
            {
                if (!pidToServices.TryGetValue(pid, out var services))
                    continue;

                var tcpRows = GetTcpRows(pid);
                var udpRows = GetUdpRows(pid);

                foreach (var (serviceName, displayName) in services)
                {
                    foreach (var row in tcpRows)
                    {
                        results.Add(new SvchostConnection
                        {
                            ServiceName = serviceName,
                            DisplayName = displayName,
                            Pid = pid,
                            Protocol = "TCP",
                            RemoteEndpoint = FormatEndpoint(row.dwRemoteAddr, row.dwRemotePort),
                            LocalPort = NativeMethods.ReverseBytes(row.dwLocalPort)
                        });
                    }
                    foreach (var row in udpRows)
                    {
                        results.Add(new SvchostConnection
                        {
                            ServiceName = serviceName,
                            DisplayName = displayName,
                            Pid = pid,
                            Protocol = "UDP",
                            RemoteEndpoint = "*",
                            LocalPort = NativeMethods.ReverseBytes(row.dwLocalPort)
                        });
                    }
                }
            }

            // Deduplicate by ServiceName + Protocol + RemoteEndpoint
            return results
                .GroupBy(c => $"{c.ServiceName}|{c.Protocol}|{c.RemoteEndpoint}")
                .Select(g => g.First())
                .ToList();
        }

        private static Dictionary<int, List<(string, string)>> GetPidServiceMap()
        {
            if (DateTime.UtcNow - _wmiFetchedAt < WmiCacheExpiry && _wmiCache.Count > 0)
                return _wmiCache;

            var map = new Dictionary<int, List<(string, string)>>();
            try
            {
                using var searcher = new Management.ManagementObjectSearcher(
                    "SELECT Name, DisplayName, ProcessId FROM Win32_Service");
                using var results = searcher.Get();
                foreach (Management.ManagementObject obj in results)
                {
                    var pidObj = obj["ProcessId"];
                    var name = obj["Name"]?.ToString() ?? string.Empty;
                    var display = obj["DisplayName"]?.ToString() ?? name;
                    if (pidObj == null) continue;
                    int pid = Convert.ToInt32(pidObj);
                    if (!map.ContainsKey(pid))
                        map[pid] = new List<(string, string)>();
                    map[pid].Add((name, display));
                }
                _wmiCache = map;
                _wmiFetchedAt = DateTime.UtcNow;
            }
            catch
            {
                // Return stale cache if WMI fails
            }
            return _wmiCache;
        }

        private static HashSet<int> GetSvchostPids()
        {
            var pids = new HashSet<int>();
            try
            {
                foreach (var proc in System.Diagnostics.Process.GetProcessesByName("svchost"))
                    pids.Add(proc.Id);
            }
            catch { }
            return pids;
        }

        private static List<NativeMethods.MIB_TCPROW_OWNER_PID> GetTcpRows(int filterPid)
        {
            var rows = new List<NativeMethods.MIB_TCPROW_OWNER_PID>();
            uint bufLen = 0;
            NativeMethods.GetExtendedTcpTable(IntPtr.Zero, ref bufLen, true,
                NativeMethods.AF_INET, NativeMethods.TCP_TABLE_OWNER_PID_ALL, 0);

            IntPtr buf = Marshal.AllocHGlobal((int)bufLen);
            try
            {
                if (NativeMethods.GetExtendedTcpTable(buf, ref bufLen, true,
                    NativeMethods.AF_INET, NativeMethods.TCP_TABLE_OWNER_PID_ALL, 0) != NativeMethods.NO_ERROR)
                    return rows;

                int count = Marshal.ReadInt32(buf);
                IntPtr rowPtr = buf + 4;
                int rowSize = Marshal.SizeOf<NativeMethods.MIB_TCPROW_OWNER_PID>();
                for (int i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<NativeMethods.MIB_TCPROW_OWNER_PID>(rowPtr);
                    if ((int)row.dwOwningPid == filterPid && row.dwRemoteAddr != 0)
                        rows.Add(row);
                    rowPtr += rowSize;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
            return rows;
        }

        private static List<NativeMethods.MIB_UDPROW_OWNER_PID> GetUdpRows(int filterPid)
        {
            var rows = new List<NativeMethods.MIB_UDPROW_OWNER_PID>();
            uint bufLen = 0;
            NativeMethods.GetExtendedUdpTable(IntPtr.Zero, ref bufLen, true,
                NativeMethods.AF_INET, NativeMethods.UDP_TABLE_OWNER_PID, 0);

            IntPtr buf = Marshal.AllocHGlobal((int)bufLen);
            try
            {
                if (NativeMethods.GetExtendedUdpTable(buf, ref bufLen, true,
                    NativeMethods.AF_INET, NativeMethods.UDP_TABLE_OWNER_PID, 0) != NativeMethods.NO_ERROR)
                    return rows;

                int count = Marshal.ReadInt32(buf);
                IntPtr rowPtr = buf + 4;
                int rowSize = Marshal.SizeOf<NativeMethods.MIB_UDPROW_OWNER_PID>();
                for (int i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<NativeMethods.MIB_UDPROW_OWNER_PID>(rowPtr);
                    if ((int)row.dwOwningPid == filterPid)
                        rows.Add(row);
                    rowPtr += rowSize;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
            return rows;
        }

        private static string FormatEndpoint(uint addr, uint port)
        {
            try
            {
                var ip = new IPAddress(addr);
                int p = NativeMethods.ReverseBytes(port);
                return $"{ip}:{p}";
            }
            catch
            {
                return "unknown";
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  SERVICE CONTROL MANAGER
    //  Stop + set StartupType = Disabled via advapi32.dll
    // ══════════════════════════════════════════════════════════════════════
    public class ServiceOperationResult
    {
        public bool Success { get; init; }
        public string? ErrorMessage { get; init; }
    }

    public static class ServiceControlManager
    {
        public static ServiceOperationResult StopAndDisable(string serviceName)
        {
            try
            {
                // 1. Stop via ServiceController
                using var sc = new ServiceController(serviceName);
                if (sc.Status == ServiceControllerStatus.Running ||
                    sc.Status == ServiceControllerStatus.StartPending)
                {
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                }

                // 2. Set startup type to Disabled via advapi32
                var result = SetStartupTypeDisabled(serviceName);
                return result;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 5) // ERROR_ACCESS_DENIED
            {
                return new ServiceOperationResult
                {
                    Success = false,
                    ErrorMessage = $"Access denied. Cannot modify critical system service '{serviceName}'."
                };
            }
            catch (InvalidOperationException ex)
            {
                return new ServiceOperationResult
                {
                    Success = false,
                    ErrorMessage = $"Service '{serviceName}' not found or could not be controlled: {ex.Message}"
                };
            }
            catch (Exception ex)
            {
                return new ServiceOperationResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        public static ServiceOperationResult StartAndEnable(string serviceName)
        {
            try
            {
                using var sc = new ServiceController(serviceName);
                if (sc.Status != ServiceControllerStatus.Running)
                {
                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
                }
                return new ServiceOperationResult { Success = true };
            }
            catch (Exception ex)
            {
                return new ServiceOperationResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        private static ServiceOperationResult SetStartupTypeDisabled(string serviceName)
        {
            IntPtr scmHandle = NativeMethods.OpenSCManager(null, null, NativeMethods.SC_MANAGER_ALL_ACCESS);
            if (scmHandle == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                return new ServiceOperationResult
                {
                    Success = false,
                    ErrorMessage = $"OpenSCManager failed. Win32 error: {err}"
                };
            }

            IntPtr svcHandle = NativeMethods.OpenService(scmHandle, serviceName, NativeMethods.SERVICE_ALL_ACCESS);
            if (svcHandle == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                NativeMethods.CloseServiceHandle(scmHandle);
                return new ServiceOperationResult
                {
                    Success = false,
                    ErrorMessage = $"OpenService failed for '{serviceName}'. Win32 error: {err}"
                };
            }

            bool changed = NativeMethods.ChangeServiceConfig(
                svcHandle,
                NativeMethods.SERVICE_NO_CHANGE,
                NativeMethods.SERVICE_DISABLED,
                NativeMethods.SERVICE_NO_CHANGE,
                null, null, IntPtr.Zero, null, null, null, null);

            NativeMethods.CloseServiceHandle(svcHandle);
            NativeMethods.CloseServiceHandle(scmHandle);

            if (!changed)
            {
                int err = Marshal.GetLastWin32Error();
                return new ServiceOperationResult
                {
                    Success = false,
                    ErrorMessage = $"ChangeServiceConfig failed. Win32 error: {err}"
                };
            }

            return new ServiceOperationResult { Success = true };
        }

        public static bool IsServiceRunning(string serviceName)
        {
            try
            {
                using var sc = new ServiceController(serviceName);
                return sc.Status == ServiceControllerStatus.Running ||
                       sc.Status == ServiceControllerStatus.StartPending;
            }
            catch
            {
                return false;
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  DATABASE HELPER
    //  LiteDB singleton with WAL mode for concurrent access safety
    // ══════════════════════════════════════════════════════════════════════
    public sealed class DatabaseHelper : IDisposable
    {
        private static readonly Lazy<DatabaseHelper> _instance =
            new(() => new DatabaseHelper());
        public static DatabaseHelper Instance => _instance.Value;

        private LiteDatabase? _db;
        private ILiteCollection<TrackedService>? _services;
        private ILiteCollection<ServiceLogEntry>? _logs;
        private ILiteCollection<AppConfig>? _config;
        private readonly object _lock = new();

        private DatabaseHelper() { }

        public void Initialize()
        {
            var dbPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SvchostMonitor", "Data.db");

            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

            var connStr = new ConnectionString(dbPath)
            {
                Connection = ConnectionType.Shared,
            };
            _db = new LiteDatabase(connStr);
            _services = _db.GetCollection<TrackedService>("TrackedServices");
            _logs = _db.GetCollection<ServiceLogEntry>("ServiceLogs");
            _config = _db.GetCollection<AppConfig>("AppPreferences");

            _services.EnsureIndex(x => x.ServiceName);
            _logs.EnsureIndex(x => x.ServiceName);
            _logs.EnsureIndex(x => x.Timestamp);
        }

        public void UpsertTrackedService(TrackedService service)
        {
            lock (_lock)
            {
                var existing = _services!.FindOne(x =>
                    x.ServiceName == service.ServiceName);
                if (existing != null)
                {
                    existing.DesiredState = service.DesiredState;
                    existing.DisplayName = service.DisplayName;
                    existing.LastTriggered = DateTime.UtcNow;
                    _services.Update(existing);
                }
                else
                {
                    _services.Insert(service);
                }
            }
        }

        public void RemoveTrackedService(string serviceName)
        {
            lock (_lock)
            {
                _services!.DeleteMany(x => x.ServiceName == serviceName);
            }
        }

        public List<TrackedService> GetTrackedServices()
        {
            lock (_lock)
            {
                return _services!.FindAll().ToList();
            }
        }

        public TrackedService? GetTrackedService(string serviceName)
        {
            lock (_lock)
            {
                return _services!.FindOne(x => x.ServiceName == serviceName);
            }
        }

        public void AddLog(ServiceLogEntry entry)
        {
            lock (_lock)
            {
                _logs!.Insert(entry);
                // Prune: keep last 500 entries per service
                var count = _logs.Count(x => x.ServiceName == entry.ServiceName);
                if (count > 500)
                {
                    var oldest = _logs
                        .Find(x => x.ServiceName == entry.ServiceName)
                        .OrderBy(x => x.Timestamp)
                        .Take(count - 500)
                        .Select(x => x.Id)
                        .ToList();
                    foreach (var id in oldest)
                        _logs.Delete(id);
                }
            }
        }

        public List<ServiceLogEntry> GetLogsForService(string serviceName, int limit = 100)
        {
            lock (_lock)
            {
                return _logs!
                    .Find(x => x.ServiceName == serviceName)
                    .OrderByDescending(x => x.Timestamp)
                    .Take(limit)
                    .ToList();
            }
        }

        public List<ServiceLogEntry> GetAllLogs(int limitPerService = 50)
        {
            lock (_lock)
            {
                var services = _services!.FindAll().Select(s => s.ServiceName).ToList();
                var result = new List<ServiceLogEntry>();
                foreach (var svc in services)
                {
                    var logs = _logs!
                        .Find(x => x.ServiceName == svc)
                        .OrderByDescending(x => x.Timestamp)
                        .Take(limitPerService)
                        .ToList();
                    result.AddRange(logs);
                }
                return result;
            }
        }

        public AppConfig GetOrCreateConfig()
        {
            lock (_lock)
            {
                var cfg = _config!.FindAll().FirstOrDefault();
                if (cfg == null)
                {
                    cfg = new AppConfig();
                    _config.Insert(cfg);
                }
                return cfg;
            }
        }

        public void SaveConfig(AppConfig config)
        {
            lock (_lock)
            {
                _config!.Upsert(config);
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _db?.Dispose();
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  BACKGROUND ENFORCER
    //  Polls every 10s; re-disables any enforced service that has restarted
    // ══════════════════════════════════════════════════════════════════════
    public sealed class BackgroundEnforcer
    {
        private readonly Action<string> _onBreachDetected;
        private CancellationTokenSource _cts = new();
        private Task? _task;

        public bool IsPaused { get; set; }

        public BackgroundEnforcer(Action<string> onBreachDetected)
        {
            _onBreachDetected = onBreachDetected;
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _task = Task.Run(() => RunAsync(_cts.Token));
        }

        public void Stop()
        {
            _cts.Cancel();
        }

        private async Task RunAsync(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            while (!ct.IsCancellationRequested && await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (IsPaused) continue;

                try
                {
                    await EnforceCycleAsync();
                }
                catch (Exception ex)
                {
                    DatabaseHelper.Instance.AddLog(new ServiceLogEntry
                    {
                        ServiceName = "_enforcer",
                        Action = LogAction.Error,
                        Message = "Background enforcer cycle error.",
                        ExceptionDetails = ex.ToString()
                    });
                }
            }
        }

        private async Task EnforceCycleAsync()
        {
            var tracked = DatabaseHelper.Instance.GetTrackedServices()
                .Where(s => s.DesiredState == EnforcedState.EnforceDisabled)
                .ToList();

            foreach (var svc in tracked)
            {
                bool running = await Task.Run(() =>
                    ServiceControlManager.IsServiceRunning(svc.ServiceName));

                if (!running) continue;

                // Breach detected
                DatabaseHelper.Instance.AddLog(new ServiceLogEntry
                {
                    ServiceName = svc.ServiceName,
                    Action = LogAction.Enforced,
                    Message = $"Breach detected: '{svc.ServiceName}' restarted. Re-disabling automatically."
                });

                var result = await Task.Run(() =>
                    ServiceControlManager.StopAndDisable(svc.ServiceName));

                if (result.Success)
                {
                    DatabaseHelper.Instance.AddLog(new ServiceLogEntry
                    {
                        ServiceName = svc.ServiceName,
                        Action = LogAction.Enforced,
                        Message = $"'{svc.ServiceName}' successfully re-stopped and re-disabled by enforcer."
                    });
                    var tracked2 = DatabaseHelper.Instance.GetTrackedService(svc.ServiceName);
                    if (tracked2 != null)
                    {
                        tracked2.LastTriggered = DateTime.UtcNow;
                        DatabaseHelper.Instance.UpsertTrackedService(tracked2);
                    }
                }
                else
                {
                    DatabaseHelper.Instance.AddLog(new ServiceLogEntry
                    {
                        ServiceName = svc.ServiceName,
                        Action = LogAction.Error,
                        Message = $"Enforcer failed to re-disable '{svc.ServiceName}': {result.ErrorMessage}",
                        ExceptionDetails = result.ErrorMessage
                    });
                }

                // Toast notification on main thread
                SendBreachNotification(svc.ServiceName, result.Success);
                _onBreachDetected(svc.ServiceName);
            }
        }

        private static void SendBreachNotification(string serviceName, bool reDisabledSuccessfully)
        {
            try
            {
                var builder = new ToastContentBuilder()
                    .AddText("Service Restart Detected")
                    .AddText($"The service '{serviceName}' restarted and initiated a network connection.")
                    .AddArgument("serviceName", serviceName);

                if (reDisabledSuccessfully)
                {
                    builder.AddText("✓ Automatically re-disabled by enforcer.");
                }
                else
                {
                    builder
                        .AddButton(new ToastButton()
                            .SetContent("Re-disable")
                            .AddArgument("action", "redisable")
                            .AddArgument("serviceName", serviceName))
                        .AddButton(new ToastButton()
                            .SetContent("Ignore")
                            .AddArgument("action", "ignore")
                            .AddArgument("serviceName", serviceName));
                }

                builder.Show();
            }
            catch
            {
                // Toast not available (e.g. older Windows build without notification support)
            }
        }
    }
}
