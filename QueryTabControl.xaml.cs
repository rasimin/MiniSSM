using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
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
        public int TotalResultRows { get; private set; } = 0;
        public int TotalResultColumns { get; private set; } = 0;

        private readonly Dictionary<string, Dictionary<string, List<string>>> _metadataCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _storedProcedureCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _scalarFunctionCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _tableFunctionCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, string>> _objectTypeCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, Dictionary<string, SqlColumnAutocompleteInfo>>> _columnDetailCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, List<string>>> _routineParameterCache = new(StringComparer.OrdinalIgnoreCase);
        private List<string>? _databaseCache;
        private const double EditorMinHeight = 60;
        private const double ResultsMinHeight = 90;
        private static readonly Drawing.Font ResultNullFont = new("Segoe UI", 9F, Drawing.FontStyle.Italic);
        private static readonly Lazy<IHighlightingDefinition?> SqlHighlighting = new(LoadSqlHighlighting);
        private static CoreWebView2Environment? _sharedEnvironment;
        private double? _pendingEditorHeight;
        private bool _editorResizeScheduled;
        private CancellationTokenSource? _queryCancellationSource;

        private static async Task<CoreWebView2Environment> GetSharedEnvironmentAsync()
        {
            if (_sharedEnvironment == null)
            {
                string userDataFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WebView2_UserData");
                _sharedEnvironment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            }
            return _sharedEnvironment;
        }

        public QueryTabControl(string connectionString, string databaseName)
        {
            InitializeComponent();
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
            if (IsWebViewInitialized) return;
            await InitializeWebViewAsync();
        }

        private async Task InitializeWebViewAsync()
        {
            try
            {
                var env = await GetSharedEnvironmentAsync();
                await SqlEditorWebView.EnsureCoreWebView2Async(env);

                string htmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sql_editor.html");
                if (!File.Exists(htmlPath))
                {
                    File.WriteAllText(htmlPath, GetDefaultHtmlContent());
                }

                SqlEditorWebView.Source = new Uri(htmlPath);
                SqlEditorWebView.WebMessageReceived += SqlEditorWebView_WebMessageReceived;
                
                SqlEditorWebView.NavigationCompleted += async (sender, args) =>
                {
                    await SqlEditorWebView.ExecuteScriptAsync(@"
                        var checkEditor = setInterval(function() {
                            if (typeof focusEditor === 'function' && typeof monaco !== 'undefined') {
                                clearInterval(checkEditor);
                                " + (string.IsNullOrEmpty(InitialSql) ? "" : "setQueryText(" + JsonSerializer.Serialize(InitialSql) + ");") + @"
                                focusEditor();
                                " + (AutoExecute ? "window.chrome.webview.postMessage({ action: 'execute' });" : "") + @"
                            }
                        }, 50);
                    ");

                    // Also focus the WebView2 control in WPF
                    _ = Dispatcher.BeginInvoke(new Action(() =>
                    {
                        SqlEditorWebView.Focus();
                    }), System.Windows.Threading.DispatcherPriority.Input);

                    // The first metadata push can happen before Monaco finishes loading.
                    // Push it again after navigation so autocomplete has the table list.
                    await CacheAndRefreshAutocompleteAsync();
                };
                
                IsWebViewInitialized = true;

                // Cache metadata & update autocompletion for this database
                await CacheAndRefreshAutocompleteAsync();
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "Failed to initialize Monaco SQL Editor");
                TxtMessages.Text = $"Error initializing Monaco SQL Editor: {ex.Message}";
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
                    _ = CacheAndRefreshAutocompleteAsync();
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

        public async void ExecuteQuery()
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
            ResultsGridContainer.Children.Clear();
            ResultsGridContainer.RowDefinitions.Clear();
            TxtMessages.Text = "";
            var cancellationSource = new CancellationTokenSource();
            _queryCancellationSource = cancellationSource;
            LoadingMessageText.Text = "Executing query...";
            CancelQueryButton.Content = "Cancel query";
            CancelQueryButton.IsEnabled = true;
            LoadingOverlay.Visibility = Visibility.Visible;
            TabResults.SelectedIndex = 1;

            var messageProgress = new Progress<string>(AppendLiveQueryMessage);

            var mainWindow = Window.GetWindow(this) as MainWindow;
            mainWindow?.UpdateStatusText("Executing query...");

            try
            {
                var result = await DatabaseHelper.ExecuteQueryAsync(
                    ConnectionString,
                    DatabaseName,
                    sqlQuery,
                    messageProgress,
                    cancellationSource.Token);

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
                    mainWindow?.UpdateStatusTime($"Success: {result.ExecutionTime.TotalMilliseconds:F2} ms");
                    mainWindow?.UpdateStatusRowsAndColumns(TotalResultRows, TotalResultColumns);

                    if (System.Text.RegularExpressions.Regex.IsMatch(
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

        public async Task CacheAndRefreshAutocompleteAsync()
        {
            if (!IsWebViewInitialized) return;

            try
            {
                if (!_metadataCache.ContainsKey(DatabaseName))
                {
                    var dbConnString = DatabaseHelper.BuildConnectionString(ConnectionString, DatabaseName);
                    using var connection = new Microsoft.Data.SqlClient.SqlConnection(dbConnString);
                    await connection.OpenAsync();
                    
                    var query = @"
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

                    var tableColumns = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                    var objectTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var columnDetails = new Dictionary<string, Dictionary<string, SqlColumnAutocompleteInfo>>(StringComparer.OrdinalIgnoreCase);

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
                    _metadataCache[DatabaseName] = tableColumns;
                    _objectTypeCache[DatabaseName] = objectTypes;
                    _columnDetailCache[DatabaseName] = columnDetails;
                }

                if (!_storedProcedureCache.ContainsKey(DatabaseName))
                {
                    _storedProcedureCache[DatabaseName] =
                        await DatabaseHelper.GetStoredProceduresAsync(ConnectionString, DatabaseName);
                }

                if (!_scalarFunctionCache.ContainsKey(DatabaseName))
                {
                    _scalarFunctionCache[DatabaseName] =
                        await DatabaseHelper.GetFunctionsAsync(ConnectionString, DatabaseName, tableValued: false);
                }

                if (!_tableFunctionCache.ContainsKey(DatabaseName))
                {
                    _tableFunctionCache[DatabaseName] =
                        await DatabaseHelper.GetFunctionsAsync(ConnectionString, DatabaseName, tableValued: true);
                }

                if (!_routineParameterCache.ContainsKey(DatabaseName))
                {
                    var parameters = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                    var dbConnString = DatabaseHelper.BuildConnectionString(ConnectionString, DatabaseName);
                    using var parameterConnection = new Microsoft.Data.SqlClient.SqlConnection(dbConnString);
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
                    _routineParameterCache[DatabaseName] = parameters;
                }

                _databaseCache ??= await DatabaseHelper.GetDatabasesAsync(ConnectionString);

                var meta = _metadataCache[DatabaseName];
                var storedProcedures = _storedProcedureCache[DatabaseName];
                var scalarFunctions = _scalarFunctionCache[DatabaseName];
                var tableFunctions = _tableFunctionCache[DatabaseName];
                var payload = new
                {
                    columns = meta,
                    objectTypes = _objectTypeCache[DatabaseName],
                    columnDetails = _columnDetailCache[DatabaseName],
                    storedProcedures,
                    scalarFunctions,
                    tableFunctions,
                    routineParameters = _routineParameterCache[DatabaseName],
                    databases = _databaseCache,
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

        private void InvalidateAutocompleteCache(string databaseName)
        {
            _metadataCache.Remove(databaseName);
            _objectTypeCache.Remove(databaseName);
            _columnDetailCache.Remove(databaseName);
            _storedProcedureCache.Remove(databaseName);
            _scalarFunctionCache.Remove(databaseName);
            _tableFunctionCache.Remove(databaseName);
            _routineParameterCache.Remove(databaseName);
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
            ResultsGridContainer.Children.Clear();
            ResultsGridContainer.RowDefinitions.Clear();

            if (dataTables == null || dataTables.Count == 0)
            {
                return;
            }

            for (int i = 0; i < dataTables.Count; i++)
            {
                ResultsGridContainer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

                var grid = CreateResultDataGrid(dataTables[i]);
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

        private FrameworkElement CreateResultDataGrid(DataTable dataTable)
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
            dataGrid.RowHeaderMouseClick += (_, e) =>
            {
                if (e.RowIndex < 0)
                {
                    return;
                }

                dataGrid.ClearSelection();
                if (dataGrid.Rows[e.RowIndex].Cells.Count > 0)
                {
                    dataGrid.CurrentCell = dataGrid.Rows[e.RowIndex].Cells[0];
                }

                foreach (WinForms.DataGridViewCell cell in dataGrid.Rows[e.RowIndex].Cells)
                {
                    cell.Selected = true;
                }
                dataGrid.Rows[e.RowIndex].Selected = true;
            };

            var contextMenu = new WinForms.ContextMenuStrip
            {
                BackColor = Drawing.Color.FromArgb(30, 30, 30),
                ForeColor = Drawing.Color.FromArgb(204, 204, 204),
                ShowImageMargin = false
            };
            contextMenu.Items.Add("Copy", null, (_, _) => CopyGridToClipboard(dataGrid, false));
            contextMenu.Items.Add("Copy with Headers", null, (_, _) => CopyGridToClipboard(dataGrid, true));
            dataGrid.ContextMenuStrip = contextMenu;
            dataGrid.DataSource = dataTable;

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
                    int viewportWidth = Math.Max(0, dataGrid.DisplayRectangle.Width);
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

        private string GetDefaultHtmlContent()
        {
            return @"<!DOCTYPE html><html><head><style>html,body,#container{width:100%;height:100%;margin:0;padding:0;overflow:hidden;background-color:#1e1e1e;}</style></head><body><div id='container'></div><script src='https://cdnjs.cloudflare.com/ajax/libs/require.js/2.3.6/require.min.js'></script><script>require.config({paths:{vs:'https://cdnjs.cloudflare.com/ajax/libs/monaco-editor/0.39.0/min/vs'}});var editor;var tables=[];var columns=[];require(['vs/editor/editor.main'],function(){monaco.languages.registerCompletionItemProvider('sql',{provideCompletionItems:function(model,position){var word=model.getWordUntilPosition(position);var range={startLineNumber:position.lineNumber,endLineNumber:position.lineNumber,startColumn:word.startColumn,endColumn:word.endColumn};var suggestions=[];var keywords=['SELECT','FROM','WHERE','INSERT','INTO','UPDATE','SET','DELETE','CREATE','TABLE','JOIN','INNER','LEFT','ON','GROUP','BY','ORDER','AND','OR','AS'];keywords.forEach(kw=>{suggestions.push({label:kw,kind:monaco.languages.CompletionItemKind.Keyword,insertText:kw,range:range});});tables.forEach(t=>{suggestions.push({label:t,kind:monaco.languages.CompletionItemKind.Class,insertText:t,detail:'Table',range:range});});columns.forEach(c=>{suggestions.push({label:c,kind:monaco.languages.CompletionItemKind.Field,insertText:c,detail:'Column',range:range});});return{suggestions:suggestions};}});editor=monaco.editor.create(document.getElementById('container'),{value:'-- Write SQL Query\nSELECT * FROM sys.databases;',language:'sql',theme:'vs-dark',automaticLayout:true,fontSize:14,scrollbar:{verticalScrollbarSize:5,horizontalScrollbarSize:5,useShadows:false}});editor.addCommand(monaco.KeyCode.F5,function(){window.chrome.webview.postMessage({action:'execute'});});editor.addCommand(monaco.KeyMod.CtrlCmd|monaco.KeyCode.KeyN,function(){window.chrome.webview.postMessage({action:'newQuery'});});});function getQueryText(){if(editor){var selection=editor.getSelection();var selectedText=editor.getModel().getValueInRange(selection);if(selectedText&&selectedText.trim().length>0){return selectedText;}return editor.getValue();}return'';}function setQueryText(text){if(editor)editor.setValue(text);}function updateMetadata(t,c){tables=t||[];columns=c||[];}</script></body></html>";
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
}
