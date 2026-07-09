using System.Data;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Media;
using Microsoft.Data.SqlClient;

namespace SSMS;

public partial class QueryTabControl : UserControl
{
    private bool _editorReady;
    private string _initialSql;
    private readonly SemaphoreSlim _metadataGate = new(1, 1);
    private string? _loadedMetadataDatabase;

    private bool _autoExecute;

    public string ConnectionString { get; }
    public string DatabaseName { get; private set; }
    public string? FilePath { get; set; }
    public QueryStatusEventArgs? LastStatus { get; private set; }
    public event EventHandler<QueryStatusEventArgs>? StatusChanged;
    public event EventHandler<ObjectDefinitionRequestEventArgs>? ObjectDefinitionRequested;

    public QueryTabControl() : this("", "master") { }

    public QueryTabControl(string connectionString, string databaseName, string? initialSql = null, bool autoExecute = false)
    {
        ConnectionString = connectionString;
        DatabaseName = databaseName;
        _initialSql = initialSql ?? "";
        _autoExecute = autoExecute;
        InitializeComponent();
        AttachedToVisualTree += OnAttached;
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        AttachedToVisualTree -= OnAttached;
        var htmlPath = Path.Combine(AppContext.BaseDirectory, "sql_editor.html");
        if (!File.Exists(htmlPath))
        {
            MessagesBox.Text = $"Monaco editor file was not found: {htmlPath}";
            return;
        }
        EditorWebView.Source = new Uri(htmlPath);
    }

    private async void EditorWebView_OnNavigationCompleted(
        object? sender, WebViewNavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess) return;
        _editorReady = true;
        
        string checkScript = @"
            (function() {
                var checkEditor = setInterval(function() {
                    if (typeof focusEditor === 'function' && typeof monaco !== 'undefined') {
                        clearInterval(checkEditor);
                        " + (string.IsNullOrEmpty(_initialSql) ? "" : "setQueryText(" + JsonSerializer.Serialize(_initialSql) + ");") + @"
                        focusEditor();
                        " + (_autoExecute ? "window.chrome.webview.postMessage({ action: 'execute' });" : "") + @"
                    }
                }, 50);
            })();
        ";
        
