using System;
using System.Windows;

namespace SSMS
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Prevent WPF from shutting down when the connection dialog closes
            Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var connWindow = new ConnectionWindow();
            if (connWindow.ShowDialog() == true)
            {
                var mainWindow = new MainWindow(connWindow.ConnectionString);
                Current.MainWindow = mainWindow;
                Current.ShutdownMode = ShutdownMode.OnLastWindowClose;
                mainWindow.Show();
            }
            else
            {
                Current.Shutdown();
            }
        }
    }
}

