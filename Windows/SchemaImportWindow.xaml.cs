using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
            ResultsGrid.ItemsSource = null;
            DetailTextBox.Clear();
            SaveReportButton.IsEnabled = false;

            try
            {
                _plan = await _service.AnalyzeAsync(filePath, cancellationSource.Token);
                _results = CreatePendingResults(_plan);
                ResultsGrid.ItemsSource = _results;
                if (IsCreateNewDatabaseSelected() && string.IsNullOrWhiteSpace(NewDatabaseNameTextBox.Text))
                {
                    NewDatabaseNameTextBox.Text = _plan.ScriptDatabaseName;
                }
                SummaryText.Text = BuildPlanSummary(_plan);
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

            var progress = new Progress<SchemaImportProgress>(UpdateProgress);
            try
            {
                _results = await _service.ImportAsync(
                    _plan,
                    _connectionString,
                    databaseName,
                    createNewDatabase,
                    progress,
                    cancellationSource.Token);
                ResultsGrid.ItemsSource = _results;
                SummaryText.Text = BuildResultSummary(_plan, _results, databaseName);
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

        private void UpdateProgress(SchemaImportProgress progress)
        {
            ImportProgressBar.Value = progress.Percentage;
            ProgressText.Text = progress.Message;

            if (progress.Result != null)
            {
                SchemaImportItemResult? row = _results.FirstOrDefault(item => item.BatchIndex == progress.Result.BatchIndex);
                if (row != null)
                {
                    row.Status = progress.Result.Status;
                    row.Attempts = progress.Result.Attempts;
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
            ResultsGrid.ItemsSource = null;
            DetailTextBox.Clear();
            SummaryText.Text = "Choose a SQL file and analyze it before importing.";
            ProgressText.Text = "Ready";
            ImportProgressBar.Value = 0;
            ImportButton.IsEnabled = false;
            SaveReportButton.IsEnabled = false;
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
            MaximizeButton.Content = isMaximized ? "❐" : "□";
            MaximizeButton.ToolTip = isMaximized ? "Restore" : "Maximize";
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
