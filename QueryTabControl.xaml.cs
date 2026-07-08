using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Data;
using Microsoft.Web.WebView2.Core;

namespace SSMS
{
    public partial class QueryTabControl : UserControl
    {
        public string ConnectionString { get; set; }
        public string DatabaseName { get; set; }
        public string InitialSql { get; set; } = string.Empty;
        public bool AutoExecute { get; set; } = false;
        public bool IsWebViewInitialized { get; private set; } = false;
        public int TotalResultRows { get; private set; } = 0;
        public int TotalResultColumns { get; private set; } = 0;

        private readonly Dictionary<string, Dictionary<string, List<string>>> _metadataCache = new(StringComparer.OrdinalIgnoreCase);
        private const double EditorMinHeight = 60;
        private const double ResultsMinHeight = 90;
        private const double ResultsResizeGripHeight = 12;
        private bool _isResultsPaneResizeActive;

        private static CoreWebView2Environment? _sharedEnvironment;

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
            ResizeEditorResults(e.VerticalChange);
            e.Handled = true;
        }

        private void ResultsPane_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.GetPosition(ResultsPane).Y > ResultsResizeGripHeight)
            {
                return;
            }

            _isResultsPaneResizeActive = true;
            ResultsPane.CaptureMouse();
            e.Handled = true;
        }

        private void ResultsPane_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_isResultsPaneResizeActive && e.LeftButton == MouseButtonState.Pressed)
            {
                double desiredEditorHeight = e.GetPosition(QueryLayoutGrid).Y - EditorResultsSplitter.ActualHeight / 2;
                ResizeEditorResultsTo(desiredEditorHeight);
                e.Handled = true;
                return;
            }

            ResultsPane.Cursor = e.GetPosition(ResultsPane).Y <= ResultsResizeGripHeight ? Cursors.SizeNS : Cursors.Arrow;
        }

        private void ResultsPane_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isResultsPaneResizeActive)
            {
                _isResultsPaneResizeActive = false;
                ResultsPane.ReleaseMouseCapture();
                e.Handled = true;
            }
        }

        private void ResizeEditorResults(double verticalDelta)
        {
            double currentEditorHeight = EditorRow.ActualHeight > 0 ? EditorRow.ActualHeight : QueryLayoutGrid.RowDefinitions[0].ActualHeight;
            ResizeEditorResultsTo(currentEditorHeight + verticalDelta);
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
                if (doc.RootElement.TryGetProperty("action", out var action) && action.GetString() == "execute")
                {
                    ExecuteQuery();
                }
            }
            catch { }
        }

        public async void ExecuteQuery()
        {
            if (!IsWebViewInitialized) return;

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
            LoadingOverlay.Visibility = Visibility.Visible;

            var mainWindow = Window.GetWindow(this) as MainWindow;
            mainWindow?.UpdateStatusText("Executing query...");

            try
            {
                var result = await Task.Run(() => DatabaseHelper.ExecuteQueryAsync(ConnectionString, DatabaseName, sqlQuery));

                // Populate Results Pane
                if (result.IsSuccess)
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
                }
                else
                {
                    TotalResultRows = 0;
                    TotalResultColumns = 0;
                    TxtMessages.Text = result.Message;
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
                TxtMessages.Text = $"Unexpected query execution error: {ex.Message}";
                TabResults.SelectedIndex = 1;
                mainWindow?.UpdateStatusTime("Error");
                mainWindow?.UpdateStatusRowsAndColumns(0, 0);
            }
            finally
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
            }
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
                        SELECT s.name AS SchemaName, t.name AS TableName, c.name AS ColumnName
                        FROM sys.tables t
                        JOIN sys.schemas s ON t.schema_id = s.schema_id
                        JOIN sys.columns c ON t.object_id = c.object_id
                        ORDER BY s.name, t.name, c.column_id;";

                    var tableColumns = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

                    using var command = new Microsoft.Data.SqlClient.SqlCommand(query, connection);
                    using var reader = await command.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        string schema = reader.GetString(0);
                        string table = reader.GetString(1);
                        string column = reader.GetString(2);
                        
                        string fullKey = $"{schema}.{table}";
                        string shortKey = table;

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
                }

                var meta = _metadataCache[DatabaseName];
                string jsonMeta = JsonSerializer.Serialize(meta);
                await SqlEditorWebView.ExecuteScriptAsync($"updateMetadata({jsonMeta});");
            }
            catch 
            {
                // Silently bypass autocomplete load errors (e.g. permission restriction on metadata tables)
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

        private DataGrid CreateResultDataGrid(DataTable dataTable)
        {
             var dataGrid = new DataGrid
            {
                AutoGenerateColumns = true,
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#1E1E1E")!,
                RowBackground = (SolidColorBrush)new BrushConverter().ConvertFromString("#1E1E1E")!,
                AlternatingRowBackground = (SolidColorBrush)new BrushConverter().ConvertFromString("#252526")!,
                GridLinesVisibility = DataGridGridLinesVisibility.All,
                HorizontalGridLinesBrush = (SolidColorBrush)new BrushConverter().ConvertFromString("#2D2D30")!,
                VerticalGridLinesBrush = (SolidColorBrush)new BrushConverter().ConvertFromString("#2D2D30")!,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                FontSize = 12,
                SelectionUnit = DataGridSelectionUnit.Cell,
                SelectionMode = DataGridSelectionMode.Extended,
                ItemsSource = dataTable.DefaultView,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };

            dataGrid.AutoGeneratingColumn += DataGrid_AutoGeneratingColumn;

            // Set high-performance text rendering parameters (disable sub-pixel text measurement layout shifts during scroll)
            TextOptions.SetTextFormattingMode(dataGrid, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(dataGrid, TextRenderingMode.ClearType);
            RenderOptions.SetClearTypeHint(dataGrid, ClearTypeHint.Enabled);

            ScrollViewer.SetHorizontalScrollBarVisibility(dataGrid, ScrollBarVisibility.Auto);
            ScrollViewer.SetVerticalScrollBarVisibility(dataGrid, ScrollBarVisibility.Auto);

            // Set virtualization properties
            VirtualizingPanel.SetIsVirtualizing(dataGrid, true);
            VirtualizingPanel.SetVirtualizationMode(dataGrid, VirtualizationMode.Recycling);
            VirtualizingPanel.SetScrollUnit(dataGrid, ScrollUnit.Item);
            VirtualizingPanel.SetCacheLengthUnit(dataGrid, VirtualizationCacheLengthUnit.Item);
            VirtualizingPanel.SetCacheLength(dataGrid, new VirtualizationCacheLength(0));
            dataGrid.EnableRowVirtualization = true;
            dataGrid.EnableColumnVirtualization = true;
            dataGrid.ColumnWidth = new DataGridLength(120);
            ScrollViewer.SetIsDeferredScrollingEnabled(dataGrid, false);
            ScrollViewer.SetCanContentScroll(dataGrid, true);

            // Tracing scroll performance
            var scrollStopwatch = new System.Diagnostics.Stopwatch();
            int rowsLoadedCount = 0;
            int rowsUnloadedCount = 0;

            dataGrid.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((s, e) =>
            {
                if (AppLogger.EnablePerformanceLogging && e.VerticalChange != 0)
                {
                    scrollStopwatch.Restart();
                    AppLogger.Info($"[Scroll] Start scrolling. Offset: {e.VerticalOffset:F1}, Change: {e.VerticalChange:F1}");
                }
            }));

            dataGrid.LoadingRow += (s, e) =>
            {
                if (AppLogger.EnablePerformanceLogging)
                {
                    rowsLoadedCount++;
                }
            };

            dataGrid.UnloadingRow += (s, e) =>
            {
                if (AppLogger.EnablePerformanceLogging)
                {
                    rowsUnloadedCount++;
                }
            };

            dataGrid.LayoutUpdated += (s, e) =>
            {
                if (AppLogger.EnablePerformanceLogging && scrollStopwatch.IsRunning)
                {
                    scrollStopwatch.Stop();
                    double elapsedMs = scrollStopwatch.Elapsed.TotalMilliseconds;
                    int activeRowContainers = GetVisualDescendantsCount<DataGridRow>(dataGrid);
                    int activeCellContainers = GetVisualDescendantsCount<DataGridCell>(dataGrid);
                    int renderTier = RenderCapability.Tier >> 16;
                    AppLogger.Info($"[Render] LayoutUpdated. Time: {elapsedMs:F2} ms. Rows Loaded: {rowsLoadedCount}, Rows Recycled: {rowsUnloadedCount}, Alive Rows: {activeRowContainers}/{dataGrid.Items.Count}, Alive Cells: {activeCellContainers}, Columns: {dataGrid.Columns.Count}, RenderTier: {renderTier}");
                    
                    if (elapsedMs > 16.6)
                    {
                        AppLogger.Info($"[WARN-LAG] Slow frame detected: {elapsedMs:F2} ms (> 16.6ms frame budget)!");
                    }

                    // Reset counts for the next scroll layout cycle
                    rowsLoadedCount = 0;
                    rowsUnloadedCount = 0;
                }
            };

            // Context Menu (Copy & Copy with Headers)
            var contextMenu = new ContextMenu { Background = dataGrid.Background, BorderBrush = (System.Windows.Media.SolidColorBrush)new System.Windows.Media.BrushConverter().ConvertFromString("#2D2D30")! };
            
            var copyMenu = new MenuItem { Header = "Copy", Foreground = (System.Windows.Media.SolidColorBrush)new System.Windows.Media.BrushConverter().ConvertFromString("#CCCCCC")! };
            copyMenu.Click += (s, e) => CopyGridToClipboard(dataGrid, false);
            
            var copyHeadersMenu = new MenuItem { Header = "Copy with Headers", Foreground = copyMenu.Foreground };
            copyHeadersMenu.Click += (s, e) => CopyGridToClipboard(dataGrid, true);

            contextMenu.Items.Add(copyMenu);
            contextMenu.Items.Add(copyHeadersMenu);
            dataGrid.ContextMenu = contextMenu;

            return dataGrid;
        }

        private static int GetVisualDescendantsCount<T>(DependencyObject parent) where T : DependencyObject
        {
            int count = 0;
            int childrenCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childrenCount; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T)
                {
                    count++;
                }
                count += GetVisualDescendantsCount<T>(child);
            }
            return count;
        }

        private void CopyGridToClipboard(DataGrid grid, bool includeHeaders)
        {
            grid.ClipboardCopyMode = includeHeaders ? DataGridClipboardCopyMode.IncludeHeader : DataGridClipboardCopyMode.ExcludeHeader;
            ApplicationCommands.Copy.Execute(null, grid);
        }

        private string GetDefaultHtmlContent()
        {
            return @"<!DOCTYPE html><html><head><style>html,body,#container{width:100%;height:100%;margin:0;padding:0;overflow:hidden;background-color:#1e1e1e;}</style></head><body><div id='container'></div><script src='https://cdnjs.cloudflare.com/ajax/libs/require.js/2.3.6/require.min.js'></script><script>require.config({paths:{vs:'https://cdnjs.cloudflare.com/ajax/libs/monaco-editor/0.39.0/min/vs'}});var editor;var tables=[];var columns=[];require(['vs/editor/editor.main'],function(){monaco.languages.registerCompletionItemProvider('sql',{provideCompletionItems:function(model,position){var word=model.getWordUntilPosition(position);var range={startLineNumber:position.lineNumber,endLineNumber:position.lineNumber,startColumn:word.startColumn,endColumn:word.endColumn};var suggestions=[];var keywords=['SELECT','FROM','WHERE','INSERT','INTO','UPDATE','SET','DELETE','CREATE','TABLE','JOIN','INNER','LEFT','ON','GROUP','BY','ORDER','AND','OR','AS'];keywords.forEach(kw=>{suggestions.push({label:kw,kind:monaco.languages.CompletionItemKind.Keyword,insertText:kw,range:range});});tables.forEach(t=>{suggestions.push({label:t,kind:monaco.languages.CompletionItemKind.Class,insertText:t,detail:'Table',range:range});});columns.forEach(c=>{suggestions.push({label:c,kind:monaco.languages.CompletionItemKind.Field,insertText:c,detail:'Column',range:range});});return{suggestions:suggestions};}});editor=monaco.editor.create(document.getElementById('container'),{value:'-- Write SQL Query\nSELECT * FROM sys.databases;',language:'sql',theme:'vs-dark',automaticLayout:true,fontSize:14,scrollbar:{verticalScrollbarSize:5,horizontalScrollbarSize:5,useShadows:false}});editor.addCommand(monaco.KeyCode.F5,function(){window.chrome.webview.postMessage({action:'execute'});});});function getQueryText(){if(editor){var selection=editor.getSelection();var selectedText=editor.getModel().getValueInRange(selection);if(selectedText&&selectedText.trim().length>0){return selectedText;}return editor.getValue();}return'';}function setQueryText(text){if(editor)editor.setValue(text);}function updateMetadata(t,c){tables=t||[];columns=c||[];}</script></body></html>";
        }

        private void DataGrid_AutoGeneratingColumn(object? sender, DataGridAutoGeneratingColumnEventArgs e)
        {
            if (e.PropertyType == typeof(bool) || e.PropertyType == typeof(bool?))
            {
                var textCol = new DataGridTextColumn
                {
                    Header = e.Column.Header,
                    Binding = new Binding(e.PropertyName)
                    {
                        Converter = new SqlCellTextConverter(),
                        Mode = BindingMode.OneWay
                    }
                };
                e.Column = textCol;
            }

            if (e.Column is DataGridTextColumn textColumn)
            {
                string bindingPath = e.PropertyName;
                
                var textBinding = new Binding(bindingPath)
                {
                    Converter = new SqlCellTextConverter(),
                    Mode = BindingMode.OneWay
                };
                textColumn.Binding = textBinding;

                var textBlockStyle = new Style(typeof(TextBlock));
                
                textBlockStyle.Setters.Add(new Setter(TextBlock.FontStyleProperty, new Binding(bindingPath)
                {
                    Converter = new SqlCellFontStyleConverter(),
                    Mode = BindingMode.OneWay
                }));

                textBlockStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new Binding(bindingPath)
                {
                    Converter = new SqlCellForegroundConverter(),
                    Mode = BindingMode.OneWay
                }));

                textColumn.ElementStyle = textBlockStyle;
            }
        }
    }

    public class SqlCellTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value == null || value == DBNull.Value)
            {
                return "NULL";
            }
            if (value is bool b)
            {
                return b ? "1" : "0";
            }
            if (value is DateTime dt)
            {
                return dt.ToString("yyyy-MM-dd HH:mm:ss.fff");
            }
            return value.ToString() ?? "";
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class SqlCellForegroundConverter : IValueConverter
    {
        private static readonly SolidColorBrush NullBrush = (SolidColorBrush)new BrushConverter().ConvertFromString("#666666")!;
        private static readonly SolidColorBrush NormalBrush = (SolidColorBrush)new BrushConverter().ConvertFromString("#CCCCCC")!;

        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value == null || value == DBNull.Value)
            {
                return NullBrush;
            }
            return NormalBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class SqlCellFontStyleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value == null || value == DBNull.Value)
            {
                return FontStyles.Italic;
            }
            return FontStyles.Normal;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
