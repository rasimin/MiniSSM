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
    public sealed class SqlColumnAutocompleteInfo
    {
        public string DataType { get; init; } = string.Empty;
        public bool IsNullable { get; init; }
        public bool IsIdentity { get; init; }
        public bool IsPrimaryKey { get; init; }
    }

    public partial class QueryTabControl : UserControl
    {
        public string ConnectionString { get; set; }
        public string DatabaseName { get; set; }
        public string InitialSql { get; set; } = string.Empty;
        public string? FilePath { get; set; }
        public bool AutoExecute { get; set; } = false;
        public bool IsWebViewInitialized { get; private set; } = false;
        public bool IsDirty { get; private set; }
        public int TotalResultRows { get; private set; } = 0;
        public int TotalResultColumns { get; private set; } = 0;
        public Task EditorReady => _editorReadyCompletion.Task;
        public event EventHandler? DirtyStateChanged;
        public event EventHandler? EditorActivated;

        private readonly TaskCompletionSource<bool> _editorReadyCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string _savedSqlText = string.Empty;
        private static readonly ConcurrentDictionary<string, Lazy<Task<AutocompleteMetadata>>> SharedMetadataCache = new(StringComparer.OrdinalIgnoreCase);
        private const double EditorMinHeight = 60;
        private const double ResultsMinHeight = 90;
        private static readonly Drawing.Font ResultNullFont = new("Segoe UI", 9F, Drawing.FontStyle.Italic);
        private static readonly Lazy<IHighlightingDefinition?> SqlHighlighting = new(LoadSqlHighlighting);
        private static readonly Lazy<Task<CoreWebView2Environment>> SharedEnvironment =
            new(CreateSharedEnvironmentAsync, LazyThreadSafetyMode.ExecutionAndPublication);
        private double? _pendingEditorHeight;
        private bool _editorResizeScheduled;
        private bool _webViewInitializationStarted;
        private bool _editorReadyHandled;
        private bool _isDisposed;
        private bool _metadataRequested;
        private CancellationTokenSource? _dirtyDebounceSource;
        private CancellationTokenSource? _queryCancellationSource;
        private readonly List<WinForms.DataGridView> _resultGrids = new();
        private readonly List<DataTable> _resultTables = new();

        private sealed class AutocompleteMetadata
        {
            public Dictionary<string, List<string>> Columns { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> ObjectTypes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, Dictionary<string, SqlColumnAutocompleteInfo>> ColumnDetails { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public List<string> StoredProcedures { get; init; } = new();
            public List<string> ScalarFunctions { get; init; } = new();
            public List<string> TableFunctions { get; init; } = new();
            public Dictionary<string, List<string>> RoutineParameters { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public List<string> Databases { get; init; } = new();
        }

        private static Task<CoreWebView2Environment> CreateSharedEnvironmentAsync()
        {
            string userDataFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WebView2_UserData");
            return CoreWebView2Environment.CreateAsync(null, userDataFolder);
        }

        public QueryTabControl(string connectionString, string databaseName)
        {
            InitializeComponent();
            SqlEditorWebView.DefaultBackgroundColor = Drawing.Color.FromArgb(255, 30, 30, 30);
            ConnectionString = connectionString;
            DatabaseName = databaseName;

            Loaded += QueryTabControl_Loaded;
        }

        private void EditorResultsSplitter_DragDelta(object sender, DragDeltaEventArgs e)
        {
            double currentHeight = _pendingEditorHeight ??
                (EditorRow.ActualHeight > 0
                    ? EditorRow.ActualHeight
                    : QueryLayoutGrid.RowDefinitions[0].ActualHeight);
            _pendingEditorHeight = currentHeight + e.VerticalChange;

            if (!_editorResizeScheduled)
            {
                _editorResizeScheduled = true;
                Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Render,
                    new Action(ApplyPendingEditorResize));
            }

            e.Handled = true;
        }

        private void EditorResultsSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            ApplyPendingEditorResize();
            e.Handled = true;
        }

        private void ApplyPendingEditorResize()
        {
            _editorResizeScheduled = false;
            if (_pendingEditorHeight is not double editorHeight)
            {
                return;
            }

            _pendingEditorHeight = null;
            ResizeEditorResultsTo(editorHeight);
        }

        private void ResizeEditorResultsTo(double desiredEditorHeight)
        {
            double availableHeight = Math.Max(0, QueryLayoutGrid.ActualHeight - EditorResultsSplitter.ActualHeight);
            if (availableHeight <= EditorMinHeight + ResultsMinHeight)
            {
                return;
            }

            double editorHeight = desiredEditorHeight;
            editorHeight = Math.Max(EditorMinHeight, Math.Min(editorHeight, availableHeight - ResultsMinHeight));
            double resultsHeight = availableHeight - editorHeight;

            EditorRow.Height = new GridLength(editorHeight);
            ResultsRow.Height = new GridLength(resultsHeight);
        }

        private async void QueryTabControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (_webViewInitializationStarted) return;
            _webViewInitializationStarted = true;
            await InitializeWebViewAsync();
        }

        private async Task InitializeWebViewAsync()
        {
            try
            {
                var env = await SharedEnvironment.Value;
                await SqlEditorWebView.EnsureCoreWebView2Async(env);
                SqlEditorWebView.DefaultBackgroundColor = Drawing.Color.FromArgb(30, 30, 30);

                string htmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sql_editor.html");
                if (!File.Exists(htmlPath))
                {
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

        private void ScheduleDirtyCheck()
        {
            _dirtyDebounceSource?.Cancel();
            _dirtyDebounceSource?.Dispose();
            var source = new CancellationTokenSource();
            _dirtyDebounceSource = source;
            _ = CheckDirtyAfterDelayAsync(source);
        }

        private async Task CheckDirtyAfterDelayAsync(CancellationTokenSource source)
        {
            try
            {
                await Task.Delay(250, source.Token);
                if (_isDisposed || !IsWebViewInitialized) return;
                string resultJson = await SqlEditorWebView.ExecuteScriptAsync("getQueryText()");
                string currentText = JsonSerializer.Deserialize<string>(resultJson) ?? string.Empty;
                SetDirty(!string.Equals(currentText, _savedSqlText, StringComparison.Ordinal));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AppLogger.Error(ex, "Failed to update dirty state"); }
        }

        public void DisposeResources()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _dirtyDebounceSource?.Cancel();
            _dirtyDebounceSource?.Dispose();
            _dirtyDebounceSource = null;
            _queryCancellationSource?.Cancel();
            DisposeDisplayedResults();
            Loaded -= QueryTabControl_Loaded;
            SqlEditorWebView.WebMessageReceived -= SqlEditorWebView_WebMessageReceived;
            SqlEditorWebView.CoreWebView2?.Stop();
            SqlEditorWebView.Dispose();
            DirtyStateChanged = null;
        }

        public void MarkSaved(string sqlText)
        {
            _savedSqlText = sqlText;
            SetDirty(false);
        }

        private void SetDirty(bool value)
        {
            if (IsDirty == value)
            {
                return;
            }

            IsDirty = value;
            DirtyStateChanged?.Invoke(this, EventArgs.Empty);
        }

        private async Task ShowObjectDefinitionTabAsync(string objectName, string objectType)
        {
            var schemaTab = CreateSchemaTab(objectName, objectType);
            schemaTab.Content = new Grid
            {
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#1E1E1E")!,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"Loading definition for {objectName}...",
                        Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#A0A0A0")!,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            };

            TabResults.Items.Add(schemaTab);
            TabResults.SelectedItem = schemaTab;

            string script;
            try
            {
                script = objectType.Equals("Table", StringComparison.OrdinalIgnoreCase)
                    ? await DatabaseHelper.GenerateTableCreateScriptAsync(ConnectionString, DatabaseName, objectName)
                    : await DatabaseHelper.GetObjectDefinitionAsync(ConnectionString, DatabaseName, objectName);

                if (string.IsNullOrWhiteSpace(script))
                {
                    script = $"-- Definition for {objectName} is unavailable or encrypted.";
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"Failed to load schema definition for '{objectName}'.");
                script = $"-- Failed to load definition for {objectName}.{Environment.NewLine}-- {ex.Message}";
            }

            schemaTab.Content = CreateSchemaTabContent(schemaTab, objectName, objectType, script);
        }

        private TabItem CreateSchemaTab(string objectName, string objectType)
        {
            var tab = new TabItem
            {
                Tag = new SchemaTabInfo(objectName, objectType),
                ToolTip = $"{objectType}: {objectName}"
            };

            var header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            header.Children.Add(new TextBlock
            {
                Text = $"Schema: {objectName}",
                MaxWidth = 220,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            });

            var closeButton = new Button
            {
                Content = "x",
                ToolTip = "Close schema tab"
            };
            closeButton.SetResourceReference(StyleProperty, "SchemaTabCloseButton");
            closeButton.Click += (_, e) =>
            {
                e.Handled = true;
                CloseSchemaTab(tab);
            };
            header.Children.Add(closeButton);
            tab.Header = header;

            var contextMenu = new ContextMenu();
            var closeItem = new MenuItem { Header = "Close" };
            closeItem.Click += (_, _) => CloseSchemaTab(tab);
            contextMenu.Items.Add(closeItem);

            var closeAllItem = new MenuItem { Header = "Close All" };
            closeAllItem.Click += (_, _) => CloseAllSchemaTabs();
            contextMenu.Items.Add(closeAllItem);

            var colorMenu = new MenuItem { Header = "Set Color" };
            foreach (var (name, color) in new[]
            {
                ("Red", "#6A1B1B"),
                ("Green", "#1B5E20"),
                ("Blue", "#0D47A1"),
                ("Yellow", "#8D6E63"),
                ("Orange", "#D84315"),
                ("Default (Dark)", "#252526")
            })
            {
                var colorItem = new MenuItem { Header = name };
                colorItem.Click += (_, _) => tab.Background =
                    (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
                colorMenu.Items.Add(colorItem);
            }
            contextMenu.Items.Add(colorMenu);
            tab.ContextMenu = contextMenu;
            return tab;
        }

        private FrameworkElement CreateSchemaTabContent(
            TabItem schemaTab,
            string objectName,
            string objectType,
            string script)
        {
            var layout = new Grid
            {
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#1E1E1E")!
            };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var toolbar = new Grid
            {
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#252526")!,
                Height = 38
            };
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            toolbar.Children.Add(new TextBlock
            {
                Text = $"{objectType}  {objectName}",
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#C8C8C8")!,
                Margin = new Thickness(12, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(actions, 1);

            var openButton = CreateSchemaActionButton("Open in New Query");
            openButton.Click += (_, _) =>
            {
                if (Window.GetWindow(this) is MainWindow mainWindow)
                {
                    mainWindow.CreateNewQueryTab(
                        ConnectionString,
                        DatabaseName,
                        script,
                        $"{objectName}_CREATE.sql");
                }
            };

            var copyButton = CreateSchemaActionButton("Copy");
            copyButton.Click += (_, _) => CopySchemaToClipboard(script, objectName);

            var closeAllButton = CreateSchemaActionButton("Close All Schemas");
            closeAllButton.Click += (_, _) => CloseAllSchemaTabs();

            actions.Children.Add(openButton);
            actions.Children.Add(copyButton);
            actions.Children.Add(closeAllButton);
            toolbar.Children.Add(actions);
            layout.Children.Add(toolbar);

            var viewer = new ICSharpCode.AvalonEdit.TextEditor
            {
                Text = script,
                IsReadOnly = true,
                ShowLineNumbers = true,
                SyntaxHighlighting = SqlHighlighting.Value,
                WordWrap = false,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#1E1E1E")!,
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#D4D4D4")!,
                LineNumbersForeground = (SolidColorBrush)new BrushConverter().ConvertFromString("#858585")!,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(12, 10, 12, 10),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13
            };
            viewer.Options.EnableHyperlinks = false;
            viewer.Options.EnableEmailHyperlinks = false;
            viewer.Options.HighlightCurrentLine = true;
            viewer.Options.IndentationSize = 4;
            viewer.TextArea.SelectionBrush =
                (SolidColorBrush)new BrushConverter().ConvertFromString("#264F78")!;
            viewer.TextArea.TextView.CurrentLineBackground =
                (SolidColorBrush)new BrushConverter().ConvertFromString("#242424")!;
            Grid.SetRow(viewer, 1);
            layout.Children.Add(viewer);
            return layout;
        }

        private static IHighlightingDefinition? LoadSqlHighlighting()
        {
            try
            {
                var resource = Application.GetResourceStream(new Uri("SqlDark.xshd", UriKind.Relative));
                if (resource == null)
                {
                    return HighlightingManager.Instance.GetDefinition("SQL");
                }

                using var reader = XmlReader.Create(resource.Stream);
                return HighlightingLoader.Load(reader, HighlightingManager.Instance);
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "Failed to load SQL syntax highlighting.");
                return HighlightingManager.Instance.GetDefinition("SQL");
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

        private Button CreateSchemaActionButton(string text)
        {
            var button = new Button { Content = text };
            button.SetResourceReference(StyleProperty, "SchemaActionButton");
            return button;
        }

        private void CopySchemaToClipboard(string script, string objectName)
        {
            try
            {
                Clipboard.SetText(script);
                if (Window.GetWindow(this) is MainWindow mainWindow)
                {
                    mainWindow.UpdateStatusText($"Definition copied: {objectName}");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"Failed to copy schema definition for '{objectName}'.");
                MessageBox.Show(
                    $"Failed to copy definition: {ex.Message}",
                    "Copy Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private void CloseSchemaTab(TabItem tab)
        {
            if (tab.Tag is not SchemaTabInfo)
            {
                return;
            }

            TabResults.Items.Remove(tab);
            if (TabResults.SelectedItem == null)
            {
                TabResults.SelectedIndex = 0;
            }
        }

        private void CloseAllSchemaTabs()
        {
            var schemaTabs = TabResults.Items
                .OfType<TabItem>()
                .Where(tab => tab.Tag is SchemaTabInfo)
                .ToList();

            foreach (TabItem tab in schemaTabs)
            {
                TabResults.Items.Remove(tab);
            }
            TabResults.SelectedIndex = 0;
        }

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

        private void DisplayExecutionPlans(IReadOnlyList<string> plans, QueryExecutionMode mode)
        {
            for (int i = 0; i < plans.Count; i++)
            {
                string planXml = plans[i];
                var planTab = new TabItem
                {
                    Header = $"{(mode == QueryExecutionMode.EstimatedPlan ? "Estimated" : "Actual")} Plan {i + 1}",
                    Tag = new ExecutionPlanTabInfo(planXml),
                    ToolTip = "Execution plan XML. Save as .sqlplan to open graphically in SSMS."
                };

                var viewer = new ICSharpCode.AvalonEdit.TextEditor
                {
                    Text = planXml,
                    IsReadOnly = true,
                    ShowLineNumbers = true,
                    SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("XML"),
                    WordWrap = false,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#1E1E1E")!,
                    Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#D4D4D4")!,
                    LineNumbersForeground = (SolidColorBrush)new BrushConverter().ConvertFromString("#858585")!,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12,
                    Padding = new Thickness(10),
                    BorderThickness = new Thickness(0)
                };
                planTab.Content = CreateExecutionPlanContent(planXml, viewer);

                var contextMenu = new ContextMenu();
                var saveItem = new MenuItem { Header = "Save as .sqlplan..." };
                saveItem.Click += (_, _) => SaveExecutionPlan(planXml, i + 1);
                contextMenu.Items.Add(saveItem);
                var copyItem = new MenuItem { Header = "Copy XML" };
                copyItem.Click += (_, _) => Clipboard.SetText(planXml);
                contextMenu.Items.Add(copyItem);
                contextMenu.Items.Add(new Separator());
                var closeItem = new MenuItem { Header = "Close" };
                closeItem.Click += (_, _) => TabResults.Items.Remove(planTab);
                contextMenu.Items.Add(closeItem);
                planTab.ContextMenu = contextMenu;

                TabResults.Items.Add(planTab);
                if (i == 0)
                {
                    TabResults.SelectedItem = planTab;
                }
            }
        }

        private FrameworkElement CreateExecutionPlanContent(
            string planXml,
            ICSharpCode.AvalonEdit.TextEditor xmlViewer)
        {
            var tabs = new TabControl
            {
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#1E1E1E")!,
                BorderThickness = new Thickness(0)
            };
            var operatorTree = new TreeView
            {
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#1E1E1E")!,
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#D4D4D4")!,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10)
            };

            try
            {
                XDocument document = XDocument.Parse(planXml);
                XNamespace ns = document.Root?.Name.Namespace ?? XNamespace.None;
                int statementNumber = 0;
                foreach (XElement statement in document.Descendants(ns + "StmtSimple"))
                {
                    statementNumber++;
                    string statementText = (string?)statement.Attribute("StatementText") ?? $"Statement {statementNumber}";
                    statementText = System.Text.RegularExpressions.Regex.Replace(statementText, @"\s+", " ").Trim();
                    if (statementText.Length > 180) statementText = statementText[..177] + "...";

                    var statementItem = new TreeViewItem
                    {
                        Header = $"Statement {statementNumber}: {statementText}",
                        IsExpanded = true,
                        Foreground = Brushes.White
                    };
                    XElement? queryPlan = statement.Descendants(ns + "QueryPlan").FirstOrDefault();
                    foreach (XElement rootOperator in queryPlan?.Elements(ns + "RelOp") ?? Enumerable.Empty<XElement>())
                    {
                        statementItem.Items.Add(CreateExecutionPlanOperatorItem(rootOperator, ns));
                    }
                    operatorTree.Items.Add(statementItem);
                }

                if (operatorTree.Items.Count == 0)
                {
                    operatorTree.Items.Add(new TreeViewItem { Header = "No relational operators were found in this plan." });
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "Failed to parse execution plan XML");
                operatorTree.Items.Add(new TreeViewItem { Header = $"Unable to parse plan summary: {ex.Message}" });
            }

            tabs.Items.Add(new TabItem { Header = "Plan Operators", Content = operatorTree });
            tabs.Items.Add(new TabItem { Header = "Plan XML", Content = xmlViewer });
            tabs.SelectedIndex = 0;
            return tabs;
        }

        private static TreeViewItem CreateExecutionPlanOperatorItem(XElement relOp, XNamespace ns)
        {
            string nodeId = (string?)relOp.Attribute("NodeId") ?? "?";
            string physical = (string?)relOp.Attribute("PhysicalOp") ?? "Unknown";
            string logical = (string?)relOp.Attribute("LogicalOp") ?? physical;
            string estimatedRows = (string?)relOp.Attribute("EstimateRows") ?? "?";
            string estimatedCost = (string?)relOp.Attribute("EstimatedTotalSubtreeCost") ?? "?";
            long actualRows = relOp.Descendants(ns + "RunTimeCountersPerThread")
                .Select(counter => long.TryParse((string?)counter.Attribute("ActualRows"), out long rows) ? rows : 0)
                .Sum();
            string actualText = actualRows > 0 ? $" | Actual rows: {actualRows:N0}" : string.Empty;

            var item = new TreeViewItem
            {
                Header = $"[{nodeId}] {physical} ({logical}) | Est. rows: {estimatedRows} | Cost: {estimatedCost}{actualText}",
                IsExpanded = true,
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#D4D4D4")!
            };

            IEnumerable<XElement> childOperators = relOp.Descendants(ns + "RelOp")
                .Where(child => child.Ancestors(ns + "RelOp").FirstOrDefault() == relOp);
            foreach (XElement child in childOperators)
            {
                item.Items.Add(CreateExecutionPlanOperatorItem(child, ns));
            }
            return item;
        }

        private void SaveExecutionPlan(string planXml, int planNumber)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save Execution Plan",
                Filter = "SQL Server Execution Plan (*.sqlplan)|*.sqlplan|XML File (*.xml)|*.xml",
                FileName = $"ExecutionPlan_{planNumber}.sqlplan",
                AddExtension = true
            };
            if (dialog.ShowDialog() == true)
            {
                File.WriteAllText(dialog.FileName, planXml, System.Text.Encoding.UTF8);
            }
        }

        private void ClearExecutionPlanTabs()
        {
            foreach (TabItem tab in TabResults.Items.OfType<TabItem>()
                         .Where(item => item.Tag is ExecutionPlanTabInfo).ToList())
            {
                TabResults.Items.Remove(tab);
            }
        }

        private async Task RecordQueryHistoryAsync(
            string sqlQuery,
            string startedDatabaseName,
            DateTimeOffset executionStartedAt,
            QueryResult result)
        {
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(ConnectionString);
            int resultRowCount = result.DataTables?.Sum(table => table.Rows.Count) ?? 0;
            string status = result.IsCancelled ? "Cancelled" : result.IsSuccess ? "Success" : "Error";

            await QueryHistoryService.TryAddAsync(new QueryHistoryEntry
            {
                ExecutedAtUtc = executionStartedAt,
                ServerName = builder.DataSource,
                StartedDatabaseName = startedDatabaseName,
                EffectiveDatabaseName = string.IsNullOrWhiteSpace(result.EffectiveDatabaseName)
                    ? startedDatabaseName
                    : result.EffectiveDatabaseName,
                QueryText = sqlQuery,
                DurationMilliseconds = Math.Max(0, (long)Math.Round(result.ExecutionTime.TotalMilliseconds)),
                ExecutionStatus = status,
                ResultMessage = result.Message,
                RowsAffected = result.RowsAffected,
                ResultRowCount = resultRowCount,
                ErrorMessage = status == "Error" ? result.Message : string.Empty
            });
        }

        private async Task RecordUnexpectedQueryErrorAsync(
            string sqlQuery,
            string startedDatabaseName,
            DateTimeOffset executionStartedAt,
            Exception exception)
        {
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(ConnectionString);
            await QueryHistoryService.TryAddAsync(new QueryHistoryEntry
            {
                ExecutedAtUtc = executionStartedAt,
                ServerName = builder.DataSource,
                StartedDatabaseName = startedDatabaseName,
                EffectiveDatabaseName = startedDatabaseName,
                QueryText = sqlQuery,
                DurationMilliseconds = Math.Max(
                    0,
                    (long)Math.Round((DateTimeOffset.UtcNow - executionStartedAt).TotalMilliseconds)),
                ExecutionStatus = "Error",
                ResultMessage = $"Unexpected query execution error: {exception.Message}",
                ResultRowCount = 0,
                ErrorMessage = exception.Message
            });
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

        public async Task CacheAndRefreshAutocompleteAsync()
        {
            if (!IsWebViewInitialized || _isDisposed || !_metadataRequested) return;

            try
            {
                string cacheKey = $"{ConnectionString}\u001f{DatabaseName}";
                var lazyMetadata = SharedMetadataCache.GetOrAdd(
                    cacheKey,
                    _ => new Lazy<Task<AutocompleteMetadata>>(
                        () => LoadAutocompleteMetadataAsync(ConnectionString, DatabaseName),
                        LazyThreadSafetyMode.ExecutionAndPublication));
                var metadata = await lazyMetadata.Value;
                if (_isDisposed) return;

                var payload = new
                {
                    columns = metadata.Columns,
                    objectTypes = metadata.ObjectTypes,
                    columnDetails = metadata.ColumnDetails,
                    storedProcedures = metadata.StoredProcedures,
                    scalarFunctions = metadata.ScalarFunctions,
                    tableFunctions = metadata.TableFunctions,
                    routineParameters = metadata.RoutineParameters,
                    databases = metadata.Databases,
                    activeDatabase = DatabaseName
                };
                string jsonPayload = JsonSerializer.Serialize(payload);
                await SqlEditorWebView.ExecuteScriptAsync($"updateMetadata({jsonPayload});");
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"Failed to load autocomplete metadata for database '{DatabaseName}'");
            }
        }

        private static async Task<AutocompleteMetadata> LoadAutocompleteMetadataAsync(string connectionString, string databaseName)
        {
            var tableColumns = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var objectTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var columnDetails = new Dictionary<string, Dictionary<string, SqlColumnAutocompleteInfo>>(StringComparer.OrdinalIgnoreCase);
            var dbConnString = DatabaseHelper.BuildConnectionString(connectionString, databaseName);
            using (var connection = new Microsoft.Data.SqlClient.SqlConnection(dbConnString))
            {
                await connection.OpenAsync();
                const string query = @"
                        SELECT s.name AS SchemaName,
                               o.name AS ObjectName,
                               c.name AS ColumnName,
                               CASE WHEN o.type = 'V' THEN 'View' ELSE 'Table' END AS ObjectType,
                               ty.name +
                                   CASE
                                       WHEN ty.name IN ('varchar','char','varbinary','binary') THEN
                                           '(' + CASE WHEN c.max_length = -1 THEN 'MAX' ELSE CAST(c.max_length AS varchar(10)) END + ')'
                                       WHEN ty.name IN ('nvarchar','nchar') THEN
                                           '(' + CASE WHEN c.max_length = -1 THEN 'MAX' ELSE CAST(c.max_length / 2 AS varchar(10)) END + ')'
                                       WHEN ty.name IN ('decimal','numeric') THEN
                                           '(' + CAST(c.precision AS varchar(10)) + ',' + CAST(c.scale AS varchar(10)) + ')'
                                       ELSE ''
                                   END AS DataType,
                               c.is_nullable,
                               c.is_identity,
                               CAST(CASE WHEN EXISTS (
                                   SELECT 1
                                   FROM sys.index_columns ic
                                   JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                                   WHERE ic.object_id = c.object_id
                                     AND ic.column_id = c.column_id
                                     AND i.is_primary_key = 1
                               ) THEN 1 ELSE 0 END AS bit) AS IsPrimaryKey
                        FROM sys.objects o
                        JOIN sys.schemas s ON o.schema_id = s.schema_id
                        JOIN sys.columns c ON o.object_id = c.object_id
                        JOIN sys.types ty ON c.user_type_id = ty.user_type_id
                        WHERE o.type IN ('U', 'V')
                        ORDER BY s.name, o.name, c.column_id;";

                    using var command = new Microsoft.Data.SqlClient.SqlCommand(query, connection);
                    using var reader = await command.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        string schema = reader.GetString(0);
                        string table = reader.GetString(1);
                        string column = reader.GetString(2);
                        string objectType = reader.GetString(3);
                        var columnInfo = new SqlColumnAutocompleteInfo
                        {
                            DataType = reader.GetString(4),
                            IsNullable = reader.GetBoolean(5),
                            IsIdentity = reader.GetBoolean(6),
                            IsPrimaryKey = reader.GetBoolean(7)
                        };
                        
                        string fullKey = $"{schema}.{table}";
                        string shortKey = table;
                        objectTypes[fullKey] = objectType;
                        objectTypes.TryAdd(shortKey, objectType);
                        if (!columnDetails.TryGetValue(fullKey, out var fullDetails))
                        {
                            fullDetails = new Dictionary<string, SqlColumnAutocompleteInfo>(StringComparer.OrdinalIgnoreCase);
                            columnDetails[fullKey] = fullDetails;
                        }
                        fullDetails[column] = columnInfo;

                        if (!columnDetails.TryGetValue(shortKey, out var shortDetails))
                        {
                            shortDetails = new Dictionary<string, SqlColumnAutocompleteInfo>(StringComparer.OrdinalIgnoreCase);
                            columnDetails[shortKey] = shortDetails;
                        }
                        shortDetails[column] = columnInfo;

                        if (!tableColumns.TryGetValue(fullKey, out var cols))
                        {
                            cols = new List<string>();
                            tableColumns[fullKey] = cols;
                        }
                        cols.Add(column);

                        if (!tableColumns.TryGetValue(shortKey, out var shortCols))
                        {
                            shortCols = new List<string>();
                            tableColumns[shortKey] = shortCols;
                        }
                        if (!shortCols.Contains(column))
                        {
                            shortCols.Add(column);
                        }
                    }
                }

            var storedProcedures = await DatabaseHelper.GetStoredProceduresAsync(connectionString, databaseName);
            var scalarFunctions = await DatabaseHelper.GetFunctionsAsync(connectionString, databaseName, tableValued: false);
            var tableFunctions = await DatabaseHelper.GetFunctionsAsync(connectionString, databaseName, tableValued: true);
            var parameters = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            using (var parameterConnection = new Microsoft.Data.SqlClient.SqlConnection(dbConnString))
            {
                await parameterConnection.OpenAsync();
                    const string parameterQuery = @"
                        SELECT s.name + '.' + o.name AS RoutineName, p.name AS ParameterName
                        FROM sys.objects o
                        JOIN sys.schemas s ON o.schema_id = s.schema_id
                        JOIN sys.parameters p ON o.object_id = p.object_id
                        WHERE o.type IN ('P', 'PC', 'FN', 'IF', 'TF', 'FS', 'FT')
                          AND p.parameter_id > 0
                        ORDER BY s.name, o.name, p.parameter_id;";
                    using var parameterCommand = new Microsoft.Data.SqlClient.SqlCommand(parameterQuery, parameterConnection);
                    using var parameterReader = await parameterCommand.ExecuteReaderAsync();
                    while (await parameterReader.ReadAsync())
                    {
                        string routineName = parameterReader.GetString(0);
                        string parameterName = parameterReader.GetString(1);
                        if (!parameters.TryGetValue(routineName, out var routineParameters))
                        {
                            routineParameters = new List<string>();
                            parameters[routineName] = routineParameters;
                        }
                        routineParameters.Add(parameterName);
                    }
            }

            return new AutocompleteMetadata
            {
                Columns = tableColumns,
                ObjectTypes = objectTypes,
                ColumnDetails = columnDetails,
                StoredProcedures = storedProcedures,
                ScalarFunctions = scalarFunctions,
                TableFunctions = tableFunctions,
                RoutineParameters = parameters,
                Databases = await DatabaseHelper.GetDatabasesAsync(connectionString)
            };
        }

        private void InvalidateAutocompleteCache(string databaseName)
        {
            string cacheKey = $"{ConnectionString}\u001f{databaseName}";
            SharedMetadataCache.TryRemove(cacheKey, out _);
        }

        public async Task RefreshAutocompleteAsync()
        {
            InvalidateAutocompleteCache(DatabaseName);
            await CacheAndRefreshAutocompleteAsync();
        }

        private void RequestAutocompleteMetadata()
        {
            if (_metadataRequested || _isDisposed) return;
            _metadataRequested = true;
            _ = CacheAndRefreshAutocompleteAsync();
        }

        private async Task LoadCrossDatabaseMetadataAsync(string databaseName)
        {
            try
            {
                var columns = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                var objectTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string dbConnectionString = DatabaseHelper.BuildConnectionString(ConnectionString, databaseName);
                using var connection = new Microsoft.Data.SqlClient.SqlConnection(dbConnectionString);
                await connection.OpenAsync();
                const string query = @"
                    SELECT s.name, o.name, c.name,
                           CASE WHEN o.type = 'V' THEN 'View' ELSE 'Table' END
                    FROM sys.objects o
                    JOIN sys.schemas s ON o.schema_id = s.schema_id
                    JOIN sys.columns c ON o.object_id = c.object_id
                    WHERE o.type IN ('U', 'V')
                    ORDER BY s.name, o.name, c.column_id;";
                using var command = new Microsoft.Data.SqlClient.SqlCommand(query, connection);
                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    string key = $"{reader.GetString(0)}.{reader.GetString(1)}";
                    if (!columns.TryGetValue(key, out var objectColumns))
                    {
                        objectColumns = new List<string>();
                        columns[key] = objectColumns;
                    }
                    objectColumns.Add(reader.GetString(2));
                    objectTypes[key] = reader.GetString(3);
                }

                var scalarFunctions =
                    await DatabaseHelper.GetFunctionsAsync(ConnectionString, databaseName, tableValued: false);
                var tableFunctions =
                    await DatabaseHelper.GetFunctionsAsync(ConnectionString, databaseName, tableValued: true);
                var payload = new { columns, objectTypes, scalarFunctions, tableFunctions };
                string jsonDatabase = JsonSerializer.Serialize(databaseName);
                string jsonPayload = JsonSerializer.Serialize(payload);
                await SqlEditorWebView.ExecuteScriptAsync(
                    $"updateDatabaseMetadata({jsonDatabase}, {jsonPayload});");
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"Failed to load cross-database autocomplete metadata for '{databaseName}'");
            }
        }

        private void DisplayQueryResults(List<DataTable> dataTables)
        {
            DisposeDisplayedResults();

            if (dataTables == null || dataTables.Count == 0)
            {
                return;
            }

            for (int i = 0; i < dataTables.Count; i++)
            {
                _resultTables.Add(dataTables[i]);
                ResultsGridContainer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

                var grid = CreateResultDataGrid(dataTables[i], i);
                Grid.SetRow(grid, ResultsGridContainer.RowDefinitions.Count - 1);
                ResultsGridContainer.Children.Add(grid);

                if (i < dataTables.Count - 1)
                {
                    ResultsGridContainer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    
                    var splitter = new GridSplitter
                    {
                        Height = 4,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        VerticalAlignment = VerticalAlignment.Center,
                        Background = (System.Windows.Media.SolidColorBrush)new System.Windows.Media.BrushConverter().ConvertFromString("#2D2D30")!
                    };
                    
                    Grid.SetRow(splitter, ResultsGridContainer.RowDefinitions.Count - 1);
                    ResultsGridContainer.Children.Add(splitter);
                }
            }
        }

        private FrameworkElement CreateResultDataGrid(DataTable dataTable, int resultIndex)
        {
            var dataGrid = new BufferedDataGridView
            {
                AutoGenerateColumns = false,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToOrderColumns = false,
                AllowUserToResizeRows = false,
                AutoSizeColumnsMode = WinForms.DataGridViewAutoSizeColumnsMode.None,
                AutoSizeRowsMode = WinForms.DataGridViewAutoSizeRowsMode.None,
                BackgroundColor = Drawing.Color.FromArgb(30, 30, 30),
                BorderStyle = WinForms.BorderStyle.None,
                CellBorderStyle = WinForms.DataGridViewCellBorderStyle.Single,
                ColumnHeadersBorderStyle = WinForms.DataGridViewHeaderBorderStyle.Single,
                ColumnHeadersHeight = 28,
                ColumnHeadersHeightSizeMode = WinForms.DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                Dock = WinForms.DockStyle.Fill,
                EnableHeadersVisualStyles = false,
                GridColor = Drawing.Color.FromArgb(72, 72, 76),
                MultiSelect = true,
                RowHeadersBorderStyle = WinForms.DataGridViewHeaderBorderStyle.Single,
                RowHeadersWidth = 46,
                RowHeadersWidthSizeMode = WinForms.DataGridViewRowHeadersWidthSizeMode.DisableResizing,
                ScrollBars = WinForms.ScrollBars.None,
                SelectionMode = WinForms.DataGridViewSelectionMode.CellSelect,
                ShowCellErrors = false,
                ShowEditingIcon = false,
                ShowRowErrors = false
            };

            dataGrid.Font = new Drawing.Font("Segoe UI", 9F, Drawing.FontStyle.Regular, Drawing.GraphicsUnit.Point);
            dataGrid.DefaultCellStyle = new WinForms.DataGridViewCellStyle
            {
                BackColor = Drawing.Color.FromArgb(30, 30, 30),
                ForeColor = Drawing.Color.FromArgb(204, 204, 204),
                SelectionBackColor = Drawing.Color.FromArgb(30, 58, 95),
                SelectionForeColor = Drawing.Color.White,
                Padding = new WinForms.Padding(5, 0, 5, 0),
                NullValue = "NULL"
            };
            dataGrid.AlternatingRowsDefaultCellStyle = new WinForms.DataGridViewCellStyle
            {
                BackColor = Drawing.Color.FromArgb(37, 37, 38),
                ForeColor = Drawing.Color.FromArgb(204, 204, 204),
                SelectionBackColor = Drawing.Color.FromArgb(30, 58, 95),
                SelectionForeColor = Drawing.Color.White
            };
            dataGrid.ColumnHeadersDefaultCellStyle = new WinForms.DataGridViewCellStyle
            {
                BackColor = Drawing.Color.FromArgb(37, 37, 38),
                ForeColor = Drawing.Color.FromArgb(241, 241, 241),
                SelectionBackColor = Drawing.Color.FromArgb(37, 37, 38),
                SelectionForeColor = Drawing.Color.White,
                Font = new Drawing.Font("Segoe UI", 9F, Drawing.FontStyle.Bold),
                Padding = new WinForms.Padding(5, 0, 5, 0)
            };
            dataGrid.RowHeadersDefaultCellStyle = new WinForms.DataGridViewCellStyle
            {
                BackColor = Drawing.Color.FromArgb(37, 37, 38),
                ForeColor = Drawing.Color.FromArgb(136, 136, 136),
                SelectionBackColor = Drawing.Color.FromArgb(45, 45, 48),
                SelectionForeColor = Drawing.Color.White,
                Alignment = WinForms.DataGridViewContentAlignment.MiddleRight
            };
            dataGrid.RowTemplate.Height = 24;

            foreach (DataColumn column in dataTable.Columns)
            {
                dataGrid.Columns.Add(new WinForms.DataGridViewTextBoxColumn
                {
                    DataPropertyName = column.ColumnName,
                    HeaderText = column.ColumnName,
                    Name = column.ColumnName,
                    SortMode = WinForms.DataGridViewColumnSortMode.NotSortable,
                    ValueType = column.DataType,
                    Width = 120
                });
            }

            dataGrid.CellFormatting += (_, e) => FormatResultCell(e);
            dataGrid.CellPainting += (_, e) => PaintRowNumber(dataGrid, e);
            dataGrid.ColumnDividerDoubleClick += (_, e) =>
            {
                if (e.ColumnIndex < 0 || e.ColumnIndex >= dataGrid.Columns.Count)
                {
                    return;
                }

                e.Handled = true;
                dataGrid.AutoResizeColumn(
                    e.ColumnIndex,
                    WinForms.DataGridViewAutoSizeColumnMode.DisplayedCells);
            };
            int rowSelectionAnchor = -1;
            bool isDraggingRowSelection = false;

            void SelectRowRange(int targetRow)
            {
                if (rowSelectionAnchor < 0 || targetRow < 0)
                {
                    return;
                }

                dataGrid.ClearSelection();
                int firstRow = Math.Min(rowSelectionAnchor, targetRow);
                int lastRow = Math.Max(rowSelectionAnchor, targetRow);
                if (dataGrid.Rows[targetRow].Cells.Count > 0)
                {
                    dataGrid.CurrentCell = dataGrid.Rows[targetRow].Cells[0];
                }

                for (int rowIndex = firstRow; rowIndex <= lastRow; rowIndex++)
                {
                    foreach (WinForms.DataGridViewCell cell in dataGrid.Rows[rowIndex].Cells)
                    {
                        cell.Selected = true;
                    }
                }
            }

            dataGrid.CellMouseDown += (_, e) =>
            {
                if (e.Button == WinForms.MouseButtons.Right && e.RowIndex >= 0 && e.ColumnIndex >= 0)
                {
                    dataGrid.CurrentCell = dataGrid.Rows[e.RowIndex].Cells[e.ColumnIndex];
                    return;
                }

                if (e.RowIndex < 0 || e.ColumnIndex != -1 || e.Button != WinForms.MouseButtons.Left)
                {
                    return;
                }

                if ((WinForms.Control.ModifierKeys & WinForms.Keys.Shift) == 0 || rowSelectionAnchor < 0)
                {
                    rowSelectionAnchor = e.RowIndex;
                }
                isDraggingRowSelection = true;
                dataGrid.Capture = true;
                SelectRowRange(e.RowIndex);
            };
            dataGrid.MouseMove += (_, e) =>
            {
                if (!isDraggingRowSelection || (e.Button & WinForms.MouseButtons.Left) == 0)
                {
                    return;
                }

                var hit = dataGrid.HitTest(e.X, e.Y);
                if (hit.RowIndex >= 0)
                {
                    SelectRowRange(hit.RowIndex);
                }
                else if (dataGrid.RowCount > 0 && e.Y < dataGrid.ColumnHeadersHeight && dataGrid.FirstDisplayedScrollingRowIndex > 0)
                {
                    int targetRow = dataGrid.FirstDisplayedScrollingRowIndex - 1;
                    dataGrid.FirstDisplayedScrollingRowIndex = targetRow;
                    SelectRowRange(targetRow);
                }
                else if (dataGrid.RowCount > 0 && e.Y > dataGrid.ClientSize.Height)
                {
                    int lastDisplayedRow = Math.Min(
                        dataGrid.RowCount - 1,
                        dataGrid.FirstDisplayedScrollingRowIndex + dataGrid.DisplayedRowCount(false) - 1);
                    if (lastDisplayedRow < dataGrid.RowCount - 1)
                    {
                        int targetRow = lastDisplayedRow + 1;
                        dataGrid.FirstDisplayedScrollingRowIndex = Math.Min(
                            targetRow,
                            Math.Max(0, dataGrid.RowCount - dataGrid.DisplayedRowCount(false)));
                        SelectRowRange(targetRow);
                    }
                }
            };
            dataGrid.MouseUp += (_, e) =>
            {
                if (e.Button == WinForms.MouseButtons.Left)
                {
                    isDraggingRowSelection = false;
                    dataGrid.Capture = false;
                }
            };

            var contextMenu = new WinForms.ContextMenuStrip
            {
                BackColor = Drawing.Color.FromArgb(30, 30, 30),
                ForeColor = Drawing.Color.FromArgb(204, 204, 204),
                ShowImageMargin = false
            };
            contextMenu.Items.Add("Copy", null, (_, _) => CopyGridToClipboard(dataGrid, false));
            contextMenu.Items.Add("Copy with Headers", null, (_, _) => CopyGridToClipboard(dataGrid, true));
            var exportMenu = new WinForms.ToolStripMenuItem("Export Results");
            exportMenu.DropDownItems.Add("CSV...", null, (_, _) => ExportResultTable(dataTable, resultIndex, ResultExportFormat.Csv));
            exportMenu.DropDownItems.Add("Tab-delimited Text...", null, (_, _) => ExportResultTable(dataTable, resultIndex, ResultExportFormat.Tsv));
            exportMenu.DropDownItems.Add("JSON...", null, (_, _) => ExportResultTable(dataTable, resultIndex, ResultExportFormat.Json));
            exportMenu.DropDownItems.Add("XML...", null, (_, _) => ExportResultTable(dataTable, resultIndex, ResultExportFormat.Xml));
            contextMenu.Items.Add(exportMenu);
            contextMenu.Items.Add(new WinForms.ToolStripSeparator());
            contextMenu.Items.Add("Auto Fit Column", null, (_, _) =>
            {
                int columnIndex = dataGrid.CurrentCell?.ColumnIndex ?? -1;
                if (columnIndex >= 0)
                {
                    dataGrid.AutoResizeColumn(
                        columnIndex,
                        WinForms.DataGridViewAutoSizeColumnMode.DisplayedCells);
                }
            });
            contextMenu.Items.Add("Widen Column (+200 px)", null, (_, _) =>
            {
                int columnIndex = dataGrid.CurrentCell?.ColumnIndex ?? -1;
                if (columnIndex >= 0)
                {
                    var column = dataGrid.Columns[columnIndex];
                    column.Width = Math.Min(10000, column.Width + 200);
                }
            });
            dataGrid.ContextMenuStrip = contextMenu;
            dataGrid.DataSource = dataTable;
            _resultGrids.Add(dataGrid);

            var host = new WindowsFormsHost
            {
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#1E1E1E")!,
                Child = dataGrid
            };

            var verticalScrollBar = new System.Windows.Controls.Primitives.ScrollBar
            {
                Orientation = System.Windows.Controls.Orientation.Vertical,
                Minimum = 0,
                SmallChange = 1,
                Width = 8
            };
            var horizontalScrollBar = new System.Windows.Controls.Primitives.ScrollBar
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                Minimum = 0,
                SmallChange = 24,
                Height = 8
            };

            var container = new Grid
            {
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#1E1E1E")!
            };
            container.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            container.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            container.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            container.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });

            Grid.SetColumn(host, 0);
            Grid.SetRow(host, 0);
            Grid.SetColumn(verticalScrollBar, 1);
            Grid.SetRow(verticalScrollBar, 0);
            Grid.SetColumn(horizontalScrollBar, 0);
            Grid.SetRow(horizontalScrollBar, 1);

            container.Children.Add(host);
            container.Children.Add(verticalScrollBar);
            container.Children.Add(horizontalScrollBar);
            var scrollCorner = new Border
            {
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#1E1E1E")!
            };
            Grid.SetColumn(scrollCorner, 1);
            Grid.SetRow(scrollCorner, 1);
            container.Children.Add(scrollCorner);

            bool synchronizingScrollBars = false;
            bool scrollBarUpdateScheduled = false;

            void UpdateScrollBars()
            {
                if (synchronizingScrollBars || !dataGrid.IsHandleCreated || dataGrid.RowCount == 0)
                {
                    return;
                }

                synchronizingScrollBars = true;
                try
                {
                    int displayedRows = Math.Max(1, dataGrid.DisplayedRowCount(false));
                    int maximumFirstRow = Math.Max(0, dataGrid.RowCount - displayedRows);
                    int firstRow = Math.Max(0, dataGrid.FirstDisplayedScrollingRowIndex);
                    verticalScrollBar.Maximum = maximumFirstRow;
                    verticalScrollBar.ViewportSize = displayedRows;
                    verticalScrollBar.LargeChange = displayedRows;
                    verticalScrollBar.Value = Math.Min(maximumFirstRow, firstRow);
                    verticalScrollBar.IsEnabled = maximumFirstRow > 0;

                    int totalColumnWidth = dataGrid.Columns.GetColumnsWidth(
                        WinForms.DataGridViewElementStates.Visible);
                    // DisplayRectangle includes the row-header area. It cannot display
                    // column content, so exclude it when calculating the scroll range.
                    int rowHeaderWidth = dataGrid.RowHeadersVisible ? dataGrid.RowHeadersWidth : 0;
                    int viewportWidth = Math.Max(0, dataGrid.DisplayRectangle.Width - rowHeaderWidth);
                    int maximumHorizontalOffset = Math.Max(0, totalColumnWidth - viewportWidth);
                    horizontalScrollBar.Maximum = maximumHorizontalOffset;
                    horizontalScrollBar.ViewportSize = viewportWidth;
                    horizontalScrollBar.LargeChange = Math.Max(24, viewportWidth);
                    horizontalScrollBar.Value = Math.Min(
                        maximumHorizontalOffset,
                        Math.Max(0, dataGrid.HorizontalScrollingOffset));
                    horizontalScrollBar.IsEnabled = maximumHorizontalOffset > 0;
                }
                finally
                {
                    synchronizingScrollBars = false;
                }
            }

            void ScheduleScrollBarUpdate()
            {
                if (scrollBarUpdateScheduled)
                {
                    return;
                }

                scrollBarUpdateScheduled = true;
                Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Render,
                    new Action(() =>
                    {
                        scrollBarUpdateScheduled = false;
                        UpdateScrollBars();
                    }));
            }

            verticalScrollBar.ValueChanged += (_, _) =>
            {
                if (synchronizingScrollBars || !dataGrid.IsHandleCreated || dataGrid.RowCount == 0)
                {
                    return;
                }

                int rowIndex = Math.Clamp((int)Math.Round(verticalScrollBar.Value), 0, dataGrid.RowCount - 1);
                if (dataGrid.FirstDisplayedScrollingRowIndex != rowIndex)
                {
                    dataGrid.FirstDisplayedScrollingRowIndex = rowIndex;
                }
            };
            horizontalScrollBar.ValueChanged += (_, _) =>
            {
                if (synchronizingScrollBars || !dataGrid.IsHandleCreated)
                {
                    return;
                }

                dataGrid.HorizontalScrollingOffset = Math.Max(0, (int)Math.Round(horizontalScrollBar.Value));
            };

            dataGrid.VerticalWheelScrolled += (_, delta) =>
            {
                if ((WinForms.Control.ModifierKeys & WinForms.Keys.Shift) == WinForms.Keys.Shift)
                {
                    double horizontalTarget = horizontalScrollBar.Value - (delta / 120.0 * 48);
                    horizontalScrollBar.Value = Math.Clamp(
                        horizontalTarget,
                        horizontalScrollBar.Minimum,
                        horizontalScrollBar.Maximum);
                    return;
                }

                int wheelLines = WinForms.SystemInformation.MouseWheelScrollLines;
                if (wheelLines <= 0)
                {
                    wheelLines = 3;
                }

                double verticalTarget = verticalScrollBar.Value - (delta / 120.0 * wheelLines);
                verticalScrollBar.Value = Math.Clamp(
                    verticalTarget,
                    verticalScrollBar.Minimum,
                    verticalScrollBar.Maximum);
            };
            dataGrid.HorizontalWheelScrolled += (_, delta) =>
            {
                double target = horizontalScrollBar.Value + (delta / 120.0 * 48);
                horizontalScrollBar.Value = Math.Clamp(
                    target,
                    horizontalScrollBar.Minimum,
                    horizontalScrollBar.Maximum);
            };

            dataGrid.Scroll += (_, _) => UpdateScrollBars();
            dataGrid.Resize += (_, _) => ScheduleScrollBarUpdate();
            dataGrid.ColumnWidthChanged += (_, _) => ScheduleScrollBarUpdate();
            dataGrid.DataBindingComplete += (_, _) => ScheduleScrollBarUpdate();
            container.Loaded += (_, _) => ScheduleScrollBarUpdate();
            container.SizeChanged += (_, _) => ScheduleScrollBarUpdate();

            return container;
        }

        private static void FormatResultCell(WinForms.DataGridViewCellFormattingEventArgs e)
        {
            bool isNull = e.Value == null || e.Value == DBNull.Value;
            if (isNull)
            {
                e.Value = "NULL";
                e.CellStyle.ForeColor = Drawing.Color.FromArgb(102, 102, 102);
                e.CellStyle.Font = ResultNullFont;
                e.FormattingApplied = true;
                return;
            }

            if (e.Value is bool boolean)
            {
                e.Value = boolean ? "1" : "0";
                e.FormattingApplied = true;
            }
            else if (e.Value is DateTime dateTime)
            {
                e.Value = dateTime.ToString("yyyy-MM-dd HH:mm:ss.fff");
                e.FormattingApplied = true;
            }
            else if (e.Value is string text)
            {
                e.Value = text.Trim();
                e.FormattingApplied = true;
            }
        }

        private void DisposeDisplayedResults()
        {
            ResultsGridContainer.Children.Clear();
            ResultsGridContainer.RowDefinitions.Clear();
            foreach (var grid in _resultGrids)
            {
                try
                {
                    grid.DataSource = null;
                    grid.ContextMenuStrip?.Dispose();
                    grid.Dispose();
                }
                catch (Exception ex)
                {
                    AppLogger.Error(ex, "Failed to dispose result grid");
                }
            }
            _resultGrids.Clear();
            foreach (var table in _resultTables)
            {
                table.Dispose();
            }
            _resultTables.Clear();
        }

        private static void PaintRowNumber(
            WinForms.DataGridView grid,
            WinForms.DataGridViewCellPaintingEventArgs e)
        {
            if (e.ColumnIndex != -1 || e.RowIndex < 0 || e.Graphics == null)
            {
                return;
            }

            bool selected = grid.Rows[e.RowIndex].Selected;
            e.PaintBackground(e.CellBounds, selected);
            e.Paint(e.CellBounds, WinForms.DataGridViewPaintParts.Border);

            var textBounds = new Drawing.Rectangle(
                e.CellBounds.X + 2,
                e.CellBounds.Y,
                Math.Max(0, e.CellBounds.Width - 7),
                e.CellBounds.Height);
            Drawing.Color textColor = selected
                ? Drawing.Color.White
                : Drawing.Color.FromArgb(136, 136, 136);

            WinForms.TextRenderer.DrawText(
                e.Graphics,
                (e.RowIndex + 1).ToString(),
                grid.Font,
                textBounds,
                textColor,
                WinForms.TextFormatFlags.Right | WinForms.TextFormatFlags.VerticalCenter |
                WinForms.TextFormatFlags.NoPadding);
            e.Handled = true;
        }

        private static void CopyGridToClipboard(WinForms.DataGridView grid, bool includeHeaders)
        {
            var originalMode = grid.ClipboardCopyMode;
            grid.ClipboardCopyMode = includeHeaders
                ? WinForms.DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText
                : WinForms.DataGridViewClipboardCopyMode.EnableWithoutHeaderText;

            var clipboardContent = grid.GetClipboardContent();
            if (clipboardContent != null)
            {
                WinForms.Clipboard.SetDataObject(clipboardContent, true);
            }

            grid.ClipboardCopyMode = originalMode;
        }

        private void ExportResultTable(DataTable table, int resultIndex, ResultExportFormat format)
        {
            try
            {
                string extension = format switch
                {
                    ResultExportFormat.Csv => "csv",
                    ResultExportFormat.Tsv => "txt",
                    ResultExportFormat.Json => "json",
                    ResultExportFormat.Xml => "xml",
                    _ => "txt"
                };
                string filter = format switch
                {
                    ResultExportFormat.Csv => "CSV File (*.csv)|*.csv",
                    ResultExportFormat.Tsv => "Tab-delimited Text (*.txt)|*.txt",
                    ResultExportFormat.Json => "JSON File (*.json)|*.json",
                    ResultExportFormat.Xml => "XML File (*.xml)|*.xml",
                    _ => "All Files (*.*)|*.*"
                };

                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = $"Export Result Set {resultIndex + 1}",
                    FileName = $"QueryResult_{resultIndex + 1}.{extension}",
                    DefaultExt = extension,
                    Filter = filter,
                    AddExtension = true
                };
                if (dialog.ShowDialog() != true) return;

                switch (format)
                {
                    case ResultExportFormat.Csv:
                        File.WriteAllText(dialog.FileName, BuildDelimitedText(table, ','), new System.Text.UTF8Encoding(true));
                        break;
                    case ResultExportFormat.Tsv:
                        File.WriteAllText(dialog.FileName, BuildDelimitedText(table, '\t'), new System.Text.UTF8Encoding(true));
                        break;
                    case ResultExportFormat.Json:
                        File.WriteAllText(dialog.FileName, BuildJson(table), new System.Text.UTF8Encoding(false));
                        break;
                    case ResultExportFormat.Xml:
                        DataTable xmlTable = table.Copy();
                        xmlTable.TableName = $"ResultSet{resultIndex + 1}";
                        xmlTable.WriteXml(dialog.FileName, XmlWriteMode.WriteSchema);
                        xmlTable.Dispose();
                        break;
                }

                if (Window.GetWindow(this) is MainWindow mainWindow)
                {
                    mainWindow.UpdateStatusText($"Result set {resultIndex + 1} exported to {Path.GetFileName(dialog.FileName)}");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "Failed to export query result");
                MessageBox.Show($"Failed to export results: {ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string BuildDelimitedText(DataTable table, char delimiter)
        {
            var builder = new System.Text.StringBuilder();
            builder.AppendLine(string.Join(delimiter, table.Columns.Cast<DataColumn>().Select(column => EscapeDelimited(column.ColumnName, delimiter))));
            foreach (DataRow row in table.Rows)
            {
                builder.AppendLine(string.Join(delimiter, row.ItemArray.Select(value =>
                    EscapeDelimited(FormatExportValue(value), delimiter))));
            }
            return builder.ToString();
        }

        private static string EscapeDelimited(string value, char delimiter)
        {
            if (value.Contains(delimiter) || value.Contains('"') || value.Contains('\r') || value.Contains('\n'))
            {
                return $"\"{value.Replace("\"", "\"\"")}\"";
            }
            return value;
        }

        private static string FormatExportValue(object? value)
        {
            if (value == null || value == DBNull.Value) return string.Empty;
            return value switch
            {
                DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture),
                DateTimeOffset offset => offset.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                byte[] bytes => Convert.ToHexString(bytes),
                IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                _ => value.ToString() ?? string.Empty
            };
        }

        private static string BuildJson(DataTable table)
        {
            var rows = table.Rows.Cast<DataRow>()
                .Select(row => table.Columns.Cast<DataColumn>().ToDictionary(
                    column => column.ColumnName,
                    column => row[column] == DBNull.Value ? null : row[column]))
                .ToList();
            return JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true });
        }

        private string GetDefaultHtmlContent()
        {
            return @"<!DOCTYPE html><html><head><style>html,body,#container{width:100%;height:100%;margin:0;padding:0;overflow:hidden;background-color:#1e1e1e;}</style></head><body><div id='container'></div><script src='https://cdnjs.cloudflare.com/ajax/libs/require.js/2.3.6/require.min.js'></script><script>require.config({paths:{vs:'https://cdnjs.cloudflare.com/ajax/libs/monaco-editor/0.39.0/min/vs'}});var editor;var tables=[];var columns=[];require(['vs/editor/editor.main'],function(){monaco.languages.registerCompletionItemProvider('sql',{provideCompletionItems:function(model,position){var word=model.getWordUntilPosition(position);var range={startLineNumber:position.lineNumber,endLineNumber:position.lineNumber,startColumn:word.startColumn,endColumn:word.endColumn};var suggestions=[];var keywords=['SELECT','FROM','WHERE','INSERT','INTO','UPDATE','SET','DELETE','CREATE','TABLE','JOIN','INNER','LEFT','ON','GROUP','BY','ORDER','AND','OR','AS'];keywords.forEach(kw=>{suggestions.push({label:kw,kind:monaco.languages.CompletionItemKind.Keyword,insertText:kw,range:range});});tables.forEach(t=>{suggestions.push({label:t,kind:monaco.languages.CompletionItemKind.Class,insertText:t,detail:'Table',range:range});});columns.forEach(c=>{suggestions.push({label:c,kind:monaco.languages.CompletionItemKind.Field,insertText:c,detail:'Column',range:range});});return{suggestions:suggestions};}});editor=monaco.editor.create(document.getElementById('container'),{value:'-- Write SQL Query\nSELECT * FROM sys.databases;',language:'sql',theme:'vs-dark',automaticLayout:true,fontSize:14,acceptSuggestionOnEnter:'off',scrollbar:{verticalScrollbarSize:5,horizontalScrollbarSize:5,useShadows:false}});editor.addCommand(monaco.KeyCode.F5,function(){window.chrome.webview.postMessage({action:'execute'});});editor.addCommand(monaco.KeyMod.CtrlCmd|monaco.KeyCode.KeyN,function(){window.chrome.webview.postMessage({action:'newQuery'});});});function getQueryText(){if(editor){var selection=editor.getSelection();var selectedText=editor.getModel().getValueInRange(selection);if(selectedText&&selectedText.trim().length>0){return selectedText;}return editor.getValue();}return'';}function setQueryText(text){if(editor)editor.setValue(text);}function updateMetadata(t,c){tables=t||[];columns=c||[];}</script></body></html>";
        }

    }

    internal sealed class BufferedDataGridView : WinForms.DataGridView
    {
        private const int WmMouseHorizontalWheel = 0x020E;

        public event EventHandler<int>? VerticalWheelScrolled;
        public event EventHandler<int>? HorizontalWheelScrolled;

        public BufferedDataGridView()
        {
            DoubleBuffered = true;
            SetStyle(WinForms.ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnMouseWheel(WinForms.MouseEventArgs e)
        {
            VerticalWheelScrolled?.Invoke(this, e.Delta);
        }

        protected override void WndProc(ref WinForms.Message m)
        {
            if (m.Msg == WmMouseHorizontalWheel)
            {
                int delta = unchecked((short)((m.WParam.ToInt64() >> 16) & 0xFFFF));
                HorizontalWheelScrolled?.Invoke(this, delta);
                m.Result = IntPtr.Zero;
                return;
            }

            base.WndProc(ref m);
        }
    }

    internal sealed record SchemaTabInfo(string ObjectName, string ObjectType);
    internal sealed record ExecutionPlanTabInfo(string PlanXml);

    internal enum ResultExportFormat
    {
        Csv,
        Tsv,
        Json,
        Xml
    }
}
