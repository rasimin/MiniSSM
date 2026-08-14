using System;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace SSMS
{
    public partial class QueryTabControl : UserControl
    {
        public async Task<string> GetQueryTextAsync()
        {
            if (MainWindow.Instance?.SharedSqlEditorWebView is not { } webView)
            {
                return InitialSql;
            }
            try
            {
                string resultJson = await webView.ExecuteScriptAsync($"getQueryText({JsonSerializer.Serialize(TabId)});");
                return JsonSerializer.Deserialize<string>(resultJson) ?? string.Empty;
            }
            catch
            {
                return InitialSql;
            }
        }

        public async Task<string> GetAllQueryTextAsync()
        {
            if (MainWindow.Instance?.SharedSqlEditorWebView is not { } webView)
            {
                return InitialSql;
            }
            try
            {
                string resultJson = await webView.ExecuteScriptAsync($"getAllQueryText({JsonSerializer.Serialize(TabId)});");
                return JsonSerializer.Deserialize<string>(resultJson) ?? string.Empty;
            }
            catch
            {
                return InitialSql;
            }
        }

        public async void FocusEditor()
        {
            if (MainWindow.Instance?.SharedSqlEditorWebView is { } webView)
            {
                try
                {
                    webView.Focus();
                    await webView.ExecuteScriptAsync("focusEditor();");
                }
                catch { }
            }
        }

        public async void InsertText(string text)
        {
            if (MainWindow.Instance?.SharedSqlEditorWebView is { } webView)
            {
                try
                {
                    webView.Focus();
                    await webView.ExecuteScriptAsync($"insertTextAtCursor({JsonSerializer.Serialize(text)});");
                }
                catch { }
            }
        }

        public async Task AddIdentityInsertWrapperAsync()
        {
            string queryText = await GetQueryTextAsync();
            if (string.IsNullOrWhiteSpace(queryText))
            {
                MessageBox.Show(
                    "Query atau block script belum dipilih.",
                    "IDENTITY_INSERT",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            Match targetMatch = Regex.Match(
                queryText,
                @"\bINSERT\s+INTO\s+([#a-zA-Z0-9_\.\[\]]+)",
                RegexOptions.IgnoreCase);
            if (!targetMatch.Success)
            {
                MessageBox.Show(
                    "Target table tidak ditemukan. Pastikan script berisi INSERT INTO nama_tabel.",
                    "IDENTITY_INSERT",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            string targetTable = QuoteSqlObjectName(targetMatch.Groups[1].Value);
            if (MainWindow.Instance?.SharedSqlEditorWebView is not { } webView)
            {
                return;
            }

            try
            {
                await webView.ExecuteScriptAsync(
                    $"addIdentityInsertWrapper({JsonSerializer.Serialize(targetTable)});");
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "Failed to add IDENTITY_INSERT wrapper");
            }
        }

        public async void FormatSql()
        {
            if (MainWindow.Instance?.SharedSqlEditorWebView is { } webView)
            {
                try
                {
                    await webView.ExecuteScriptAsync("formatSql();");
                }
                catch { }
            }
        }

        public async void GotoLine(int lineNumber)
        {
            if (MainWindow.Instance?.SharedSqlEditorWebView is { } webView)
            {
                try
                {
                    webView.Focus();
                    await webView.ExecuteScriptAsync($"gotoLine({lineNumber});");
                }
                catch { }
            }
        }

        public void NotifyEditorFocused()
        {
            EditorActivated?.Invoke(this, EventArgs.Empty);
        }

        public async Task CompleteEditorInitializationAsync()
        {
            if (_editorReadyHandled) return;
            _editorReadyHandled = true;
            await Task.Yield();
            IsWebViewInitialized = true;
            EditorLoadingPanel.Visibility = Visibility.Collapsed;

            _savedSqlText = FilePath == null ? string.Empty : InitialSql;
            SetDirty(!string.Equals(InitialSql, _savedSqlText, StringComparison.Ordinal));

            if (AutoExecute)
            {
                ExecuteQuery();
            }

            _editorReadyCompletion.TrySetResult(true);
        }

        public async Task FetchObjectScriptAndReplaceAsync(string objectName, string statementType)
        {
            if (string.IsNullOrWhiteSpace(objectName)) return;

            string dbName = DatabaseName;
            string connStr = ConnectionString;

            try
            {
                string script = await DatabaseHelper.GetObjectDefinitionAsync(connStr, dbName, objectName);
                if (string.IsNullOrWhiteSpace(script))
                {
                    try
                    {
                        script = await DatabaseHelper.GenerateTableCreateScriptAsync(connStr, dbName, objectName);
                    }
                    catch { }
                }

                if (string.IsNullOrWhiteSpace(script)) return;

                if (statementType.Equals("ALTER", StringComparison.OrdinalIgnoreCase))
                {
                    var alterRegex = new System.Text.RegularExpressions.Regex(
                        @"\bCREATE\s+(PROCEDURE|PROC|VIEW|FUNCTION|TRIGGER)\b",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    script = alterRegex.Replace(script, "ALTER $1", 1);
                }

                if (MainWindow.Instance?.SharedSqlEditorWebView is { } webView)
                {
                    var msg = new
                    {
                        action = "replaceCurrentLineWithScript",
                        tabId = TabId,
                        script = script
                    };
                    webView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(msg));
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"Failed to fetch object script for {objectName}");
            }
        }
    }
}
