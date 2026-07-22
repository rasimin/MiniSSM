using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SSMS
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override async void OnStartup(StartupEventArgs e)
        {
            // Disable UI Automation globally for ItemsControls to prevent major DataGrid scroll lag
            AppContext.SetSwitch("Switch.System.Windows.Controls.ItemsControlDoesNotSupportAutomation", true);

            base.OnStartup(e);
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
            AppLogger.Info("Application starting.");

            // Prevent WPF from shutting down when the connection dialog closes
            Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var startupWindow = new StartupWindow
            {
                Topmost = true
            };
            var connWindow = new ConnectionWindow();
            connWindow.ConnectionAccepted += (_, _) =>
            {
                startupWindow.SetStatus("Loading workspace...");
                startupWindow.Show();
                startupWindow.UpdateLayout();
                Dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
            };

            // Preload WebView2 environment in the background so that the first query tab opens instantly
            _ = QueryTabControl.PreloadWebViewEnvironmentAsync();

            if (connWindow.ShowDialog() == true)
            {
                var mainWindow = new MainWindow(connWindow.ConnectionString);
                Current.MainWindow = mainWindow;
                mainWindow.Show();
                await mainWindow.StartupCompletion;

                Current.ShutdownMode = ShutdownMode.OnLastWindowClose;
                mainWindow.Activate();

                var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180))
                {
                    FillBehavior = FillBehavior.Stop
                };
                fadeOut.Completed += (_, _) =>
                {
                    startupWindow.Topmost = false;
                    startupWindow.Close();
                    mainWindow.Activate();
                };
                startupWindow.BeginAnimation(Window.OpacityProperty, fadeOut);
            }
            else
            {
                AppLogger.Info("Connection dialog cancelled. Application shutting down.");
                startupWindow.Close();
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

