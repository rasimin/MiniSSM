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
                sqlQuery = await GetQueryTextAsync();
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
            ClearQueryMessages();
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

            var messageProgress = new Progress<string>(msg => AppendLiveQueryMessage(msg));

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
                    AppendLiveQueryMessage(result.Message, isError: true);
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

                    SetQueryMessages(result.Message);
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
                    SetQueryMessages(result.Message, isError: true);
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
                SetQueryMessages($"Unexpected query execution error: {ex.Message}", isError: true);
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

        private static readonly System.Windows.Media.Brush MessageRedBrush = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#F87171")!;
        private static readonly System.Windows.Media.Brush MessageGreenBrush = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#4ADE80")!;
        private static readonly System.Windows.Media.Brush MessageWhiteBrush = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#D4D4D4")!;

        private static System.Windows.Media.Brush ClassifyMessageLineColor(string line)
        {
            string trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) return MessageWhiteBrush;

            if (trimmed.StartsWith("Msg ", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Msg-", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Error", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Unexpected query execution error", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Level ", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("Incorrect syntax", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("Execution Canceled", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("Query execution cancelled", StringComparison.OrdinalIgnoreCase))
            {
                return MessageRedBrush;
            }

            if (trimmed.Contains("row affected", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("rows affected", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("completed successfully", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("Syntax check passed", StringComparison.OrdinalIgnoreCase))
            {
                return MessageGreenBrush;
            }

            return MessageWhiteBrush;
        }

        private static readonly System.Text.RegularExpressions.Regex LineNumberRegex = new(
            @"\bLine\s+(\d+)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        private void ClearQueryMessages()
        {
            TxtMessages.Document.Blocks.Clear();
        }

        private void SetQueryMessages(string message, bool isError = false)
        {
            TxtMessages.Document.Blocks.Clear();
            AppendLiveQueryMessage(message, isError);
        }

        private void AppendLiveQueryMessage(string message, bool isError = false)
        {
            if (string.IsNullOrEmpty(message))
            {
                return;
            }

            string[] lines = message.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            int lastExtractedLineNumber = -1;

            foreach (var line in lines)
            {
                var brush = isError ? MessageRedBrush : ClassifyMessageLineColor(line);
                
                int lineNum = -1;
                System.Text.RegularExpressions.Match match = LineNumberRegex.Match(line);
                if (match.Success && int.TryParse(match.Groups[1].Value, out int parsedLine))
                {
                    lineNum = parsedLine;
                    lastExtractedLineNumber = parsedLine;
                }
                else if (isError || brush == MessageRedBrush)
                {
                    lineNum = lastExtractedLineNumber;
                }

                var run = new System.Windows.Documents.Run(line);

                var paragraph = new System.Windows.Documents.Paragraph(run)
                {
                    Margin = new Thickness(0, 0, 0, 2),
                    Foreground = brush
                };

                if (lineNum > 0)
                {
                    paragraph.Tag = lineNum;
                }

                TxtMessages.Document.Blocks.Add(paragraph);
            }
            TxtMessages.ScrollToEnd();
        }

        private void TxtMessages_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ClickCount >= 2)
            {
                int targetLine = ExtractLineNumberFromClick(e.GetPosition(TxtMessages));
                if (targetLine > 0)
                {
                    GotoLine(targetLine);
                    e.Handled = true;
                }
            }
        }

        private int ExtractLineNumberFromClick(Point position)
        {
            var textPosition = TxtMessages.GetPositionFromPoint(position, true);
            if (textPosition == null) return -1;

            var paragraph = textPosition.Paragraph;
            if (paragraph != null)
            {
                if (paragraph.Tag is int tagLine && tagLine > 0)
                {
                    return tagLine;
                }

                string paraText = new System.Windows.Documents.TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text;
                System.Text.RegularExpressions.Match match = LineNumberRegex.Match(paraText);
                if (match.Success && int.TryParse(match.Groups[1].Value, out int lineNum))
                {
                    return lineNum;
                }
            }

            foreach (var block in TxtMessages.Document.Blocks)
            {
                if (block is System.Windows.Documents.Paragraph p)
                {
                    var range = new System.Windows.Documents.TextRange(p.ContentStart, p.ContentEnd);
                    if (range.Contains(textPosition))
                    {
                        if (p.Tag is int pTag && pTag > 0) return pTag;
                        string pText = range.Text;
                        System.Text.RegularExpressions.Match m = LineNumberRegex.Match(pText);
                        if (m.Success && int.TryParse(m.Groups[1].Value, out int lNum))
                        {
                            return lNum;
                        }
                    }
                }
            }

            return -1;
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