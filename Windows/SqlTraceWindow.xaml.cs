using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Data.SqlClient;

namespace SSMS
{
    public partial class SqlTraceWindow : Window
    {
        private readonly string _connectionString;
        private readonly string? _initialDatabaseName;
        private SqlTraceSession? _traceSession;
        private DispatcherTimer? _pollTimer;
        private bool _pollInProgress;
        private bool _allowClose;
        private bool _closeInProgress;
        private bool _isCustomMaximized;
        private Rect _restoreBounds;
        private long _lastEventSequence;
        private ICollectionView? _traceEventsView;
        private string _searchFilter = string.Empty;

        public ObservableCollection<SqlTraceEvent> TraceEvents { get; } = new();

        public SqlTraceWindow(string connectionString, string? databaseName = null)
        {
            InitializeComponent();
            DataContext = this;
            _connectionString = connectionString;
            _initialDatabaseName = databaseName;
            BtnMaximize.Content = "\u25A1";

            _traceEventsView = CollectionViewSource.GetDefaultView(TraceEvents);
            _traceEventsView.Filter = FilterTraceEvent;
            UpdateSearchUiState(isRunning: false);
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            var choices = new ObservableCollection<TraceDatabaseChoice>
            {
                new("All databases", null)
            };

            try
            {
                foreach (string database in await DatabaseHelper.GetDatabasesAsync(_connectionString))
                {
                    choices.Add(new TraceDatabaseChoice(database, database));
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "Failed to load SQL Trace database choices");
                TxtStatus.Text = $"Could not load database list: {ex.Message}";
            }

            CboDatabase.ItemsSource = choices;
            CboDatabase.SelectedItem = choices.FirstOrDefault(choice =>
                string.Equals(choice.DatabaseName, _initialDatabaseName, StringComparison.OrdinalIgnoreCase)) ?? choices[0];
        }

        private void HeaderGrid_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void BtnMaximize_Click(object sender, RoutedEventArgs e)
        {
            if (_isCustomMaximized)
            {
                Left = _restoreBounds.Left;
                Top = _restoreBounds.Top;
                Width = _restoreBounds.Width;
                Height = _restoreBounds.Height;
                _isCustomMaximized = false;
            }
            else
            {
                _restoreBounds = new Rect(Left, Top, Width, Height);
                Rect workArea = SystemParameters.WorkArea;
                Left = workArea.Left;
                Top = workArea.Top;
                Width = workArea.Width;
                Height = workArea.Height;
                _isCustomMaximized = true;
            }

            UpdateMaximizeButton();
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void Window_StateChanged(object? sender, EventArgs e)
        {
            UpdateMaximizeButton();
        }

        private void UpdateMaximizeButton()
        {
            if (BtnMaximize != null)
            {
                BtnMaximize.Content = _isCustomMaximized
                    ? "\u25A3"
                    : "\u25A1";
            }
        }

        private void TraceGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (TraceGrid.SelectedItem is not SqlTraceEvent traceEvent)
            {
                TxtSelectedEvent.Text = "SQL Text Detail — select an event above";
                TxtSqlDetail.Clear();
                return;
            }

            TxtSelectedEvent.Text =
                $"{traceEvent.EventName} | {traceEvent.StartTime:yyyy-MM-dd HH:mm:ss.fff} | " +
                $"{traceEvent.DatabaseName} | SPID {traceEvent.Spid}";
            TxtSqlDetail.Text = traceEvent.TextData;
            TxtSqlDetail.CaretIndex = 0;
            TxtSqlDetail.ScrollToHome();
        }

