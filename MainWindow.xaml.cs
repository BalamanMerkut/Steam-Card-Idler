using System.ComponentModel;
using System.Windows;
using SteamCardIdler.ViewModels;
using Wpf.Ui.Controls;

namespace SteamCardIdler
{
    public partial class MainWindow : FluentWindow
    {
        private MainViewModel _viewModel;

        public MainWindow()
        {
            InitializeComponent();
            _viewModel = new MainViewModel();
            DataContext = _viewModel;

            // Pass tray icon reference to ViewModel for balloon notifications
            _viewModel.SetTrayIcon(TrayIcon);
        }

        // Minimize to tray when window is minimized
        private void Window_StateChanged(object sender, System.EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                if (_viewModel.MinimizeToTrayOnClose)
                {
                    Hide();
                    try
                    {
                        if (TrayIcon != null && !TrayIcon.IsDisposed)
                        {
                            TrayIcon.ShowBalloonTip(
                                "Steam Card Idler",
                                "Uygulama arka planda çalışmaya devam ediyor.",
                                Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info
                            );
                        }
                    }
                    catch { /* TrayIcon erişilemez durumdaysa sessizce geç */ }
                }
            }
        }

        // Close button: exit the app directly
        private void Window_Closing(object sender, CancelEventArgs e)
        {
            TrayIcon?.Dispose();
        }

        // Double-click tray icon to restore window
        private void TrayIcon_TrayMouseDoubleClick(object sender, RoutedEventArgs e)
        {
            BringToFront();
        }

        // Tray context menu → "Show"
        private void ShowWindow_Click(object sender, RoutedEventArgs e)
        {
            BringToFront();
        }

        private void BringToFront()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }
    }
}
