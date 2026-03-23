using System.Windows;

namespace SteamCardIdler
{
    public partial class App : Application
    {
        private bool _isHandlingException = false;

        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            base.OnStartup(e);
        }

        private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            if (_isHandlingException) return;
            _isHandlingException = true;

            try 
            {
                System.IO.File.AppendAllText("debug.log", $"[FATAL ERROR] {DateTime.Now}: {e.Exception}{Environment.NewLine}");
            }
            catch { }
            finally
            {
                e.Handled = true;
                Shutdown();
            }
        }
    }
}
