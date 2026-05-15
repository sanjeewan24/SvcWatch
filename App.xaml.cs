using System.Windows;
using SvchostMonitor.Engine;

namespace SvchostMonitor;

public partial class App : Application
{
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DatabaseHelper.Instance.Initialize();
        _mainWindow = new MainWindow();
        _mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (Resources["TrayIcon"] is System.Windows.FrameworkElement tray)
            tray.Visibility = Visibility.Collapsed;
        DatabaseHelper.Instance.Dispose();
        base.OnExit(e);
    }

    private void TrayMenuOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_mainWindow == null) return;
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private void TrayMenuPause_Click(object sender, RoutedEventArgs e)
    {
        if (_mainWindow?.DataContext is ViewModels.MainViewModel vm)
            vm.IsPaused = !vm.IsPaused;
    }

    private void TrayMenuExit_Click(object sender, RoutedEventArgs e)
    {
        Shutdown();
    }

    private void TrayIcon_DoubleClick(object sender, RoutedEventArgs e)
    {
        TrayMenuOpen_Click(sender, e);
    }
}
