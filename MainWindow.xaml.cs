using System.Windows;
using System.Windows.Input;
using SvchostMonitor.ViewModels;

namespace SvchostMonitor;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            await vm.InitializeAsync();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        if (Application.Current.Resources["TrayIcon"] is System.Windows.FrameworkElement tray)
            tray.Visibility = Visibility.Visible;
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
        if (Application.Current.Resources["TrayIcon"] is System.Windows.FrameworkElement tray2)
            tray2.Visibility = Visibility.Visible;
    }
}
