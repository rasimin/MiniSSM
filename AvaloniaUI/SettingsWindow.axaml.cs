using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SSMS;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        TimeoutBox.Text = AppSettings.Current.Query.CommandTimeoutSeconds.ToString();
    }

    private async void Save_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!int.TryParse(TimeoutBox.Text, out var timeout) || timeout < 0)
        {
            await DialogService.ShowAsync(this, "Invalid Setting",
                "Command timeout must be a whole number of seconds (0 or greater).");
            TimeoutBox.Focus();
            TimeoutBox.SelectAll();
            return;
        }
        try
        {
            var settings = AppSettings.Current;
            settings.Query.CommandTimeoutSeconds = timeout;
            AppSettings.Save(settings);
            Close(true);
        }
        catch (Exception ex)
        {
            AppLogger.Error(ex, "Failed to save application settings.");
            await DialogService.ShowAsync(this, "Settings Error", ex.Message);
        }
    }

    private void Cancel_OnClick(object? sender, RoutedEventArgs e) => Close(false);
}