        private void BtnOpenNewQuery_Click(object sender, RoutedEventArgs e)
        {
            if (TraceGrid.SelectedItem is not SqlTraceEvent traceEvent || string.IsNullOrWhiteSpace(traceEvent.TextData))
            {
                DarkMessageBoxWindow.Show(
                    this,
                    "Pilih event yang memiliki SQL Text terlebih dahulu.",
                    "Open New Query",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (MainWindow.Instance == null)
            {
                return;
            }

            var databaseChoice = CboDatabase.SelectedItem as TraceDatabaseChoice;
            string databaseName = string.IsNullOrWhiteSpace(traceEvent.DatabaseName)
                ? databaseChoice?.DatabaseName ?? "master"
                : traceEvent.DatabaseName;

            MainWindow.Instance.CreateNewQueryTab(
                _connectionString,
                databaseName,
                traceEvent.TextData,
                $"Trace_{traceEvent.EventName.Replace(':', '_')}.sql");
            MainWindow.Instance.Activate();
        }

        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (_traceSession != null)
            {
                return;
            }

            MessageBoxResult confirmation = DarkMessageBoxWindow.Show(
                this,
                "SQL Trace bersifat legacy/deprecated dan akan menangkap query dari session lain. " +
                "Trace dapat menambah beban server dan SQL text mungkin berisi data sensitif. " +
                "Lanjutkan memulai trace?",
                "Warning — Legacy SQL Trace",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.Yes)
            {
                return;
            }

            BtnStart.IsEnabled = false;
            CboDatabase.IsEnabled = false;
            TxtState.Text = "Starting...";
            TxtStatus.Text = "Creating trace on SQL Server...";

            try
            {
                var choice = CboDatabase.SelectedItem as TraceDatabaseChoice;
                _traceSession = await SqlTraceSession.StartAsync(_connectionString, choice?.DatabaseName);
                _lastEventSequence = 0;
                TraceEvents.Clear();
                TxtSearch.Clear();
                BtnStop.IsEnabled = true;
                TxtState.Text = "Running";
                TxtState.Foreground = System.Windows.Media.Brushes.LightGreen;
                TxtStatus.Text = "Waiting for completed RPC and SQL batch events...";
                TxtTraceFile.Text = $"Trace file: {_traceSession.TraceFilePath}";
                UpdateSearchUiState(isRunning: true);

                _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _pollTimer.Tick += PollTimer_Tick;
                _pollTimer.Start();
                await PollEventsAsync();
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "Failed to start SQL Trace");
                if (_traceSession != null)
                {
                    await _traceSession.StopAsync();
                    _traceSession = null;
                }

                BtnStart.IsEnabled = true;
                CboDatabase.IsEnabled = true;
                TxtState.Text = "Not running";
                TxtState.Foreground = System.Windows.Media.Brushes.Gray;
                TxtStatus.Text = "Failed to start trace.";
                UpdateSearchUiState(isRunning: false);
                DarkMessageBoxWindow.Show(
                    this,
                    $"Trace gagal dimulai: {ex.Message}\n\nPastikan login memiliki permission ALTER TRACE dan SQL Server dapat menulis trace file.",
                    "SQL Trace Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            await StopTraceAsync();
        }

        private void BtnClearTrace_Click(object sender, RoutedEventArgs e)
        {
            TraceEvents.Clear();
            TxtSearch.Clear();
            TraceGrid.SelectedItem = null;
            TxtSelectedEvent.Text = "SQL Text Detail — select an event above";
            TxtSqlDetail.Clear();
            TxtStatus.Text = _traceSession == null
                ? "Display cleared."
                : "Display cleared. Trace is still running.";
            UpdateSearchMatchCount();
        }

        private async void PollTimer_Tick(object? sender, EventArgs e)
        {
            await PollEventsAsync();
        }

        private async Task PollEventsAsync()
        {
            if (_pollInProgress || _traceSession == null)
            {
                return;
            }

            _pollInProgress = true;
            try
            {
                IReadOnlyList<SqlTraceEvent> newEvents = await _traceSession.ReadEventsAsync(_lastEventSequence);
                foreach (SqlTraceEvent traceEvent in newEvents)
                {
                    TraceEvents.Add(traceEvent);
                    _lastEventSequence = Math.Max(_lastEventSequence, traceEvent.EventSequence);
                }

                while (TraceEvents.Count > 5000)
                {
                    TraceEvents.RemoveAt(0);
                }

                if (newEvents.Count > 0)
                {
                    TxtStatus.Text = $"Captured {TraceEvents.Count:N0} event(s). Last update: {DateTime.Now:HH:mm:ss}.";
                    TraceGrid.ScrollIntoView(TraceEvents[^1]);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "Failed to poll SQL Trace events");
                TxtStatus.Text = $"Trace read error: {ex.Message}";
            }
            finally
            {
                _pollInProgress = false;
            }
        }

        private async Task StopTraceAsync()
        {
            _pollTimer?.Stop();
            _pollTimer = null;

            SqlTraceSession? session = _traceSession;
            _traceSession = null;
            if (session != null)
            {
                TxtState.Text = "Stopping...";
                await session.StopAsync();
            }

            BtnStart.IsEnabled = true;
            BtnStop.IsEnabled = false;
            CboDatabase.IsEnabled = true;
            TxtState.Text = "Stopped";
            TxtState.Foreground = System.Windows.Media.Brushes.Gray;
            TxtStatus.Text = "Trace stopped. Captured events remain visible in this window.";
            UpdateSearchUiState(isRunning: false);
            UpdateSearchMatchCount();
        }

        private async void Window_Closing(object? sender, CancelEventArgs e)
        {
            if (_allowClose || _traceSession == null)
            {
                return;
            }

            e.Cancel = true;
            if (_closeInProgress)
            {
                return;
            }

            _closeInProgress = true;
            await StopTraceAsync();
            _allowClose = true;
            Close();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        public async Task StopAndCloseAsync()
        {
            if (_allowClose)
            {
                return;
            }

            _allowClose = true;
            await StopTraceAsync();
            if (IsVisible)
            {
                Close();
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (TxtSearch.IsEnabled)
                {
                    TxtSearch.Focus();
                    TxtSearch.SelectAll();
                    e.Handled = true;
                }
                return;
            }

            if (e.Key == Key.Escape)
            {
                Close();
            }
        }

        private void TxtSearch_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            _searchFilter = TxtSearch.Text;
            BtnClearSearch.Visibility = string.IsNullOrEmpty(_searchFilter) ? Visibility.Collapsed : Visibility.Visible;
            _traceEventsView?.Refresh();
            UpdateSearchMatchCount();

            if (TraceGrid.SelectedItem == null && _traceEventsView != null)
            {
                var first = _traceEventsView.Cast<SqlTraceEvent>().FirstOrDefault();
                if (first != null)
                {
                    TraceGrid.SelectedItem = first;
                }
            }
        }

        private void TxtSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (!string.IsNullOrEmpty(TxtSearch.Text))
                {
                    TxtSearch.Clear();
                    e.Handled = true;
                }
            }
        }

