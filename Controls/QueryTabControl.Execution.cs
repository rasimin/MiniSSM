using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Data;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Forms.Integration;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using Microsoft.Web.WebView2.Core;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace SSMS
{
    public partial class QueryTabControl : UserControl
    {

        public async void ExecuteQuery(QueryExecutionMode mode = QueryExecutionMode.Execute)
        {
            if (!IsWebViewInitialized || _queryCancellationSource != null) return;

            string sqlQuery = "";
            try
            {
                string resultJson = await SqlEditorWebView.ExecuteScriptAsync("getQueryText()");
                sqlQuery = JsonSerializer.Deserialize<string>(resultJson) ?? "";
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "Failed to fetch SQL query from editor");
                MessageBox.Show($"Failed to fetch SQL query from editor: {ex.Message}", "Editor Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(sqlQuery))
            {
                MessageBox.Show("Please type an SQL query to execute.", "Empty Query", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Clear UI previous records
            DisposeDisplayedResults();
            ClearExecutionPlanTabs();
            TxtMessages.Text = "";
            var cancellationSource = new CancellationTokenSource();
            _queryCancellationSource = cancellationSource;
            LoadingMessageText.Text = mode switch
            {
                QueryExecutionMode.Parse => "Checking SQL syntax...",
                QueryExecutionMode.EstimatedPlan => "Generating estimated execution plan...",
                QueryExecutionMode.ActualPlan => "Executing query with actual plan...",
                _ => "Executing query..."
            };
            CancelQueryButton.Content = "Cancel query";
            CancelQueryButton.IsEnabled = true;
            LoadingOverlay.Visibility = Visibility.Visible;
            TabResults.SelectedIndex = 1;

            var messageProgress = new Progress<string>(AppendLiveQueryMessage);

            var mainWindow = Window.GetWindow(this) as MainWindow;
            mainWindow?.UpdateStatusText(LoadingMessageText.Text);
            string startedDatabaseName = DatabaseName;
            DateTimeOffset executionStartedAt = DateTimeOffset.UtcNow;
            bool historyRecorded = false;

            try
            {
                var result = await DatabaseHelper.ExecuteQueryAsync(
                    ConnectionString,
                    DatabaseName,
                    sqlQuery,
                    messageProgress,
                    cancellationSource.Token,
                    mode);

                if (mode is QueryExecutionMode.Execute or QueryExecutionMode.ActualPlan)
                {
                    await RecordQueryHistoryAsync(sqlQuery, startedDatabaseName, executionStartedAt, result);
                    historyRecorded = true;
                }

                // Populate Results Pane
                if (result.IsCancelled)
                {
                    TotalResultRows = 0;
                    TotalResultColumns = 0;
                    AppendLiveQueryMessage(result.Message);
                    TabResults.SelectedIndex = 1;
                    mainWindow?.UpdateStatusText("Query cancelled.");
                    mainWindow?.UpdateStatusTime($"Cancelled: {result.ExecutionTime.TotalMilliseconds:F2} ms");
                    mainWindow?.UpdateStatusRowsAndColumns(0, 0);
                }
                else if (result.IsSuccess)
                {
                    if (!string.IsNullOrWhiteSpace(result.EffectiveDatabaseName) &&
                        !string.Equals(DatabaseName, result.EffectiveDatabaseName, StringComparison.OrdinalIgnoreCase))
                    {
                        DatabaseName = result.EffectiveDatabaseName;
                        if (mainWindow != null)
                        {
                            await mainWindow.SyncDatabaseContextAsync(this);
                        }
                        await CacheAndRefreshAutocompleteAsync();
                    }

                    TxtMessages.Text = result.Message;
                    if (result.DataTables != null && result.DataTables.Count > 0)
                    {
                        TotalResultRows = 0;
                        TotalResultColumns = 0;
                        foreach (var dt in result.DataTables)
                        {
                            TotalResultRows += dt.Rows.Count;
                            TotalResultColumns += dt.Columns.Count;
                        }

                        DisplayQueryResults(result.DataTables);
                        TabResults.SelectedIndex = 0; // Select Results DataGrid Tab
                    }
                    else
                    {
                        TotalResultRows = 0;
                        TotalResultColumns = 0;
                        TabResults.SelectedIndex = 1; // Select Messages Textbox Tab
                    }
                    if (result.ExecutionPlans.Count > 0)
                    {
                        DisplayExecutionPlans(result.ExecutionPlans, mode);
                    }

                    mainWindow?.UpdateStatusText(mode switch
                    {
                        QueryExecutionMode.Parse => "Syntax check passed.",
                        QueryExecutionMode.EstimatedPlan => $"Generated {result.ExecutionPlans.Count} estimated plan(s).",
                        QueryExecutionMode.ActualPlan => $"Query completed with {result.ExecutionPlans.Count} actual plan(s).",
                        _ => "Query completed successfully."
                    });
                    mainWindow?.UpdateStatusTime($"Success: {result.ExecutionTime.TotalMilliseconds:F2} ms");
                    mainWindow?.UpdateStatusRowsAndColumns(TotalResultRows, TotalResultColumns);

                    if ((mode is QueryExecutionMode.Execute or QueryExecutionMode.ActualPlan) &&
                        System.Text.RegularExpressions.Regex.IsMatch(
                        sqlQuery,
                        @"\b(CREATE|ALTER|DROP)\s+(TABLE|VIEW|PROCEDURE|PROC|FUNCTION)\b",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    {
                        InvalidateAutocompleteCache(DatabaseName);
                        await CacheAndRefreshAutocompleteAsync();
                    }
                }
                else
                {
                    TotalResultRows = 0;
                    TotalResultColumns = 0;
                    AppendLiveQueryMessage(result.Message);
                    TabResults.SelectedIndex = 1; // Select Messages Textbox Tab
                    mainWindow?.UpdateStatusTime($"Error: {result.ExecutionTime.TotalMilliseconds:F2} ms");
                    mainWindow?.UpdateStatusRowsAndColumns(0, 0);
                }
            }
            catch (Exception ex)
            {
                if (!historyRecorded)
                {
                    if (mode is QueryExecutionMode.Execute or QueryExecutionMode.ActualPlan)
                    {
                        await RecordUnexpectedQueryErrorAsync(
                            sqlQuery,
                            startedDatabaseName,
                            executionStartedAt,
                            ex);
                    }
                }
                TotalResultRows = 0;
                TotalResultColumns = 0;
                AppLogger.Error(ex, "ExecuteQuery failed");
                AppendLiveQueryMessage($"Unexpected query execution error: {ex.Message}");
                TabResults.SelectedIndex = 1;
                mainWindow?.UpdateStatusTime("Error");
                mainWindow?.UpdateStatusRowsAndColumns(0, 0);
            }
            finally
            {
                if (ReferenceEquals(_queryCancellationSource, cancellationSource))
                {
                    _queryCancellationSource = null;
                }
                cancellationSource.Dispose();
                LoadingMessageText.Text = "Executing query...";
                CancelQueryButton.Content = "Cancel query";
                CancelQueryButton.IsEnabled = true;
                LoadingOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private void AppendLiveQueryMessage(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return;
            }

            if (TxtMessages.Text.Length > 0 && !TxtMessages.Text.EndsWith(Environment.NewLine, StringComparison.Ordinal))
            {
                TxtMessages.AppendText(Environment.NewLine);
            }

            TxtMessages.AppendText(message.TrimEnd('\r', '\n'));
            TxtMessages.AppendText(Environment.NewLine);
            TxtMessages.ScrollToEnd();
        }

        private void CancelQueryButton_Click(object sender, RoutedEventArgs e)
        {
            var cancellationSource = _queryCancellationSource;
            if (cancellationSource == null || cancellationSource.IsCancellationRequested)
            {
                return;
            }

            LoadingMessageText.Text = "Cancelling query...";
            CancelQueryButton.Content = "Cancelling...";
            CancelQueryButton.IsEnabled = false;
            if (Window.GetWindow(this) is MainWindow mainWindow)
            {
                mainWindow.UpdateStatusText("Cancelling query...");
            }
            cancellationSource.Cancel();
        }
    }
}