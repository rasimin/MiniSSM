using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace SSMS
{
    public partial class QueryHistoryWindow : Window
    {
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DwmwaUseImmersiveDarkMode = 20;
        public event EventHandler<QueryHistoryEntry>? OpenInNewQueryRequested;

        public QueryHistoryWindow()
        {
            InitializeComponent();
            Loaded += async (_, _) => await RefreshHistoryAsync();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try
            {
                IntPtr handle = new WindowInteropHelper(this).Handle;
                int darkMode = 1;
                DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int));
            }
            catch
            {
                // Dark title bar is best effort on older Windows versions.
            }
        }

        private async System.Threading.Tasks.Task RefreshHistoryAsync()
        {
            try
            {
                if (FromDatePicker.SelectedDate.HasValue &&
                    ToDatePicker.SelectedDate.HasValue &&
                    FromDatePicker.SelectedDate.Value.Date > ToDatePicker.SelectedDate.Value.Date)
                {
                    MessageBox.Show(
                        "The From date cannot be later than the To date.",
                        "Invalid Date Range",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                DateTimeOffset? fromUtc = ToUtcBoundary(FromDatePicker.SelectedDate, addOneDay: false);
                DateTimeOffset? beforeUtc = ToUtcBoundary(ToDatePicker.SelectedDate, addOneDay: true);
                CountText.Text = "Loading...";
                var entries = await QueryHistoryService.GetLatestAsync(
                    300,
                    fromUtc,
                    beforeUtc,
                    DatabaseFilterTextBox.Text,
                    SqlFilterTextBox.Text);
                HistoryGrid.ItemsSource = entries;
                CountText.Text = $"{entries.Count} matching entries (maximum 300)";
                if (entries.Count > 0)
                {
                    HistoryGrid.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "Failed to load query execution history.");
                CountText.Text = "Failed to load history";
                MessageBox.Show(
                    $"Failed to load query history: {ex.Message}",
                    "Query History Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private static DateTimeOffset? ToUtcBoundary(DateTime? selectedDate, bool addOneDay)
        {
            if (!selectedDate.HasValue)
            {
                return null;
            }

            DateTime localDate = selectedDate.Value.Date;
            if (addOneDay)
            {
                localDate = localDate.AddDays(1);
            }
            return new DateTimeOffset(localDate, TimeZoneInfo.Local.GetUtcOffset(localDate)).ToUniversalTime();
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshHistoryAsync();
        }

        private async void ApplyFilterButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshHistoryAsync();
        }

        private async void ClearFilterButton_Click(object sender, RoutedEventArgs e)
        {
            FromDatePicker.SelectedDate = null;
            ToDatePicker.SelectedDate = null;
            DatabaseFilterTextBox.Clear();
            SqlFilterTextBox.Clear();
            await RefreshHistoryAsync();
        }

        private async void FilterTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await RefreshHistoryAsync();
            }
        }

        private void HistoryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (HistoryGrid.SelectedItem is QueryHistoryEntry entry)
            {
                QueryTextBox.Text = entry.QueryText;
                MessageTextBox.Text = entry.ResultMessage;
                OpenButton.IsEnabled = true;
                CopyQueryButton.IsEnabled = true;
            }
            else
            {
                QueryTextBox.Clear();
                MessageTextBox.Clear();
                OpenButton.IsEnabled = false;
                CopyQueryButton.IsEnabled = false;
            }
        }

        private void CopyQueryButton_Click(object sender, RoutedEventArgs e)
        {
            if (HistoryGrid.SelectedItem is not QueryHistoryEntry entry)
            {
                return;
            }

            try
            {
                Clipboard.SetText(entry.QueryText);
                CountText.Text = "Executed query copied to clipboard";
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "Failed to copy query history text.");
                MessageBox.Show(
                    $"Failed to copy query: {ex.Message}",
                    "Copy Query Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private void HistoryGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            OpenSelectedEntry();
        }

        private void OpenButton_Click(object sender, RoutedEventArgs e)
        {
            OpenSelectedEntry();
        }

        private void OpenSelectedEntry()
        {
            if (HistoryGrid.SelectedItem is QueryHistoryEntry entry)
            {
                OpenInNewQueryRequested?.Invoke(this, entry);
            }
        }
    }
}
