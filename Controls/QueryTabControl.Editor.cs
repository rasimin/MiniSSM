using System;
using System.Text.Json;
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
    }
}