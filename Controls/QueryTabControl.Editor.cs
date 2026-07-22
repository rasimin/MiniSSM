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

        private async Task InitializeWebViewAsync()
        {
            try
            {
                var env = await SharedEnvironment.Value;
                await SqlEditorWebView.EnsureCoreWebView2Async(env);
                SqlEditorWebView.DefaultBackgroundColor = Drawing.Color.FromArgb(30, 30, 30);

                string editorDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Editor");
                string htmlPath = Path.Combine(editorDirectory, "sql_editor.html");
                if (!File.Exists(htmlPath))
                {
                    Directory.CreateDirectory(editorDirectory);
                    File.WriteAllText(htmlPath, GetDefaultHtmlContent());
                }

                SqlEditorWebView.WebMessageReceived += SqlEditorWebView_WebMessageReceived;
                SqlEditorWebView.Source = new Uri(htmlPath);
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "Failed to initialize Monaco SQL Editor");
                TxtMessages.Text = $"Error initializing Monaco SQL Editor: {ex.Message}";
                _editorReadyCompletion.TrySetResult(false);
            }
        }

        public async void FocusEditor()
        {
            if (!IsWebViewInitialized) return;
            try
            {
                SqlEditorWebView.Focus();
                await SqlEditorWebView.ExecuteScriptAsync("focusEditor();");
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "FocusEditor failed");
            }
        }

        public async void InsertText(string text)
        {
            if (!IsWebViewInitialized) return;
            try
            {
                SqlEditorWebView.Focus();
                await SqlEditorWebView.ExecuteScriptAsync($"insertTextAtCursor({JsonSerializer.Serialize(text)});");
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "InsertText failed");
            }
        }

        private void SqlEditorWebView_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string json = e.WebMessageAsJson;
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("action", out var action))
                {
                    return;
                }

                if (action.GetString() == "execute")
                {
                    ExecuteQuery();
                }
                else if (action.GetString() == "newQuery" &&
                         Window.GetWindow(this) is MainWindow newQueryWindow)
                {
                    newQueryWindow.CreateNewQueryFromCurrentContext();
                }
                else if (action.GetString() == "editorReady")
                {
                    _ = CompleteEditorInitializationAsync();
                }
                else if (action.GetString() == "editorFocused")
                {
                    EditorActivated?.Invoke(this, EventArgs.Empty);
                }
                else if (action.GetString() == "contentChanged")
                {
                    ScheduleDirtyCheck();
                }
                else if (action.GetString() == "requestMetadata")
                {
                    RequestAutocompleteMetadata();
                }
                else if (action.GetString() == "loadDatabaseMetadata" &&
                         doc.RootElement.TryGetProperty("databaseName", out var databaseElement))
                {
                    string? databaseName = databaseElement.GetString();
                    if (!string.IsNullOrWhiteSpace(databaseName))
                    {
                        _ = LoadCrossDatabaseMetadataAsync(databaseName);
                    }
                }
                else if (action.GetString() == "viewObjectDefinition" &&
                         doc.RootElement.TryGetProperty("objectName", out var objectNameElement) &&
                         doc.RootElement.TryGetProperty("objectType", out var objectTypeElement))
                {
                    string? objectName = objectNameElement.GetString();
                    string? objectType = objectTypeElement.GetString();
                    if (!string.IsNullOrWhiteSpace(objectName) && !string.IsNullOrWhiteSpace(objectType))
                    {
                        _ = ShowObjectDefinitionTabAsync(objectName, objectType);
                    }
                }
            }
            catch { }
        }

        private async Task CompleteEditorInitializationAsync()
        {
            if (_editorReadyHandled)
            {
                return;
            }

            _editorReadyHandled = true;
            IsWebViewInitialized = true;
            EditorLoadingPanel.Visibility = Visibility.Collapsed;

            try
            {
                if (!string.IsNullOrEmpty(InitialSql))
                {
                    await SqlEditorWebView.ExecuteScriptAsync(
                        $"setQueryText({JsonSerializer.Serialize(InitialSql)});");
                }

                _savedSqlText = FilePath == null ? string.Empty : InitialSql;
                SetDirty(!string.Equals(InitialSql, _savedSqlText, StringComparison.Ordinal));

                SqlEditorWebView.Focus();
                await SqlEditorWebView.ExecuteScriptAsync("focusEditor();");

                if (AutoExecute)
                {
                    ExecuteQuery();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "Failed to complete Monaco editor initialization");
            }
            finally
            {
                _editorReadyCompletion.TrySetResult(true);
            }

        }

        public async void FormatSql()
        {
            if (!IsWebViewInitialized) return;
            try
            {
                await SqlEditorWebView.ExecuteScriptAsync("formatSql();");
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "Format SQL failed");
            }
        }

        private string GetDefaultHtmlContent()
        {
            return @"<!DOCTYPE html><html><head><style>html,body,#container{width:100%;height:100%;margin:0;padding:0;overflow:hidden;background-color:#1e1e1e;}</style></head><body><div id='container'></div><script src='https://cdnjs.cloudflare.com/ajax/libs/require.js/2.3.6/require.min.js'></script><script>require.config({paths:{vs:'https://cdnjs.cloudflare.com/ajax/libs/monaco-editor/0.39.0/min/vs'}});var editor;var tables=[];var columns=[];require(['vs/editor/editor.main'],function(){monaco.languages.registerCompletionItemProvider('sql',{provideCompletionItems:function(model,position){var word=model.getWordUntilPosition(position);var range={startLineNumber:position.lineNumber,endLineNumber:position.lineNumber,startColumn:word.startColumn,endColumn:word.endColumn};var suggestions=[];var keywords=['SELECT','FROM','WHERE','INSERT','INTO','UPDATE','SET','DELETE','CREATE','TABLE','JOIN','INNER','LEFT','ON','GROUP','BY','ORDER','AND','OR','AS'];keywords.forEach(kw=>{suggestions.push({label:kw,kind:monaco.languages.CompletionItemKind.Keyword,insertText:kw,range:range});});tables.forEach(t=>{suggestions.push({label:t,kind:monaco.languages.CompletionItemKind.Class,insertText:t,detail:'Table',range:range});});columns.forEach(c=>{suggestions.push({label:c,kind:monaco.languages.CompletionItemKind.Field,insertText:c,detail:'Column',range:range});});return{suggestions:suggestions};}});editor=monaco.editor.create(document.getElementById('container'),{value:'-- Write SQL Query\nSELECT * FROM sys.databases;',language:'sql',theme:'vs-dark',automaticLayout:true,fontSize:14,acceptSuggestionOnEnter:'off',scrollbar:{verticalScrollbarSize:5,horizontalScrollbarSize:5,useShadows:false}});editor.addCommand(monaco.KeyCode.F5,function(){window.chrome.webview.postMessage({action:'execute'});});editor.addCommand(monaco.KeyMod.CtrlCmd|monaco.KeyCode.KeyN,function(){window.chrome.webview.postMessage({action:'newQuery'});});});function getQueryText(){if(editor){var selection=editor.getSelection();var selectedText=editor.getModel().getValueInRange(selection);if(selectedText&&selectedText.trim().length>0){return selectedText;}return editor.getValue();}return'';}function setQueryText(text){if(editor)editor.setValue(text);}function updateMetadata(t,c){tables=t||[];columns=c||[];}</script></body></html>";
        }
    }
}