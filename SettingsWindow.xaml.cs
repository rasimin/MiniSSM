using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;

namespace SSMS
{
    public partial class SettingsWindow : Window
    {
        public SettingsWindow()
        {
            InitializeComponent();
            TxtCommandTimeout.Text = AppSettings.Current.Query.CommandTimeoutSeconds.ToString();
        }

        private void TxtCommandTimeout_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = Regex.IsMatch(e.Text, "[^0-9]+");
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(TxtCommandTimeout.Text, out int timeout) || timeout < 0)
            {
                MessageBox.Show(
                    "Command timeout must be a whole number of seconds (0 or greater).",
                    "Invalid Setting",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                TxtCommandTimeout.Focus();
                TxtCommandTimeout.SelectAll();
                return;
            }

            try
            {
                var settings = AppSettings.Current;
                settings.Query.CommandTimeoutSeconds = timeout;
                AppSettings.Save(settings);
                DialogResult = true;
                Close();
            }
            catch (System.Exception ex)
            {
                AppLogger.Error(ex, "Failed to save application settings.");
                MessageBox.Show(
                    $"Failed to save settings: {ex.Message}",
                    "Settings Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }
}
