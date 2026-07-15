using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace SSMS
{
    public partial class SettingsWindow : Window
    {
        private const int DwmwaUseImmersiveDarkMode = 20;
        private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(
            System.IntPtr hwnd,
            int attribute,
            ref int attributeValue,
            int attributeSize);

        public SettingsWindow()
        {
            InitializeComponent();
            TxtCommandTimeout.Text = AppSettings.Current.Query.CommandTimeoutSeconds.ToString();
        }

        protected override void OnSourceInitialized(System.EventArgs e)
        {
            base.OnSourceInitialized(e);

            int enabled = 1;
            System.IntPtr hwnd = new WindowInteropHelper(this).Handle;
            int result = DwmSetWindowAttribute(
                hwnd,
                DwmwaUseImmersiveDarkMode,
                ref enabled,
                sizeof(int));

            if (result != 0)
            {
                DwmSetWindowAttribute(
                    hwnd,
                    DwmwaUseImmersiveDarkModeBefore20H1,
                    ref enabled,
                    sizeof(int));
            }
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
