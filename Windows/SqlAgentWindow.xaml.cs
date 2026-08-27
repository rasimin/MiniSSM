using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SSMS
{
    public partial class SqlAgentWindow : Window
    {
        private readonly SqlAgentService _service;
        private readonly DispatcherTimer _autoRefreshTimer;
        private CancellationTokenSource? _detailCancellationSource;
        private bool _refreshInProgress;

        public SqlAgentWindow(string connectionString)
        {
            InitializeComponent();
            _service = new SqlAgentService(connectionString);

            _autoRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _autoRefreshTimer.Tick += AutoRefreshTimer_Tick;
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            await RefreshAsync(selectFirstJob: true);
            _autoRefreshTimer.Start();
        }

        private void Window_Closed(object? sender, EventArgs e)
        {
            _autoRefreshTimer.Stop();
            _detailCancellationSource?.Cancel();
            _detailCancellationSource?.Dispose();
        }

        private async void AutoRefreshTimer_Tick(object? sender, EventArgs e)
        {
            if (AutoRefreshCheckBox.IsChecked == true)
            {
                await RefreshAsync(selectFirstJob: false);
            }
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshAsync(selectFirstJob: false);
        }

        private async void SearchTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await RefreshAsync(selectFirstJob: true);
            }
        }

        private async Task RefreshAsync(bool selectFirstJob)
        {
            if (_refreshInProgress)
            {
                return;
            }

            _refreshInProgress = true;
            OperationText.Text = "Refreshing...";

            try
            {
                SqlAgentServiceStatus? status = null;
                try
                {
                    status = await _service.GetServiceStatusAsync();
                    ApplyServiceStatus(status);
                }
                catch (Exception ex)
                {
                    AppLogger.Error(ex, "Failed to read SQL Server Agent service status.");
                    AgentStatusText.Text = "Unavailable (permission or unsupported instance)";
                    AgentStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xA7, 0x00));
                    AgentStartupText.Text = string.Empty;
                }

                var jobs = await _service.GetJobsAsync(SearchTextBox.Text);
                Guid? selectedJobId = (JobsGrid.SelectedItem as SqlAgentJob)?.JobId;
                JobsGrid.ItemsSource = jobs;
                JobsCountText.Text = $"{jobs.Count} job(s)";

                if (selectFirstJob || !selectedJobId.HasValue)
                {
                    JobsGrid.SelectedIndex = jobs.Count > 0 ? 0 : -1;
                }
                else
                {
                    JobsGrid.SelectedItem = jobs.Find(job => job.JobId == selectedJobId.Value);
                    if (JobsGrid.SelectedItem == null && jobs.Count > 0)
                    {
                        JobsGrid.SelectedIndex = 0;
                    }
                }

                OperationText.Text = status?.IsAvailable == false
                    ? "SQL Server Agent is not available on this instance."
                    : $"Last refreshed {DateTime.Now:HH:mm:ss}";
            }
            catch (OperationCanceledException)
            {
                OperationText.Text = "Refresh canceled";
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "Failed to load SQL Server Agent jobs.");
                OperationText.Text = $"Failed to load jobs: {ex.Message}";
                MessageBox.Show(
                    $"Failed to load SQL Agent jobs:{Environment.NewLine}{ex.Message}",
                    "SQL Agent Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                _refreshInProgress = false;
            }
        }

        private void ApplyServiceStatus(SqlAgentServiceStatus status)
        {
            if (!status.IsAvailable)
            {
                AgentStatusText.Text = "Unavailable";
                AgentStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xA7, 0x00));
                AgentStartupText.Text = status.StatusDescription;
                return;
            }

            AgentStatusText.Text = status.StatusDisplay;
            AgentStatusText.Foreground = new SolidColorBrush(
                status.IsRunning ? Color.FromRgb(0x4E, 0xC9, 0xB0) : Color.FromRgb(0xF1, 0x4C, 0x4C));
            AgentStartupText.Text = status.LastStartupTime.HasValue
                ? $"Started {status.LastStartupTime.Value:yyyy-MM-dd HH:mm:ss}"
                : string.Empty;
        }

        private async void JobsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateActionButtons();
            await LoadSelectedJobDetailsAsync();
        }

        private void JobsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (JobsGrid.SelectedItem is SqlAgentJob)
            {
                DetailTabs.SelectedIndex = 1;
            }
        }

        private void UpdateActionButtons()
        {
            var job = JobsGrid.SelectedItem as SqlAgentJob;
            StartJobButton.IsEnabled = job != null && job.Enabled && !job.IsRunning;
            StopJobButton.IsEnabled = job?.IsRunning == true;
            ToggleJobButton.IsEnabled = job != null;
            ToggleJobButton.Content = job?.Enabled == true ? "Disable Job" : "Enable Job";
        }

        private async Task LoadSelectedJobDetailsAsync()
        {
            _detailCancellationSource?.Cancel();
            _detailCancellationSource?.Dispose();
            _detailCancellationSource = new CancellationTokenSource();
            CancellationToken cancellationToken = _detailCancellationSource.Token;

            if (JobsGrid.SelectedItem is not SqlAgentJob job)
            {
                DetailTitleText.Text = "Select a job";
                DetailMetaText.Text = string.Empty;
                StepsGrid.ItemsSource = null;
                SchedulesGrid.ItemsSource = null;
                HistoryGrid.ItemsSource = null;
                HistoryMessageTextBox.Clear();
                return;
            }

            DetailTitleText.Text = job.Name;
            DetailMetaText.Text = $"{job.LastRunStatusDisplay} · Owner: {job.OwnerName} · {job.ScheduleDisplay}";
            HistoryMessageTextBox.Text = job.LastRunMessage;

            try
            {
                Task<List<SqlAgentJobStep>> stepsTask = _service.GetJobStepsAsync(job.JobId, cancellationToken);
                Task<List<SqlAgentSchedule>> schedulesTask = _service.GetJobSchedulesAsync(job.JobId, cancellationToken);
                Task<List<SqlAgentJobHistory>> historyTask = _service.GetJobHistoryAsync(job.JobId, 100, cancellationToken);
                await Task.WhenAll(stepsTask, schedulesTask, historyTask);
                cancellationToken.ThrowIfCancellationRequested();

                if (JobsGrid.SelectedItem is SqlAgentJob selected && selected.JobId == job.JobId)
                {
                    StepsGrid.ItemsSource = stepsTask.Result;
                    SchedulesGrid.ItemsSource = schedulesTask.Result;
                    HistoryGrid.ItemsSource = historyTask.Result;
                    if (HistoryGrid.SelectedIndex < 0 && HistoryGrid.Items.Count > 0)
                    {
                        HistoryGrid.SelectedIndex = 0;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // A newer row selection replaced this detail request.
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"Failed to load SQL Agent job details for '{job.Name}'.");
                StepsGrid.ItemsSource = null;
                SchedulesGrid.ItemsSource = null;
                HistoryGrid.ItemsSource = null;
                HistoryMessageTextBox.Text = $"Failed to load job details: {ex.Message}";
            }
        }

        private void HistoryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (HistoryGrid.SelectedItem is SqlAgentJobHistory history)
            {
                HistoryMessageTextBox.Text = history.Message;
            }
        }

        private async void StartJobButton_Click(object sender, RoutedEventArgs e)
        {
            if (JobsGrid.SelectedItem is not SqlAgentJob job)
            {
                return;
            }

            await ExecuteJobActionAsync(
                job,
                "start",
                $"Start job '{job.Name}'?",
                () => _service.StartJobAsync(job.JobId));
        }

        private async void StopJobButton_Click(object sender, RoutedEventArgs e)
        {
            if (JobsGrid.SelectedItem is not SqlAgentJob job)
            {
                return;
            }

            await ExecuteJobActionAsync(
                job,
                "stop",
                $"Stop job '{job.Name}'?",
                () => _service.StopJobAsync(job.JobId));
        }

        private async void ToggleJobButton_Click(object sender, RoutedEventArgs e)
        {
            if (JobsGrid.SelectedItem is not SqlAgentJob job)
            {
                return;
            }

            bool enable = !job.Enabled;
            MessageBoxResult confirmation = MessageBox.Show(
                $"{(enable ? "Enable" : "Disable")} job '{job.Name}'?",
                "Confirm SQL Agent Job Change",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirmation != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                await _service.SetJobEnabledAsync(job.JobId, enable);
                OperationText.Text = $"Job {(enable ? "enabled" : "disabled")}: {job.Name}";
                await RefreshAsync(selectFirstJob: false);
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"Failed to {(enable ? "enable" : "disable")} SQL Agent job '{job.Name}'.");
                MessageBox.Show(
                    $"Failed to update job:{Environment.NewLine}{ex.Message}",
                    "SQL Agent Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async Task ExecuteJobActionAsync(
            SqlAgentJob job,
            string action,
            string confirmationText,
            Func<Task> actionCall)
        {
            MessageBoxResult confirmation = MessageBox.Show(
                confirmationText,
                "Confirm SQL Agent Job Action",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirmation != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                await actionCall();
                OperationText.Text = $"Job {action} requested: {job.Name}";
                await RefreshAsync(selectFirstJob: false);
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"Failed to {action} SQL Agent job '{job.Name}'.");
                MessageBox.Show(
                    $"Failed to {action} job:{Environment.NewLine}{ex.Message}",
                    "SQL Agent Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_StateChanged(object? sender, EventArgs e)
        {
            bool isMaximized = WindowState == WindowState.Maximized;
            if (isMaximized)
            {
                Rect workArea = SystemParameters.WorkArea;
                MaxWidth = workArea.Width;
                MaxHeight = workArea.Height;
                WindowCard.Margin = new Thickness(0);
                MaximizeButton.Content = "❐";
                MaximizeButton.ToolTip = "Restore";
            }
            else
            {
                MaxWidth = double.PositiveInfinity;
                MaxHeight = double.PositiveInfinity;
                WindowCard.Margin = new Thickness(8);
                MaximizeButton.Content = "□";
                MaximizeButton.ToolTip = "Maximize / Restore";
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        }
    }
}
