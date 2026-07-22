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
                var resource = Application.GetResourceStream(new Uri("Resources/SqlDark.xshd", UriKind.Relative));
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