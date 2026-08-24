using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Data.SqlClient;
using SSMS.Utilities;

namespace SSMS
{
    public partial class SchemaImportWindow : Window
    {
        private const string CreateNewDatabaseOption = "(Create new database...)";
        private readonly string _connectionString;
        private readonly string _initialDatabaseName;
        private readonly SchemaImportService _service = new();
        private CancellationTokenSource? _operationCancellationSource;
        private SchemaImportPlan? _plan;
        private List<SchemaImportItemResult> _results = new();
        private ICollectionView? _resultsView;
        private string _lastImportDatabaseName = string.Empty;
        private IReadOnlyDictionary<int, int>? _rerunAttemptOffsets;
        private bool _isBusy;

        public SchemaImportWindow(string connectionString, string databaseName)
        {
            InitializeComponent();
            _connectionString = connectionString;
            _initialDatabaseName = databaseName;

            try
            {
                ServerTextBox.Text = new SqlConnectionStringBuilder(connectionString).DataSource;
            }
            catch
            {
                ServerTextBox.Text = "Current connection";
            }

            DatabaseComboBox.Text = databaseName;
            Loaded += Window_Loaded;
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                List<string> databases = await DatabaseHelper.GetDatabasesAsync(_connectionString);
                var databaseOptions = databases
                    .Where(database => !string.IsNullOrWhiteSpace(database))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (!string.IsNullOrWhiteSpace(_initialDatabaseName) &&
                    !databaseOptions.Any(database => string.Equals(database, _initialDatabaseName, StringComparison.OrdinalIgnoreCase)))
                {
                    databaseOptions.Insert(0, _initialDatabaseName);
                }
                databaseOptions.Add(CreateNewDatabaseOption);
                DatabaseComboBox.ItemsSource = databaseOptions;
                if (databaseOptions.Any(database => string.Equals(database, _initialDatabaseName, StringComparison.OrdinalIgnoreCase)))
                {
                    DatabaseComboBox.SelectedItem = databaseOptions.First(database => string.Equals(database, _initialDatabaseName, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    DatabaseComboBox.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "Failed to load databases for schema import.");
                ProgressText.Text = "Database list unavailable; enter the target database manually.";
            }
        }

        private async void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                (bool result, string[] fileNames) = await FileDialogHelper.ShowOpenFileDialogAsync(
                    "SQL files (*.sql)|*.sql|All files (*.*)|*.*",
                    ".sql",
                    "Select schema SQL file",
                    false,
                    null,
                    this);

                if (result && fileNames.Length > 0)
                {
                    FilePathTextBox.Text = fileNames[0];
                    ResetAnalysis();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "Failed to open schema SQL file dialog.");
                MessageBox.Show($"Failed to select SQL file: {ex.Message}", "Import Schema", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void AnalyzeButton_Click(object sender, RoutedEventArgs e)
        {
            string filePath = FilePathTextBox.Text.Trim();
            if (!File.Exists(filePath))
            {
                MessageBox.Show("Select a valid .sql file first.", "Import Schema", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _operationCancellationSource?.Cancel();
            using var cancellationSource = new CancellationTokenSource();
            _operationCancellationSource = cancellationSource;
            SetBusy(true, "Analyzing SQL file...");
            _plan = null;
            _results.Clear();
            _lastImportDatabaseName = string.Empty;
            _resultsView = null;
            ResultsGrid.ItemsSource = null;
            DetailTextBox.Clear();
            ReportTextBox.Clear();
            SaveReportButton.IsEnabled = false;
            RerunFailedButton.IsEnabled = false;

            try
            {
                _plan = await _service.AnalyzeAsync(filePath, cancellationSource.Token);
                _results = CreatePendingResults(_plan);
                SetResultsView();
                if (IsCreateNewDatabaseSelected() && string.IsNullOrWhiteSpace(NewDatabaseNameTextBox.Text))
                {
                    NewDatabaseNameTextBox.Text = _plan.ScriptDatabaseName;
                }
                SummaryText.Text = BuildPlanSummary(_plan);
                ReportTextBox.Text = BuildAnalysisReport(_plan);
                ImportTabs.SelectedItem = ReportTab;
                ImportButton.IsEnabled = _plan.Batches.Count > 0;
                ProgressText.Text = "Analysis completed. Review the plan before importing.";
                ImportProgressBar.Value = 0;
            }
            catch (OperationCanceledException)
            {
                ProgressText.Text = "Analysis cancelled.";
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "Schema script analysis failed.");
                SummaryText.Text = "Analysis failed.";
                ProgressText.Text = ex.Message;
                MessageBox.Show($"Schema analysis failed: {ex.Message}", "Import Schema", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (ReferenceEquals(_operationCancellationSource, cancellationSource))
                {
                    _operationCancellationSource = null;
                }
                SetBusy(false, ProgressText.Text);
                ImportButton.IsEnabled = _plan?.Batches.Count > 0;
            }
        }

        private async void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_plan == null || _plan.Batches.Count == 0)
            {
                MessageBox.Show("Analyze a SQL file before importing.", "Import Schema", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            bool createNewDatabase = IsCreateNewDatabaseSelected();
            string databaseName = createNewDatabase
                ? NewDatabaseNameTextBox.Text.Trim()
                : GetSelectedDatabaseName();
            if (string.IsNullOrWhiteSpace(databaseName))
            {
                MessageBox.Show(
                    createNewDatabase ? "Enter a new database name." : "Select a target database.",
                    "Import Schema",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                if (createNewDatabase) NewDatabaseNameTextBox.Focus(); else DatabaseComboBox.Focus();
                return;
            }

            MessageBoxResult confirmation = MessageBox.Show(
                createNewDatabase
                    ? $"Create database '{databaseName}' and import {_plan.Batches.Count:N0} SQL batches into it?"
                    : $"Import {_plan.Batches.Count:N0} SQL batches into database '{databaseName}'?\n\nExisting objects are not automatically dropped.",
                "Confirm Schema Import",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.Yes)
            {
                return;
            }

            _operationCancellationSource?.Cancel();
            using var cancellationSource = new CancellationTokenSource();
            _operationCancellationSource = cancellationSource;
            SetBusy(true, "Starting schema import...");
            SaveReportButton.IsEnabled = false;

            var progress = new Progress<SchemaImportProgress>(value => UpdateProgress(value));
            try
            {
                _results = await _service.ImportAsync(
                    _plan,
                    _connectionString,
                    databaseName,
                    createNewDatabase,
                    progress,
                    cancellationSource.Token);
                _lastImportDatabaseName = databaseName;
                SetResultsView();
                SummaryText.Text = BuildResultSummary(_plan, _results, databaseName);
                ReportTextBox.Text = BuildImportReport(_plan, _results, databaseName);
                ImportTabs.SelectedItem = ReportTab;
                SaveReportButton.IsEnabled = true;
                ProgressText.Text = "Schema import completed. Review failed objects and save the report if needed.";
            }
            catch (OperationCanceledException)
            {
                ProgressText.Text = "Schema import cancelled.";
                SummaryText.Text = "Schema import was cancelled. Completed batches remain in the target database.";
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "Schema import failed unexpectedly.");
                ProgressText.Text = ex.Message;
                MessageBox.Show($"Schema import failed: {ex.Message}", "Import Schema", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (ReferenceEquals(_operationCancellationSource, cancellationSource))
                {
                    _operationCancellationSource = null;
                }
                SetBusy(false, ProgressText.Text);
                SaveReportButton.IsEnabled = _results.Any(result => result.Status != SchemaImportStatus.Pending);
            }
        }

        private async void RerunFailedButton_Click(object sender, RoutedEventArgs e)
        {
            if (_plan == null || _results.Count == 0)
            {
                return;
            }

            List<SchemaImportItemResult> failedRows = _results
                .Where(result => result.Status == SchemaImportStatus.Failed)
                .ToList();
            if (failedRows.Count == 0)
            {
                MessageBox.Show("There are no failed objects to rerun.", "Rerun Failed", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string databaseName = _lastImportDatabaseName;
            if (string.IsNullOrWhiteSpace(databaseName))
            {
                databaseName = GetSelectedDatabaseName();
            }

            MessageBoxResult confirmation = MessageBox.Show(
                $"Rerun {failedRows.Count:N0} failed object(s) against database '{databaseName}'?",
                "Confirm Rerun Failed",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.Yes)
            {
                return;
            }

            _operationCancellationSource?.Cancel();
            using var cancellationSource = new CancellationTokenSource();
            _operationCancellationSource = cancellationSource;
            _rerunAttemptOffsets = failedRows.ToDictionary(row => row.BatchIndex, row => row.Attempts);
            SetBusy(true, "Rerunning failed objects...");

            var progress = new Progress<SchemaImportProgress>(value => UpdateProgress(value, _rerunAttemptOffsets));
            try
            {
                List<SchemaImportItemResult> rerunResults = await _service.RerunFailedAsync(
                    _plan,
                    failedRows,
                    _connectionString,
                    databaseName,
                    progress,
                    cancellationSource.Token);

                ApplyRerunResults(rerunResults);
                ReportTextBox.AppendText(
                    $"\r\n\r\n===== RERUN FAILED ({DateTime.Now:yyyy-MM-dd HH:mm:ss}) =====\r\n" +
                    SchemaImportService.BuildReportText(_plan, rerunResults, databaseName));
                ImportTabs.SelectedItem = ReportTab;
                SummaryText.Text = BuildResultSummary(_plan, _results, databaseName);
                ProgressText.Text = "Rerun completed. Review the updated results and report.";
            }
            catch (OperationCanceledException)
            {
                ProgressText.Text = "Rerun cancelled.";
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "Failed to rerun failed schema objects.");
                ProgressText.Text = ex.Message;
                MessageBox.Show($"Rerun failed: {ex.Message}", "Rerun Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _rerunAttemptOffsets = null;
                if (ReferenceEquals(_operationCancellationSource, cancellationSource))
                {
                    _operationCancellationSource = null;
                }
                SetBusy(false, ProgressText.Text);
            }
        }

        private async void SaveReportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_plan == null || _results.Count == 0)
            {
                return;
            }

            try
            {
                string defaultFileName = $"schema-import-report-{DateTime.Now:yyyyMMdd-HHmmss}.txt";
                (bool result, string? filePath) = await FileDialogHelper.ShowSaveFileDialogAsync(
                    "Text report (*.txt)|*.txt|All files (*.*)|*.*",
                    ".txt",
                    "Save schema import report",
                    defaultFileName,
                    null,
                    this);

                if (result && !string.IsNullOrWhiteSpace(filePath))
                {
                    string report = SchemaImportService.BuildReportText(_plan, _results, GetSelectedDatabaseName());
                    if (!string.IsNullOrWhiteSpace(ReportTextBox.Text))
                    {
                        report = ReportTextBox.Text;
                    }
                    await File.WriteAllTextAsync(filePath, report);
                    ProgressText.Text = $"Report saved: {filePath}";
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "Failed to save schema import report.");
                MessageBox.Show($"Failed to save report: {ex.Message}", "Import Schema", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ResultsGrid.SelectedItem is not SchemaImportItemResult item)
            {
                DetailTextBox.Clear();
                return;
            }

            DetailTextBox.Text =
                $"{item.DisplayName} ({item.ObjectType}) | Status: {item.Status} | Source line: {item.SourceLine}\r\n" +
                $"Dependencies: {item.DependencyText}\r\n" +
                $"Error: {item.ErrorMessage}\r\n\r\n" +
                item.SqlText.Trim();
        }

        private void UpdateProgress(SchemaImportProgress progress, IReadOnlyDictionary<int, int>? attemptOffsets = null)
        {
            ImportProgressBar.Value = progress.Percentage;
            ProgressText.Text = progress.Message;

            if (progress.Result != null)
            {
                SchemaImportItemResult? row = _results.FirstOrDefault(item => item.BatchIndex == progress.Result.BatchIndex);
                if (row != null)
                {
                    row.Status = progress.Result.Status;
                    int attemptOffset = attemptOffsets != null && attemptOffsets.TryGetValue(progress.Result.BatchIndex, out int offset)
                        ? offset
                        : 0;
                    row.Attempts = attemptOffset + progress.Result.Attempts;
                    row.ErrorMessage = progress.Result.ErrorMessage;
                }
                ResultsGrid.Items.Refresh();
            }
        }

        private void SetBusy(bool busy, string message)
        {
            _isBusy = busy;
            AnalyzeButton.IsEnabled = !busy;
            ImportButton.IsEnabled = !busy && _plan?.Batches.Count > 0;
            RerunFailedButton.IsEnabled = !busy && _results.Any(result => result.Status == SchemaImportStatus.Failed);
            SaveReportButton.IsEnabled = !busy && _results.Any(result => result.Status != SchemaImportStatus.Pending);
            DatabaseComboBox.IsEnabled = !busy;
            NewDatabaseNameTextBox.IsEnabled = !busy;
            CancelButton.Content = busy ? "Cancel" : "Close";
            if (!string.IsNullOrWhiteSpace(message))
            {
                ProgressText.Text = message;
            }
        }

        private void ResetAnalysis()
        {
            _plan = null;
            _results.Clear();
            _resultsView = null;
            _lastImportDatabaseName = string.Empty;
            ResultsGrid.ItemsSource = null;
            DetailTextBox.Clear();
            ReportTextBox.Clear();
            SummaryText.Text = "Choose a SQL file and analyze it before importing.";
            ProgressText.Text = "Ready";
            ImportProgressBar.Value = 0;
            ImportButton.IsEnabled = false;
            RerunFailedButton.IsEnabled = false;
            SaveReportButton.IsEnabled = false;
        }

        private void SetResultsView()
        {
            _resultsView = new ListCollectionView(_results);
            _resultsView.Filter = MatchesResultFilter;
            ResultsGrid.ItemsSource = _resultsView;
            UpdateFilterSummary();
        }

        private bool MatchesResultFilter(object item)
        {
            if (item is not SchemaImportItemResult result)
            {
                return false;
            }

            string selectedStatus = (StatusFilterComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All";
            if (!string.Equals(selectedStatus, "All", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(result.Status.ToString(), selectedStatus, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string filterText = FilterTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(filterText))
            {
                return true;
            }

            string searchableText = string.Join(" ",
                result.Status,
                result.DisplayName,
                result.ObjectType,
                result.SourceLine,
                result.Attempts,
                result.ErrorMessage,
                result.DependencyText);
            return searchableText.Contains(filterText, StringComparison.OrdinalIgnoreCase);
        }

        private void FilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _resultsView?.Refresh();
            UpdateFilterSummary();
        }

        private void StatusFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _resultsView?.Refresh();
            UpdateFilterSummary();
        }

        private void UpdateFilterSummary()
        {
            // SelectionChanged can fire while InitializeComponent is still creating named controls.
            if (FilterSummaryText == null)
            {
                return;
            }

            if (_resultsView == null)
            {
                FilterSummaryText.Text = string.Empty;
                return;
            }

            int visibleCount = _resultsView.Cast<SchemaImportItemResult>().Count();
            int failedCount = _results.Count(result => result.Status == SchemaImportStatus.Failed);
            FilterSummaryText.Text = $"Showing {visibleCount:N0} of {_results.Count:N0} | Failed: {failedCount:N0}";
        }

        private void ApplyRerunResults(IEnumerable<SchemaImportItemResult> rerunResults)
        {
            foreach (SchemaImportItemResult rerunResult in rerunResults)
            {
                SchemaImportItemResult? row = _results.FirstOrDefault(result => result.BatchIndex == rerunResult.BatchIndex);
                if (row == null)
                {
                    continue;
                }

                int previousAttempts = _rerunAttemptOffsets != null &&
                                        _rerunAttemptOffsets.TryGetValue(rerunResult.BatchIndex, out int offset)
                    ? offset
                    : row.Attempts - rerunResult.Attempts;
                row.Status = rerunResult.Status == SchemaImportStatus.Success
                    ? SchemaImportStatus.Retried
                    : rerunResult.Status;
                row.Attempts = previousAttempts + rerunResult.Attempts;
                row.ErrorMessage = rerunResult.ErrorMessage;
            }

            _resultsView?.Refresh();
            UpdateFilterSummary();
        }

        private static string BuildAnalysisReport(SchemaImportPlan plan)
        {
            var sb = new StringBuilder();
            sb.AppendLine("MiniSSMS Schema Analysis Report");
            sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"File: {plan.FilePath}");
            sb.AppendLine($"File size: {plan.FileLength:N0} bytes");
            sb.AppendLine();
            sb.AppendLine($"Batches: {plan.Batches.Count:N0}");
            sb.AppendLine($"Execution units: {plan.TotalRepeatUnits:N0}");
            sb.AppendLine($"Detected database: {plan.ScriptDatabaseName}");
            sb.AppendLine();
            sb.AppendLine("Objects by type:");
            foreach (SchemaImportObjectType type in Enum.GetValues<SchemaImportObjectType>())
            {
                int count = plan.Count(type);
                if (count > 0)
                {
                    sb.AppendLine($"  {type}: {count:N0}");
                }
            }

            if (plan.Warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Warnings:");
                foreach (string warning in plan.Warnings)
                {
                    sb.AppendLine($"- {warning}");
                }
            }

            return sb.ToString();
        }

        private static string BuildImportReport(
            SchemaImportPlan plan,
            IEnumerable<SchemaImportItemResult> results,
            string databaseName)
        {
            return SchemaImportService.BuildReportText(plan, results, databaseName);
        }

        private static List<SchemaImportItemResult> CreatePendingResults(SchemaImportPlan plan)
        {
            return plan.Batches.Select(batch => new SchemaImportItemResult
            {
                BatchIndex = batch.Index,
                ObjectName = batch.DisplayName,
                ObjectType = batch.TypeName,
                SourceLine = batch.StartLineNumber,
                Phase = batch.Phase,
                Status = SchemaImportStatus.Pending,
                DependencyText = string.Join(", ", batch.Dependencies),
                SqlText = batch.Text
            }).ToList();
        }

        private static string BuildPlanSummary(SchemaImportPlan plan)
        {
            string types = string.Join(", ", Enum.GetValues<SchemaImportObjectType>()
                .Where(type => type != SchemaImportObjectType.Unknown && plan.Count(type) > 0)
                .Select(type => $"{type}: {plan.Count(type)}"));
            string warningText = plan.Warnings.Count == 0 ? string.Empty : $" Warnings: {plan.Warnings.Count}.";
            string databaseText = string.IsNullOrWhiteSpace(plan.ScriptDatabaseName)
                ? string.Empty
                : $" Script contains CREATE DATABASE [{plan.ScriptDatabaseName}].";
            return $"Analyzed {plan.Batches.Count:N0} batches ({plan.TotalRepeatUnits:N0} execution units). {types}.{databaseText}{warningText}";
        }

        private static string BuildResultSummary(SchemaImportPlan plan, IEnumerable<SchemaImportItemResult> resultRows, string databaseName)
        {
            List<SchemaImportItemResult> rows = resultRows.ToList();
            int successful = rows.Count(row => row.Status is SchemaImportStatus.Success or SchemaImportStatus.Retried);
            int skipped = rows.Count(row => row.Status == SchemaImportStatus.Skipped);
            int failed = rows.Count(row => row.Status == SchemaImportStatus.Failed);
            return $"Target: {databaseName} | Success: {successful:N0} | Skipped: {skipped:N0} | Failed: {failed:N0} | Planned: {plan.Batches.Count:N0}";
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy)
            {
                _operationCancellationSource?.Cancel();
                ProgressText.Text = "Canceling...";
                return;
            }

            Close();
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        private void Window_StateChanged(object? sender, EventArgs e)
        {
            bool isMaximized = WindowState == WindowState.Maximized;
            ApplyWindowStateLayout(isMaximized);
            MaximizeButton.Content = isMaximized ? "❐" : "□";
            MaximizeButton.ToolTip = isMaximized ? "Restore" : "Maximize";
        }

        private void ApplyWindowStateLayout(bool isMaximized)
        {
            if (isMaximized)
            {
                Rect workArea = SystemParameters.WorkArea;
                MaxWidth = workArea.Width;
                MaxHeight = workArea.Height;
                WindowCard.Margin = new Thickness(0);
            }
            else
            {
                MaxWidth = double.PositiveInfinity;
                MaxHeight = double.PositiveInfinity;
                WindowCard.Margin = new Thickness(8);
            }
        }

        private void DatabaseComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            bool createNew = IsCreateNewDatabaseSelected();
            NewDatabasePanel.Visibility = createNew ? Visibility.Visible : Visibility.Collapsed;
            if (createNew && _plan != null && string.IsNullOrWhiteSpace(NewDatabaseNameTextBox.Text))
            {
                NewDatabaseNameTextBox.Text = _plan.ScriptDatabaseName;
            }
        }

        private bool IsCreateNewDatabaseSelected()
        {
            return string.Equals(DatabaseComboBox.SelectedItem?.ToString(), CreateNewDatabaseOption, StringComparison.Ordinal);
        }

        private string GetSelectedDatabaseName()
        {
            return IsCreateNewDatabaseSelected()
                ? NewDatabaseNameTextBox.Text.Trim()
                : DatabaseComboBox.SelectedItem?.ToString()?.Trim() ?? string.Empty;
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                CloseButton_Click(sender, e);
                e.Handled = true;
            }
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }
    }
}
