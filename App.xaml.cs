using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using System.Diagnostics;
using SvchostMonitor.Engine;

namespace SvchostMonitor;

public partial class App : System.Windows.Application
{
    private MainWindow? _mainWindow;
    private NotifyIcon? _trayIcon;
    private const string TaskName = "SvchostNetworkMonitor_Startup";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DatabaseHelper.Instance.Initialize();
        InitTrayIcon();
        _mainWindow = new MainWindow();
        _mainWindow.Show();
    }

    private void InitTrayIcon()
    {
        _trayIcon = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "Svchost Network Monitor",
            Visible = true
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowMainWindow());
        menu.Items.Add(new ToolStripSeparator());

        var pauseItem = new ToolStripMenuItem("Pause Enforcement");
        pauseItem.Click += (_, _) =>
        {
            if (_mainWindow?.DataContext is ViewModels.MainViewModel vm)
            {
                vm.IsPaused = !vm.IsPaused;
                pauseItem.Text = vm.IsPaused ? "Resume Enforcement" : "Pause Enforcement";
            }
        };
        menu.Items.Add(pauseItem);

        menu.Items.Add("Run Diagnostics Now", null, (_, _) =>
        {
            if (_mainWindow?.DataContext is ViewModels.MainViewModel vm)
                _ = vm.RefreshCommand.ExecuteAsync(null);
        });

        menu.Items.Add(new ToolStripSeparator());

        var startupItem = new ToolStripMenuItem("Run at Windows Startup")
        {
            Checked = IsStartupTaskEnabled()
        };
        startupItem.Click += (_, _) =>
        {
            ToggleStartupTask();
            startupItem.Checked = IsStartupTaskEnabled();
        };
        menu.Items.Add(startupItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit Completely", null, (_, _) => Shutdown());

        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        if (_mainWindow == null) return;
        _mainWindow.Show();
        _mainWindow.WindowState = System.Windows.WindowState.Normal;
        _mainWindow.Activate();
    }

    private static Icon CreateIcon()
    {
        var exeDir = System.IO.Path.GetDirectoryName(ExePath) ?? ".";
        var icoPath = System.IO.Path.Combine(exeDir, "tray.ico");
        if (System.IO.File.Exists(icoPath))
            return new Icon(icoPath, 16, 16);

        // Fallback: generate in memory
        var bmp = new System.Drawing.Bitmap(16, 16);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.FillEllipse(new SolidBrush(Color.FromArgb(0, 212, 255)), 1, 1, 13, 13);
        g.FillEllipse(new SolidBrush(Color.FromArgb(0, 30, 50)), 4, 4, 7, 7);
        return Icon.FromHandle(bmp.GetHicon());
    }

    private static string ExePath =>
        Process.GetCurrentProcess().MainModule!.FileName;

    private static bool IsStartupTaskEnabled()
    {
        var result = RunSchtasks($"/Query /TN \"{TaskName}\" /FO LIST");
        return result.ExitCode == 0;
    }

    private static void ToggleStartupTask()
    {
        if (IsStartupTaskEnabled())
        {
            RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
        }
        else
        {
            string xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <Triggers><LogonTrigger><Enabled>true</Enabled></LogonTrigger></Triggers>
  <Principals>
    <Principal id=""Author"">
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions>
    <Exec><Command>{ExePath}</Command></Exec>
  </Actions>
</Task>";
            var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "svcwatch_task.xml");
            System.IO.File.WriteAllText(tmp, xml, System.Text.Encoding.Unicode);
            RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{tmp}\" /F");
            System.IO.File.Delete(tmp);
        }
    }

    private static (int ExitCode, string Output) RunSchtasks(string args)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return (p.ExitCode, p.StandardOutput.ReadToEnd());
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        DatabaseHelper.Instance.Dispose();
        base.OnExit(e);
    }
}
