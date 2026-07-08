using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

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
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
            AppLogger.Info("Application starting.");

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
                AppLogger.Info("Connection dialog cancelled. Application shutting down.");
                Current.Shutdown();
            }
        }

        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            AppLogger.Error(e.Exception, "Unhandled UI exception");
            MessageBox.Show($"Unexpected error. Log saved to:{Environment.NewLine}{AppLogger.LogDirectory}", "MiniSSMS Error", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                AppLogger.Error(ex, "Unhandled domain exception");
            }
            else
            {
                AppLogger.Info($"Unhandled domain exception object: {e.ExceptionObject}");
            }
        }

        private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            AppLogger.Error(e.Exception, "Unobserved task exception");
            e.SetObserved();
        }
    }
}

