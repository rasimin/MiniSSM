using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace SSMS;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                AppLogger.Error(ex, "Unhandled domain exception");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLogger.Error(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Start(desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static async void Start(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            AppLogger.Info("Avalonia application starting.");
            var connection = new ConnectionWindow();
            desktop.MainWindow = connection;
            connection.Show();
            var connectionString = await connection.WaitForResultAsync();
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                desktop.Shutdown();
                return;
            }

            var main = new MainWindow(connectionString);
            desktop.MainWindow = main;
            desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
            main.Show();
        }
        catch (Exception ex)
        {
            AppLogger.Error(ex, "Application startup failed.");
            desktop.Shutdown(-1);
        }
    }
}