        await EditorWebView.InvokeScript(checkScript);
        await RefreshMetadataAsync();
    }

    private void EditorWebView_OnWebMessageReceived(object? sender, WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.Body ?? "{}");
            if (!doc.RootElement.TryGetProperty("action", out var action)) return;
            switch (action.GetString())
            {
                case "textChanged":
                    _initialSql = doc.RootElement.GetProperty("text").GetString() ?? "";
                    break;
                case "execute":
                    _ = ExecuteQueryAsync();
                    break;
                case "editorReady":
                    _ = RefreshMetadataAsync();
                    break;
                case "viewObjectDefinition":
                    ObjectDefinitionRequested?.Invoke(this, new ObjectDefinitionRequestEventArgs(
                        doc.RootElement.GetProperty("objectName").GetString() ?? "",
                        doc.RootElement.GetProperty("objectType").GetString() ?? ""));
                    break;
                case "loadDatabaseMetadata":
                    _ = PushCrossDatabaseMetadataAsync(
                        doc.RootElement.GetProperty("databaseName").GetString() ?? "");
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error(ex, "Failed to process Monaco message.");
        }
    }

    public async Task ExecuteQueryAsync()
    {
        if (!_editorReady) return;
        var sql = await GetQueryTextAsync();
        if (string.IsNullOrWhiteSpace(sql))
        {
            MessagesBox.Text = "Please type an SQL query.";
            ResultTabs.SelectedIndex = 1;
            return;
        }

        LoadingOverlay.IsVisible = true;
        LastStatus = new QueryStatusEventArgs("Executing query...", "", 0, 0);
        StatusChanged?.Invoke(this, LastStatus);
        try
        {
            var result = await DatabaseHelper.ExecuteQueryAsync(ConnectionString, DatabaseName, sql);
            MessagesBox.Text = result.Message;
            DisplayResults(result.DataTables);
            var rows = result.DataTables.Sum(x => x.Rows.Count);
            var columns = result.DataTables.Sum(x => x.Columns.Count);
            ResultTabs.SelectedIndex = result.IsSuccess && result.DataTables.Count > 0 ? 0 : 1;
            LastStatus = new QueryStatusEventArgs(
                result.IsSuccess ? "Query completed" : "Query failed",
                $"{result.ExecutionTime.TotalSeconds:0.000} sec", rows, columns);
            StatusChanged?.Invoke(this, LastStatus);
        }
        catch (Exception ex)
        {
            AppLogger.Error(ex, "Query execution failed.");
            MessagesBox.Text = ex.Message;
            ResultTabs.SelectedIndex = 1;
        }
        finally
        {
            LoadingOverlay.IsVisible = false;
        }
    }

    private void DisplayResults(IReadOnlyList<DataTable> tables)
    {
        ResultsGridContainer.Children.Clear();
        ResultsGridContainer.RowDefinitions.Clear();

        if (tables == null || tables.Count == 0) return;

        for (int i = 0; i < tables.Count; i++)
        {
            var table = tables[i];

            // Add RowDefinition for DataGrid
            ResultsGridContainer.RowDefinitions.Add(new RowDefinition(GridLength.Parse("*")));

            var grid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                ItemsSource = table.Rows.Cast<DataRow>().ToList(),
                RowHeight = 25,
                ColumnHeaderHeight = 28,
                Background = Brush.Parse("#1E1E1E"),
                GridLinesVisibility = DataGridGridLinesVisibility.All,
                HeadersVisibility = DataGridHeadersVisibility.All
            };

            // Custom row header numbers
            grid.LoadingRow += (_, e) =>
            {
                e.Row.Header = (e.Row.Index + 1).ToString();
            };

            foreach (DataColumn column in table.Columns)
            {
                grid.Columns.Add(new DataGridTextColumn
                {
                    Header = column.ColumnName,
                    Binding = new Binding($"ItemArray[{column.Ordinal}]")
                    {
                        Converter = SqlResultValueConverter.Instance
                    },
                    Width = new DataGridLength(120)
                });
            }

            Grid.SetRow(grid, ResultsGridContainer.RowDefinitions.Count - 1);
            ResultsGridContainer.Children.Add(grid);

            // Add Splitter if there's a next table
            if (i < tables.Count - 1)
            {
                ResultsGridContainer.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

                var splitter = new GridSplitter
                {
                    Height = 4,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Background = Brush.Parse("#2D2D30"),
                    ResizeDirection = GridResizeDirection.Rows
                };

                Grid.SetRow(splitter, ResultsGridContainer.RowDefinitions.Count - 1);
                ResultsGridContainer.Children.Add(splitter);
            }
        }
    }

    public async Task<string> GetQueryTextAsync()
    {
        var result = await EditorWebView.InvokeScript("getQueryText();");
        if (string.IsNullOrEmpty(result)) return "";
        try { return JsonSerializer.Deserialize<string>(result) ?? result; }
        catch { return result; }
    }

    public Task SetQueryTextAsync(string text)
    {
        _initialSql = text;
        return _editorReady
            ? EditorWebView.InvokeScript($"setQueryText({JsonSerializer.Serialize(text)});")
            : Task.CompletedTask;
    }

    public Task RunEditorCommandAsync(string command) =>
        _editorReady ? EditorWebView.InvokeScript(command) : Task.CompletedTask;

    public async Task InsertTextAsync(string text)
    {
        if (!_editorReady) return;
        try
        {
            await EditorWebView.InvokeScript($"insertTextAtCursor({JsonSerializer.Serialize(text)});");
        }
        catch (Exception ex)
        {
            AppLogger.Error(ex, "InsertText failed");
        }
    }

    public async Task ChangeDatabaseAsync(string databaseName)
    {
        if (DatabaseName.Equals(databaseName, StringComparison.OrdinalIgnoreCase) &&
            _loadedMetadataDatabase?.Equals(databaseName, StringComparison.OrdinalIgnoreCase) == true)
            return;
        DatabaseName = databaseName;
        await RefreshMetadataAsync();
    }

    private async Task RefreshMetadataAsync()
    {
        if (!_editorReady || string.IsNullOrWhiteSpace(DatabaseName)) return;
        var requestedDatabase = DatabaseName;
        await _metadataGate.WaitAsync();
        try
        {
            if (_loadedMetadataDatabase?.Equals(
                    requestedDatabase, StringComparison.OrdinalIgnoreCase) == true)
                return;

            AppLogger.Info($"Loading Monaco metadata for {requestedDatabase}.");
            var (columns, objectTypes, columnDetails) = await LoadColumnMetadataAsync(requestedDatabase);
            var storedProcedures = await DatabaseHelper.GetStoredProceduresAsync(ConnectionString, requestedDatabase);
            var scalarFunctions = await DatabaseHelper.GetFunctionsAsync(ConnectionString, requestedDatabase, false);
            var tableFunctions = await DatabaseHelper.GetFunctionsAsync(ConnectionString, requestedDatabase, true);
            var databases = await DatabaseHelper.GetDatabasesAsync(ConnectionString);
            var payload = new
            {
                columns, objectTypes, columnDetails, storedProcedures,
                scalarFunctions, tableFunctions,
                routineParameters = new Dictionary<string, List<string>>(),
                databases, activeDatabase = requestedDatabase
            };
            await EditorWebView.InvokeScript(
                $"updateMetadata({JsonSerializer.Serialize(payload)});");
            _loadedMetadataDatabase = requestedDatabase;
            AppLogger.Info($"Monaco metadata loaded for {requestedDatabase}: {columns.Count} objects.");
        }
        catch (Exception ex)
        {
            AppLogger.Error(ex, $"Failed to load autocomplete metadata for {requestedDatabase}.");
        }
        finally
        {
            _metadataGate.Release();
        }
    }

    private async Task PushCrossDatabaseMetadataAsync(string databaseName)
    {
        if (string.IsNullOrWhiteSpace(databaseName)) return;
        try
        {
            var (columns, objectTypes, _) = await LoadColumnMetadataAsync(databaseName);
            var scalarFunctions = await DatabaseHelper.GetFunctionsAsync(ConnectionString, databaseName, false);
            var tableFunctions = await DatabaseHelper.GetFunctionsAsync(ConnectionString, databaseName, true);
            await EditorWebView.InvokeScript(
                $"updateDatabaseMetadata({JsonSerializer.Serialize(databaseName)}, " +
                $"{JsonSerializer.Serialize(new { columns, objectTypes, scalarFunctions, tableFunctions })});");
        }
        catch (Exception ex)
        {
            AppLogger.Error(ex, $"Failed to load cross-database metadata for {databaseName}.");
        }
    }

    private async Task<(Dictionary<string, List<string>> Columns,
        Dictionary<string, string> ObjectTypes,
        Dictionary<string, Dictionary<string, ColumnInfo>> ColumnDetails)>
        LoadColumnMetadataAsync(string databaseName)
    {
        var columns = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var details = new Dictionary<string, Dictionary<string, ColumnInfo>>(StringComparer.OrdinalIgnoreCase);
        await using var connection = new SqlConnection(
            DatabaseHelper.BuildConnectionString(ConnectionString, databaseName));
        await connection.OpenAsync();
        const string query = """
            SELECT s.name, o.name, c.name,
                   CASE WHEN o.type = 'V' THEN 'View' ELSE 'Table' END,
                   ty.name, c.is_nullable, c.is_identity,
                   CAST(CASE WHEN pk.column_id IS NULL THEN 0 ELSE 1 END AS bit)
            FROM sys.objects o
            JOIN sys.schemas s ON o.schema_id = s.schema_id
            JOIN sys.columns c ON o.object_id = c.object_id
            JOIN sys.types ty ON c.user_type_id = ty.user_type_id
            LEFT JOIN (
                SELECT ic.object_id, ic.column_id
                FROM sys.index_columns ic
                JOIN sys.indexes i ON i.object_id=ic.object_id AND i.index_id=ic.index_id
                WHERE i.is_primary_key=1
            ) pk ON pk.object_id=c.object_id AND pk.column_id=c.column_id
            WHERE o.type IN ('U','V')
            ORDER BY s.name, o.name, c.column_id;
            """;
        await using var command = new SqlCommand(query, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var fullName = $"{reader.GetString(0)}.{reader.GetString(1)}";
            var shortName = reader.GetString(1);
            var column = reader.GetString(2);
            var info = new ColumnInfo(reader.GetString(4), reader.GetBoolean(5),
                reader.GetBoolean(6), reader.GetBoolean(7));
            AddMetadata(fullName, column, reader.GetString(3), info, columns, types, details);
            AddMetadata(shortName, column, reader.GetString(3), info, columns, types, details);
        }
        return (columns, types, details);
    }

    private static void AddMetadata(string objectName, string column, string type, ColumnInfo info,
        Dictionary<string, List<string>> columns, Dictionary<string, string> types,
        Dictionary<string, Dictionary<string, ColumnInfo>> details)
    {
        types[objectName] = type;
        if (!columns.TryGetValue(objectName, out var list))
            columns[objectName] = list = [];
        if (!list.Contains(column, StringComparer.OrdinalIgnoreCase)) list.Add(column);
        if (!details.TryGetValue(objectName, out var map))
            details[objectName] = map = new(StringComparer.OrdinalIgnoreCase);
        map[column] = info;
    }

    private sealed record ColumnInfo(
        string DataType, bool IsNullable, bool IsIdentity, bool IsPrimaryKey);
}

public sealed record QueryStatusEventArgs(
    string Message, string Time, int Rows, int Columns);
public sealed record ObjectDefinitionRequestEventArgs(
    string ObjectName, string ObjectType);

internal sealed class SqlResultValueConverter : Avalonia.Data.Converters.IValueConverter
{
    public static readonly SqlResultValueConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter,
        System.Globalization.CultureInfo culture) => value switch
    {
        null or DBNull => "NULL",
        bool boolean => boolean ? "1" : "0",
        DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss.fff"),
        byte[] bytes => $"0x{System.Convert.ToHexString(bytes)}",
        _ => value.ToString() ?? ""
    };

    public object ConvertBack(object? value, Type targetType, object? parameter,
        System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}