        private void BtnClearSearch_Click(object sender, RoutedEventArgs e)
        {
            TxtSearch.Clear();
            TxtSearch.Focus();
        }

        private bool FilterTraceEvent(object item)
        {
            if (item is not SqlTraceEvent ev) return false;
            if (string.IsNullOrWhiteSpace(_searchFilter)) return true;

            string[] terms = _searchFilter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (terms.Length == 0) return true;

            for (int i = 0; i < terms.Length; i++)
            {
                string term = terms[i];
                bool matched =
                    (!string.IsNullOrEmpty(ev.TextData) && ev.TextData.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (!string.IsNullOrEmpty(ev.EventName) && ev.EventName.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (!string.IsNullOrEmpty(ev.DatabaseName) && ev.DatabaseName.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (!string.IsNullOrEmpty(ev.LoginName) && ev.LoginName.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (!string.IsNullOrEmpty(ev.HostName) && ev.HostName.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (!string.IsNullOrEmpty(ev.ApplicationName) && ev.ApplicationName.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    ev.Spid.ToString().Contains(term, StringComparison.OrdinalIgnoreCase);

                if (!matched) return false;
            }

            return true;
        }

        private void UpdateSearchMatchCount()
        {
            int total = TraceEvents.Count;
            if (string.IsNullOrWhiteSpace(_searchFilter))
            {
                SearchCountBadge.Visibility = Visibility.Collapsed;
                if (_traceSession == null && total > 0)
                {
                    TxtStatus.Text = $"Trace stopped. Showing all {total:N0} captured event(s).";
                }
                return;
            }

            int matches = 0;
            if (_traceEventsView != null)
            {
                foreach (var _ in _traceEventsView) matches++;
            }

            SearchCountBadge.Visibility = Visibility.Visible;
            TxtSearchCount.Text = $"{matches} / {total}";

            if (matches == 0)
            {
                TxtStatus.Text = $"Tidak ada event yang cocok dengan '{_searchFilter}' (dari {total:N0} total event).";
            }
            else
            {
                TxtStatus.Text = $"Menampilkan {matches:N0} dari {total:N0} event yang cocok dengan '{_searchFilter}'.";
            }
        }

        private void UpdateSearchUiState(bool isRunning)
        {
            if (isRunning)
            {
                TxtSearch.Clear();
                SearchBorder.IsEnabled = false;
                SearchBorder.Opacity = 0.45;
                SearchBorder.ToolTip = "Pencarian dinonaktifkan saat trace sedang berjalan. Hentikan trace untuk mencari hasil.";
            }
            else
            {
                SearchBorder.IsEnabled = true;
                SearchBorder.Opacity = 1.0;
                SearchBorder.ToolTip = "Cari event hasil trace berdasarkan SQL Text, database, login, event, atau SPID (Ctrl+F)";
            }
        }

        private sealed class TraceDatabaseChoice
        {
            public TraceDatabaseChoice(string displayName, string? databaseName)
            {
                DisplayName = displayName;
                DatabaseName = databaseName;
            }

            public string DisplayName { get; }
            public string? DatabaseName { get; }
        }
    }
}
