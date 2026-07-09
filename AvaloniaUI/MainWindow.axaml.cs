using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using Microsoft.Data.SqlClient;

namespace SSMS;

public partial class MainWindow : Window
{
    private string _connectionString;
    private int _queryNumber = 1;
    private bool _changingDatabase;

    private TabItem? _draggedTab;
    private Point _dragStartPoint;
    private double _draggedTabGrabOffsetX;
    private int _lastTargetIndex = -1;

    private readonly Dictionary<string, string> _folderFilters = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<ExplorerNode> ExplorerRoots { get; } = [];
    public ICommand ExecuteCommand { get; }
    public ICommand NewQueryCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand SaveCommand { get; }

    public MainWindow() : this("") { }

    public MainWindow(string connectionString)
    {
        _connectionString = connectionString;
        ExecuteCommand = new ActionCommand(() => _ = ExecuteActiveAsync());
        NewQueryCommand = new ActionCommand(() => CreateQueryTab());
        OpenCommand = new ActionCommand(() => _ = OpenSqlAsync());
        SaveCommand = new ActionCommand(() => _ = SaveSqlAsync());
        InitializeComponent();
        DataContext = this;
        ExplorerTree.AddHandler(TreeViewItem.ExpandedEvent, ExplorerNode_OnExpanded);
        Opened += async (_, _) => await InitializeServerAsync();
        
        // Setup Window drag & drop for SQL files
        AddHandler(DragDrop.DragOverEvent, Window_DragOver);
        AddHandler(DragDrop.DropEvent, Window_Drop);
    }

    private QueryTabControl? ActiveQuery =>
        QueryTabs is not null &&
        QueryTabs.SelectedItem is TabItem { Tag: QueryTabControl query } ? query : null;

    private async Task InitializeServerAsync()
    {
        try
        {
            StatusText.Text = "Loading databases...";
            var databases = await DatabaseHelper.GetDatabasesAsync(_connectionString);
            _changingDatabase = true;
            DatabaseBox.ItemsSource = databases;
            DatabaseBox.SelectedItem = databases.Contains("master") ? "master" : databases.FirstOrDefault();
            _changingDatabase = false;
            await LoadExplorerAsync(databases);
            CreateQueryTab(databaseName: DatabaseBox.SelectedItem as string ?? "master");
            StatusText.Text = "Ready";
        }
        catch (Exception ex)
        {
            AppLogger.Error(ex, "Failed to initialize server.");
            await DialogService.ShowAsync(this, "Connection Error", ex.Message);
        }
    }

    private Task LoadExplorerAsync(IEnumerable<string> databases)
    {
        ExplorerRoots.Clear();
        var server = new ExplorerNode { Name = "🖥️ " + GetServerName(), Kind = "Server", IsLoaded = true, ConnectionString = _connectionString };
        server.NodeContextMenu = CreateObjectContextMenu(server);

        var databasesFolder = new ExplorerNode
        {
            Name = "📁 Databases", Kind = "DatabasesFolder", IsLoaded = true, Database = "master", ConnectionString = _connectionString
        };
        databasesFolder.NodeContextMenu = CreateObjectContextMenu(databasesFolder);

        foreach (var database in databases)
        {
            var node = new ExplorerNode
            {
                Name = "🛢️ " + database, Database = database, ObjectName = database, Kind = "Database", ConnectionString = _connectionString
            };
            node.Children.Add(new ExplorerNode { Name = "Loading...", Kind = "Placeholder" });
            node.NodeContextMenu = CreateObjectContextMenu(node);
            databasesFolder.Children.Add(node);
        }
        server.Children.Add(databasesFolder);
        ExplorerRoots.Add(server);
        return Task.CompletedTask;
    }

    private string GetServerName()
    {
        try { return new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(_connectionString).DataSource; }
        catch { return "SQL Server"; }
    }

    private Task LoadDatabaseNodeAsync(ExplorerNode database)
    {
        if (database.IsLoaded) return Task.CompletedTask;
        database.Children.Clear();
        database.Children.Add(new ExplorerNode { Name = "Loading...", Kind = "Placeholder" });
        try
        {
            database.Children.Clear();

            var tablesFolder = new ExplorerNode { Name = "📁 Tables", Kind = "TablesFolder", Database = database.Database, ConnectionString = _connectionString };
            tablesFolder.Children.Add(new ExplorerNode { Name = "Loading...", Kind = "Placeholder" });
            tablesFolder.NodeContextMenu = CreateFolderContextMenu(tablesFolder, "TablesFolder", "Tables");
            database.Children.Add(tablesFolder);

            var viewsFolder = new ExplorerNode { Name = "📁 Views", Kind = "ViewsFolder", Database = database.Database, ConnectionString = _connectionString };
            viewsFolder.Children.Add(new ExplorerNode { Name = "Loading...", Kind = "Placeholder" });
            viewsFolder.NodeContextMenu = CreateFolderContextMenu(viewsFolder, "ViewsFolder", "Views");
            database.Children.Add(viewsFolder);

            var spsFolder = new ExplorerNode { Name = "📁 Stored Procedures", Kind = "SpsFolder", Database = database.Database, ConnectionString = _connectionString };
            spsFolder.Children.Add(new ExplorerNode { Name = "Loading...", Kind = "Placeholder" });
            spsFolder.NodeContextMenu = CreateFolderContextMenu(spsFolder, "SpsFolder", "Stored Procedures");
            database.Children.Add(spsFolder);

            var funcsFolder = new ExplorerNode { Name = "📁 Functions", Kind = "FuncsFolder", Database = database.Database, ConnectionString = _connectionString };
            funcsFolder.Children.Add(new ExplorerNode { Name = "Loading...", Kind = "Placeholder" });
            funcsFolder.NodeContextMenu = CreateFolderContextMenu(funcsFolder, "FuncsFolder", "Functions");
            database.Children.Add(funcsFolder);

            database.IsLoaded = true;
        }
        catch (Exception ex)
        {
            database.Children.Clear();
            database.Children.Add(new ExplorerNode { Name = $"Error: {ex.Message}", Kind = "Error" });
            AppLogger.Error(ex, $"Failed to load database {database.Database}.");
        }
        return Task.CompletedTask;
    }

    private async Task LoadTableOrViewNodeAsync(ExplorerNode node)
    {
        if (node.IsLoaded) return;
        node.Children.Clear();
        node.Children.Add(new ExplorerNode { Name = "Loading...", Kind = "Placeholder" });
        try
        {
            var connStr = _connectionString;
            var dbName = node.Database;
            var objName = node.ObjectName;

            node.Children.Clear();

            // Columns Folder
            var colsFolder = new ExplorerNode { Name = "📁 Columns", Kind = "ColumnsFolder", Database = dbName, ObjectName = objName };
            var columns = await DatabaseHelper.GetColumnsAsync(connStr, dbName, objName);
            foreach (var col in columns)
            {
                string pkSuffix = col.IsPrimaryKey ? ", PK" : "";
                string icon = col.IsPrimaryKey ? "🔑" : "🔹";
                colsFolder.Children.Add(new ExplorerNode
                {
                    Name = $"{icon} {col.ColumnName} ({col.DataType}{pkSuffix})",
                    Kind = "Column",
                    Database = dbName,
                    ObjectName = col.ColumnName,
                    IsPrimaryKey = col.IsPrimaryKey,
                    DataType = col.DataType
                });
            }
            node.Children.Add(colsFolder);

            if (node.Kind == "Table")
            {
                // Indexes Folder
                var indexesFolder = new ExplorerNode { Name = "📁 Indexes", Kind = "IndexesFolder", Database = dbName, ObjectName = objName };
                var indexes = await DatabaseHelper.GetIndexesAsync(connStr, dbName, objName);
                foreach (var idx in indexes)
                {
                    string uniqueSuffix = idx.IsUnique ? ", Unique" : "";
                    string pkSuffix = idx.IsPrimaryKey ? ", PK" : "";
                    indexesFolder.Children.Add(new ExplorerNode
                    {
                        Name = $"🔖 {idx.IndexName} ({idx.IndexType}{uniqueSuffix}{pkSuffix})",
                        Kind = "Index",
                        Database = dbName,
                        ObjectName = idx.IndexName
                    });
                }
                node.Children.Add(indexesFolder);

                // Triggers Folder
                var triggersFolder = new ExplorerNode { Name = "📁 Triggers", Kind = "TriggersFolder", Database = dbName, ObjectName = objName };
                var triggers = await DatabaseHelper.GetTriggersAsync(connStr, dbName, objName);
                foreach (var trig in triggers)
                {
                    string disabledSuffix = trig.IsDisabled ? " (Disabled)" : "";
                    triggersFolder.Children.Add(new ExplorerNode
                    {
                        Name = $"⚡ {trig.TriggerName}{disabledSuffix}",
                        Kind = "Trigger",
                        Database = dbName,
                        ObjectName = trig.TriggerName
                    });
                }
                node.Children.Add(triggersFolder);
            }

            node.IsLoaded = true;
        }
        catch (Exception ex)
        {
            node.Children.Clear();
            node.Children.Add(new ExplorerNode { Name = $"Error: {ex.Message}", Kind = "Error" });
            AppLogger.Error(ex, $"Failed to load columns/indexes/triggers for table/view {node.ObjectName}.");
        }
    }

    private void CreateQueryTab(string? sql = null, string? title = null,
        string? databaseName = null, string? filePath = null, bool autoExecute = false)
    {
        var database = databaseName ?? DatabaseBox.SelectedItem as string ?? "master";
        var query = new QueryTabControl(_connectionString, database, sql, autoExecute) { FilePath = filePath };
        
        TabItem? tab = null;

        query.StatusChanged += (_, e) =>
        {
            if (QueryTabs.SelectedItem == tab)
            {
                StatusText.Text = e.Message;
                TimeText.Text = e.Time;
                RowsText.Text = e.Rows > 0 || e.Columns > 0 ? $"{e.Rows} rows · {e.Columns} cols" : "";
            }
        };
        
        query.ObjectDefinitionRequested += async (_, e) =>
            await OpenObjectDefinitionAsync(database, e.ObjectType, e.ObjectName);

        // Build tab header panel with close button
        var headerPanel = new StackPanel { Orientation = Orientation.Horizontal, Tag = "QueryTabDragHandle" };
        var headerText = new TextBlock 
        { 
            Text = title ?? $"SQLQuery{_queryNumber++}.sql", 
            VerticalAlignment = VerticalAlignment.Center, 
            Margin = new Thickness(0, 0, 6, 0),
            FontSize = 11,
            FontFamily = new FontFamily("Segoe UI, Inter")
        };

        var closeBtn = new Button 
        { 
            Content = "✕", 
            Cursor = new Cursor(StandardCursorType.Hand),
            FontSize = 7,
            VerticalAlignment = VerticalAlignment.Center
        };
        closeBtn.Classes.Add("tab-close-btn");
        closeBtn.Click += (s, e) =>
        {
            if (tab != null) CloseTab(tab);
            e.Handled = true;
        };

        headerPanel.Children.Add(headerText);
        headerPanel.Children.Add(closeBtn);

        tab = new TabItem
        {
            Header = headerPanel,
            Tag = query
        };
        tab.ContextMenu = CreateTabContextMenu(tab);

        // Attach pointer events for tab dragging reorder
        tab.PointerPressed += TabItem_PointerPressed;
        tab.PointerMoved += TabItem_PointerMoved;
        tab.PointerReleased += TabItem_PointerReleased;

        QueryTabContentContainer.Children.Add(query);
        QueryTabs.Items.Add(tab);
        QueryTabs.SelectedItem = tab;
    }

    private async Task OpenObjectDefinitionAsync(string database, string kind, string objectName)
    {
        try
        {
            StatusText.Text = $"Scripting {objectName}...";
            var sql = kind.Equals("Table", StringComparison.OrdinalIgnoreCase)
                ? await DatabaseHelper.GenerateTableCreateScriptAsync(_connectionString, database, objectName)
                : await DatabaseHelper.GetObjectDefinitionAsync(_connectionString, database, objectName);
            CreateQueryTab(sql, objectName, database);
            StatusText.Text = "Ready";
        }
        catch (Exception ex)
        {
            await DialogService.ShowAsync(this, "Script Object", ex.Message);
        }
    }

    private async Task ExecuteActiveAsync()
    {
        if (ActiveQuery is { } query) await query.ExecuteQueryAsync();
    }

    private async Task OpenSqlAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open SQL File",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("SQL files") { Patterns = ["*.sql"] },
                FilePickerFileTypes.All
            ]
        });
        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (path is null) continue;
            CreateQueryTab(await File.ReadAllTextAsync(path), Path.GetFileName(path),
                filePath: path);
        }
    }

    private async Task SaveSqlAsync(bool saveAs = false)
    {
        if (ActiveQuery is not { } query) return;
        var path = saveAs ? null : query.FilePath;
        if (path is null)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save SQL File",
                SuggestedFileName = QueryTabs.SelectedItem is TabItem tab
                    ? GetTabHeaderText(tab) : "query.sql",
                DefaultExtension = "sql",
                FileTypeChoices = [new FilePickerFileType("SQL files") { Patterns = ["*.sql"] }]
            });
            path = file?.TryGetLocalPath();
        }
        if (path is null) return;
        await File.WriteAllTextAsync(path, await query.GetQueryTextAsync());
        query.FilePath = path;
        if (QueryTabs.SelectedItem is TabItem current)
        {
            if (current.Header is Panel p && p.Children.Count > 0 && p.Children[0] is TextBlock tb)
                tb.Text = Path.GetFileName(path);
        }
        StatusText.Text = $"Saved {path}";
    }

    private string GetTabHeaderText(TabItem tabItem)
    {
        if (tabItem.Header is Panel p && p.Children.Count > 0 && p.Children[0] is TextBlock tb)
            return tb.Text ?? "";
        return tabItem.Header?.ToString() ?? "(untitled)";
    }

    private async void ExplorerTree_OnDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (ExplorerTree.SelectedItem is not ExplorerNode node) return;
        if (node.Kind == "Database")
        {
            await LoadDatabaseNodeAsync(node);
        }
        else if (node.Kind is "Table" or "View" or "StoredProcedure" or "Function")
        {
            await OpenObjectDefinitionAsync(node.Database, node.Kind, node.ObjectName);
        }
    }

    private async void ExplorerNode_OnExpanded(object? sender, RoutedEventArgs e)
    {
        if (e.Source is TreeViewItem { DataContext: ExplorerNode node })
        {
            if (node.Kind == "Database")
            {
                await LoadDatabaseNodeAsync(node);
            }
            else if (node.Kind is "Table" or "View")
            {
                await LoadTableOrViewNodeAsync(node);
            }
            else if (node.Kind == "FuncsFolder")
            {
                PopulateFunctionsFolder(node);
            }
            else if (node.Kind is "TablesFolder" or "ViewsFolder" or "SpsFolder" or "ScalarFunctionsFolder" or "TableFunctionsFolder")
            {
                await ReloadFolderNodeAsync(node, node.Kind);
            }
        }
    }

    private void PopulateFunctionsFolder(ExplorerNode funcsFolder)
    {
        if (funcsFolder.Children.Count == 1 && funcsFolder.Children[0].Kind == "Placeholder")
        {
            funcsFolder.Children.Clear();
            var scalarFolder = new ExplorerNode { Name = "📁 Scalar-valued Functions", Kind = "ScalarFunctionsFolder", Database = funcsFolder.Database, ConnectionString = _connectionString };
            scalarFolder.Children.Add(new ExplorerNode { Name = "Loading...", Kind = "Placeholder" });
            scalarFolder.NodeContextMenu = CreateFolderContextMenu(scalarFolder, "ScalarFunctionsFolder", "Scalar-valued Functions");
            funcsFolder.Children.Add(scalarFolder);

            var tableFolder = new ExplorerNode { Name = "📁 Table-valued Functions", Kind = "TableFunctionsFolder", Database = funcsFolder.Database, ConnectionString = _connectionString };
            tableFolder.Children.Add(new ExplorerNode { Name = "Loading...", Kind = "Placeholder" });
            tableFolder.NodeContextMenu = CreateFolderContextMenu(tableFolder, "TableFunctionsFolder", "Table-valued Functions");
            funcsFolder.Children.Add(tableFolder);
        }
    }

    private async void DatabaseBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_changingDatabase || DatabaseBox.SelectedItem is not string database) return;
        if (ActiveQuery is { } query) await query.ChangeDatabaseAsync(database);
    }

    private void QueryTabs_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (QueryTabs is null || DatabaseBox is null || QueryTabContentContainer is null) return;
        if (e.Source != QueryTabs) return;

        var active = ActiveQuery;
        foreach (var child in QueryTabContentContainer.Children)
        {
            if (child is QueryTabControl queryCtrl)
            {
                queryCtrl.IsVisible = (queryCtrl == active);
            }
        }

        if (active is not { } query)
        {
            DatabaseBox.SelectedItem = null;
            StatusText.Text = "Ready";
            TimeText.Text = "";
            RowsText.Text = "";
            return;
        }
        _changingDatabase = true;
        DatabaseBox.SelectedItem = query.DatabaseName;
        _changingDatabase = false;

        // Sync status bar
        if (query.LastStatus is { } status)
        {
            StatusText.Text = status.Message;
            TimeText.Text = status.Time;
            RowsText.Text = status.Rows > 0 || status.Columns > 0 ? $"{status.Rows} rows · {status.Columns} cols" : "";
        }
        else
        {
            StatusText.Text = "Ready";
            TimeText.Text = "";
            RowsText.Text = "";
        }
    }

    private async void RefreshExplorer_OnClick(object? sender, RoutedEventArgs e)
    {
        var databases = await DatabaseHelper.GetDatabasesAsync(_connectionString);
        await LoadExplorerAsync(databases);
    }

    private void NewQuery_OnClick(object? sender, RoutedEventArgs e) => CreateQueryTab();
    private async void Execute_OnClick(object? sender, RoutedEventArgs e) => await ExecuteActiveAsync();
    private async void Open_OnClick(object? sender, RoutedEventArgs e) => await OpenSqlAsync();
    private async void Save_OnClick(object? sender, RoutedEventArgs e) => await SaveSqlAsync();
    
    private async void Comment_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ActiveQuery is { } query) await query.RunEditorCommandAsync("commentSelection();");
    }
    
    private async void Uncomment_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ActiveQuery is { } query) await query.RunEditorCommandAsync("uncommentSelection();");
    }
    
    private async void Settings_OnClick(object? sender, RoutedEventArgs e) =>
        await new SettingsWindow().ShowDialog<bool>(this);

    private void InsertScript_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn)
        {
            var menu = new ContextMenu();

            var insertDataItem = new MenuItem { Header = "Generate INSERT Script with Data (Table)..." };
            insertDataItem.Click += async (s, ev) =>
            {
                if (ExplorerTree.SelectedItem is not ExplorerNode { Kind: "Table" } table)
                {
                    await DialogService.ShowAsync(this, "INSERT Script",
                        "Select a table in Object Explorer first.");
                    return;
                }
                var dialog = new InsertWithDataWindow(_connectionString, table.Database, table.ObjectName);
                var sql = await dialog.ShowDialog<string?>(this);
                if (!string.IsNullOrWhiteSpace(sql))
                    CreateQueryTab(sql, $"INSERT {table.ObjectName}", table.Database);
            };
            menu.Items.Add(insertDataItem);
            menu.Items.Add(new Separator());

            var scopeIdentityItem = new MenuItem { Header = "Scope Identity (DECLARE @NewID = SCOPE_IDENTITY())" };
            scopeIdentityItem.Click += async (s, ev) =>
            {
                if (ActiveQuery is { } query)
                    await query.InsertTextAsync("DECLARE @NewID BIGINT = SCOPE_IDENTITY();");
            };
            menu.Items.Add(scopeIdentityItem);

            var getDateItem = new MenuItem { Header = "Get Date ('YYYY-MM-DD HH:mm:ss')" };
            getDateItem.Click += async (s, ev) =>
            {
                if (ActiveQuery is { } query)
                {
                    string dateStr = $"'{DateTime.Now:yyyy-MM-dd HH:mm:ss}'";
                    await query.InsertTextAsync(dateStr);
                }
            };
            menu.Items.Add(getDateItem);

            menu.Open(btn);
        }
    }

    private void CloseTab_OnClick(object? sender, RoutedEventArgs e)
    {
        if (QueryTabs.SelectedItem is TabItem item) CloseTab(item);
    }

    private void Exit_OnClick(object? sender, RoutedEventArgs e) => Close();

    private async void Reconnect_OnClick(object? sender, RoutedEventArgs e)
    {
        var window = new ConnectionWindow();
        window.Show();
        var connectionString = await window.WaitForResultAsync();
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        _connectionString = connectionString;
        QueryTabs.Items.Clear();
        await InitializeServerAsync();
    }

    #region Context Menus

    private ContextMenu CreateObjectContextMenu(ExplorerNode node)
    {
        var menu = new ContextMenu();

        var copyNameItem = new MenuItem { Header = "Copy Name" };
        copyNameItem.Click += async (_, _) =>
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(node.ObjectName);
            }
        };
        menu.Items.Add(copyNameItem);
        menu.Items.Add(new Separator());

        if (node.Kind is "Table" or "View" or "StoredProcedure" or "Function")
        {
            var newQueryItem = new MenuItem { Header = "New Query" };
            newQueryItem.Click += (s, e) => CreateQueryTab(databaseName: node.Database);
            menu.Items.Add(newQueryItem);

            if (node.Kind is "Table" or "View")
            {
                var selectTopItem = new MenuItem { Header = "Select Top 200" };
                selectTopItem.Click += (s, e) =>
                {
                    string safeName = node.ObjectName;
                    var parts = node.ObjectName.Split('.');
                    safeName = parts.Length == 2 ? $"[{parts[0]}].[{parts[1]}]" : $"[{node.ObjectName}]";
                    string sql = $"SELECT TOP 200 * FROM {safeName};";
                    CreateQueryTab(sql, $"{node.ObjectName} (Top 200)", node.Database, autoExecute: true);
                };
                menu.Items.Add(selectTopItem);
            }

            var scriptAsMenu = new MenuItem { Header = "Script Object as" };

            // CREATE To
            var createToMenu = new MenuItem { Header = "CREATE To" };
            var createNewQuery = new MenuItem { Header = "New Query Editor Window" };
            createNewQuery.Click += async (s, e) => await GenerateScriptObjectAsync(node, "CREATE");
            createToMenu.Items.Add(createNewQuery);
            scriptAsMenu.Items.Add(createToMenu);

            if (node.Kind == "Table")
            {
                var insertToMenu = new MenuItem { Header = "INSERT To" };
                
                var insertNewQuery = new MenuItem { Header = "New Query Editor Window (Standard)" };
                insertNewQuery.Click += async (s, e) => await GenerateScriptObjectAsync(node, "INSERT");
                insertToMenu.Items.Add(insertNewQuery);

                var insertVarsNewQuery = new MenuItem { Header = "New Query Editor Window (with Variables)" };
                insertVarsNewQuery.Click += async (s, e) => await GenerateScriptObjectAsync(node, "INSERT_VARS");
                insertToMenu.Items.Add(insertVarsNewQuery);

                var insertDataNewQuery = new MenuItem { Header = "New Query Editor Window (with Data)" };
                insertDataNewQuery.Click += (s, e) => ShowInsertWithDataDialog(node);
                insertToMenu.Items.Add(insertDataNewQuery);

                scriptAsMenu.Items.Add(insertToMenu);
            }

            var alterToMenu = new MenuItem { Header = "ALTER To" };
            var alterNewQuery = new MenuItem { Header = "New Query Editor Window" };
            alterNewQuery.Click += async (s, e) => await GenerateScriptObjectAsync(node, "ALTER");
            alterToMenu.Items.Add(alterNewQuery);
            scriptAsMenu.Items.Add(alterToMenu);

            var dropToMenu = new MenuItem { Header = "DROP To" };
            var dropNewQuery = new MenuItem { Header = "New Query Editor Window" };
            dropNewQuery.Click += async (s, e) => await GenerateScriptObjectAsync(node, "DROP");
            dropToMenu.Items.Add(dropNewQuery);
            scriptAsMenu.Items.Add(dropToMenu);

            menu.Items.Add(scriptAsMenu);
        }

        return menu;
    }

    private void ShowInsertWithDataDialog(ExplorerNode node)
    {
        var dialog = new InsertWithDataWindow(_connectionString, node.Database, node.ObjectName);
        _ = ShowInsertWithDataDialogAsync(dialog, node.ObjectName, node.Database);
    }

    private async Task ShowInsertWithDataDialogAsync(InsertWithDataWindow dialog, string tableName, string database)
    {
        var sql = await dialog.ShowDialog<string?>(this);
        if (!string.IsNullOrWhiteSpace(sql))
        {
            CreateQueryTab(sql, $"{tableName}_InsertData.sql", database);
        }
    }

    private async Task GenerateScriptObjectAsync(ExplorerNode node, string scriptType)
    {
        string sql = "";
        try
        {
            var connStr = _connectionString;
            var dbName = node.Database;
            var objName = node.ObjectName;
            var objType = node.Kind;

            if (scriptType == "DROP")
            {
                string dropKeyword = objType;
                if (objType == "StoredProcedure") dropKeyword = "PROCEDURE";
                sql = $"DROP {dropKeyword.ToUpper()} {objName};";
            }
            else if (objType == "Table")
            {
                if (scriptType == "CREATE")
                {
                    sql = await DatabaseHelper.GenerateTableCreateScriptAsync(connStr, dbName, objName);
                }
                else if (scriptType == "INSERT" || scriptType == "INSERT_VARS")
                {
                    var columns = await DatabaseHelper.GetColumnsAsync(connStr, dbName, objName);
                    var sb = new System.Text.StringBuilder();
                    string safeName = objName;
                    var parts = objName.Split('.');
                    safeName = parts.Length == 2 ? $"[{parts[0]}].[{parts[1]}]" : $"[{objName}]";

                    if (scriptType == "INSERT_VARS")
                    {
                        foreach (var col in columns)
                        {
                            string varName = col.ColumnName.Replace(" ", "_");
                            varName = new string(varName.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
                            sb.AppendLine($"DECLARE @{varName} {col.DataType} = NULL;");
                        }
                        sb.AppendLine();
                    }

                    sb.AppendLine($"INSERT INTO {safeName}");
                    sb.AppendLine("(");
                    for (int i = 0; i < columns.Count; i++)
                    {
                        sb.Append($"    [{columns[i].ColumnName}]");
                        if (i < columns.Count - 1) sb.AppendLine(",");
                        else sb.AppendLine();
                    }
                    sb.AppendLine(")");
                    sb.AppendLine("VALUES");
                    sb.AppendLine("(");
                    for (int i = 0; i < columns.Count; i++)
                    {
                        if (scriptType == "INSERT_VARS")
                        {
                            string varName = columns[i].ColumnName.Replace(" ", "_");
                            varName = new string(varName.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
                            sb.Append($"    @{varName}");
                        }
                        else
                        {
                            sb.Append($"    NULL /* {columns[i].ColumnName} ({columns[i].DataType}) */");
                        }

                        if (i < columns.Count - 1) sb.AppendLine(",");
                        else sb.AppendLine();
                    }
                    sb.AppendLine(");");
                    sql = sb.ToString();
                }
                else // ALTER Table
                {
                    sql = $"-- Alter Table Script for {objName}\n-- ALTER TABLE {objName} ADD [NewColumnName] DataType;";
                }
            }
            else // View, StoredProcedure, Function
            {
                string def = await DatabaseHelper.GetObjectDefinitionAsync(connStr, dbName, objName);
                if (string.IsNullOrEmpty(def))
                {
                    sql = $"-- Could not retrieve definition for {objName}.";
                }
                else
                {
                    if (scriptType == "CREATE")
                    {
                        sql = def;
                    }
                    else // ALTER
                    {
                        int idx = def.IndexOf("CREATE", StringComparison.OrdinalIgnoreCase);
                        if (idx >= 0)
                        {
                            sql = def.Substring(0, idx) + "ALTER" + def.Substring(idx + 6);
                        }
                        else
                        {
                            sql = def;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            sql = $"-- Error generating script: {ex.Message}";
        }

        CreateQueryTab(sql, $"{node.ObjectName}_{scriptType}.sql", node.Database);
    }

    private ContextMenu CreateFolderContextMenu(ExplorerNode folder, string folderType, string baseName)
    {
        var menu = new ContextMenu();

        if (TryGetCreateObjectLabel(folderType, out string createLabel))
        {
            var createItem = new MenuItem { Header = $"Create New {createLabel}" };
            createItem.Click += (s, e) => CreateNewObjectScript(folder.Database, folderType);
            menu.Items.Add(createItem);
            menu.Items.Add(new Separator());
        }

        var newQueryItem = new MenuItem { Header = "New Query" };
        newQueryItem.Click += (s, e) => CreateQueryTab(databaseName: folder.Database);
        menu.Items.Add(newQueryItem);
        
        var filterItem = new MenuItem { Header = "Filter..." };
        filterItem.Click += (s, e) => OpenFilterDialog(folder, folderType, baseName);
        menu.Items.Add(filterItem);

        var clearFilterItem = new MenuItem { Header = "Clear Filter" };
        clearFilterItem.Click += (s, e) => ClearFilter(folder, folderType, baseName);
        menu.Items.Add(clearFilterItem);

        return menu;
    }

    private static bool TryGetCreateObjectLabel(string folderType, out string label)
    {
        label = folderType switch
        {
            "TablesFolder" => "Table",
            "ViewsFolder" => "View",
            "SpsFolder" => "Stored Procedure",
            "ScalarFunctionsFolder" => "Scalar-valued Function",
            "TableFunctionsFolder" => "Table-valued Function",
            _ => string.Empty
        };
        return label.Length > 0;
    }

    private void CreateNewObjectScript(string databaseName, string folderType)
    {
        string sql = folderType switch
        {
            "TablesFolder" => "CREATE TABLE [dbo].[NewTable]\n(\n    [Id] INT NOT NULL PRIMARY KEY\n);",
            "ViewsFolder" => "CREATE VIEW [dbo].[NewView]\nAS\nSELECT\n    1 AS [Value];",
            "SpsFolder" => "CREATE PROCEDURE [dbo].[NewProcedure]\nAS\nBEGIN\n    SET NOCOUNT ON;\n\n    SELECT 1 AS [Value];\nEND;",
            "ScalarFunctionsFolder" => "CREATE FUNCTION [dbo].[NewScalarFunction]\n(\n    @Value INT\n)\nRETURNS INT\nAS\nBEGIN\n    RETURN @Value;\nEND;",
            "TableFunctionsFolder" => "CREATE FUNCTION [dbo].[NewTableFunction]\n(\n    @Value INT\n)\nRETURNS TABLE\nAS\nRETURN\n(\n    SELECT @Value AS [Value]\n);",
            _ => string.Empty
        };

        if (!string.IsNullOrEmpty(sql))
        {
            CreateQueryTab(sql, $"Create {folderType.Replace("Folder", "")}.sql", databaseName);
        }
    }

    private string GetFolderFilter(string dbName, string folderType)
    {
        string key = $"{dbName}_{folderType}";
        return _folderFilters.TryGetValue(key, out var val) ? val : string.Empty;
    }

    private string GetFolderHeader(string dbName, string folderType, string baseName)
    {
        string filter = GetFolderFilter(dbName, folderType);
        return string.IsNullOrEmpty(filter) ? $"📁 {baseName}" : $"📁 {baseName} (filtered: '{filter}')";
    }

    private async void OpenFilterDialog(ExplorerNode folderItem, string folderType, string baseName)
    {
        string key = $"{folderItem.Database}_{folderType}";
        string currentFilter = GetFolderFilter(folderItem.Database, folderType);
        var dialog = new InputDialog("Filter Objects", "Enter filter query (wildcard/substring):", currentFilter);
        string filterText = await dialog.ShowInputAsync(this);

        if (filterText != currentFilter)
        {
            if (string.IsNullOrEmpty(filterText))
            {
                _folderFilters.Remove(key);
            }
            else
            {
                _folderFilters[key] = filterText;
            }

            folderItem.Name = GetFolderHeader(folderItem.Database, folderType, baseName);
            await ReloadFolderNodeAsync(folderItem, folderType);
        }
    }

    private async void ClearFilter(ExplorerNode folderItem, string folderType, string baseName)
    {
        string key = $"{folderItem.Database}_{folderType}";
        if (_folderFilters.ContainsKey(key))
        {
            _folderFilters.Remove(key);
            folderItem.Name = GetFolderHeader(folderItem.Database, folderType, baseName);
            await ReloadFolderNodeAsync(folderItem, folderType);
        }
    }

    private async Task ReloadFolderNodeAsync(ExplorerNode folderNode, string folderType)
    {
        folderNode.Children.Clear();
        folderNode.Children.Add(new ExplorerNode { Name = "Loading...", Kind = "Placeholder" });
        
        try
        {
            var connStr = _connectionString;
            var dbName = folderNode.Database;
            string filter = GetFolderFilter(dbName, folderType);
            
            IEnumerable<string> items;
            string childKind;

            if (folderType == "TablesFolder")
            {
                items = await DatabaseHelper.GetTablesAsync(connStr, dbName);
                childKind = "Table";
            }
            else if (folderType == "ViewsFolder")
            {
                items = await DatabaseHelper.GetViewsAsync(connStr, dbName);
                childKind = "View";
            }
            else if (folderType == "SpsFolder")
            {
                items = await DatabaseHelper.GetStoredProceduresAsync(connStr, dbName);
                childKind = "StoredProcedure";
            }
            else if (folderType == "ScalarFunctionsFolder")
            {
                items = await DatabaseHelper.GetFunctionsAsync(connStr, dbName, false);
                childKind = "Function";
            }
            else if (folderType == "TableFunctionsFolder")
            {
                items = await DatabaseHelper.GetFunctionsAsync(connStr, dbName, true);
                childKind = "Function";
            }
            else
            {
                items = Array.Empty<string>();
                childKind = "";
            }
            
            folderNode.Children.Clear();
            foreach (var item in items)
            {
                if (!string.IsNullOrEmpty(filter) && !item.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    continue;
                
                var child = new ExplorerNode
                {
                    Name = (childKind == "Table" ? "田 " : childKind == "View" ? "👓 " : childKind == "StoredProcedure" ? "⚡ " : "⚙️ ") + item,
                    ObjectName = item,
                    Database = dbName,
                    Kind = childKind,
                    IsLoaded = false
                };
                if (childKind is "Table" or "View")
                {
                    child.Children.Add(new ExplorerNode { Name = "Double-click to load...", Kind = "Placeholder" });
                }
                child.NodeContextMenu = CreateObjectContextMenu(child);
                folderNode.Children.Add(child);
            }
            folderNode.IsLoaded = true;
        }
        catch (Exception ex)
        {
            folderNode.Children.Clear();
            folderNode.Children.Add(new ExplorerNode { Name = $"Error: {ex.Message}", Kind = "Error" });
            AppLogger.Error(ex, $"Failed to reload folder {folderNode.Name}.");
        }
    }

    private ExplorerNode CreateFolder(string title, string folderType, string childKind, string database, IEnumerable<string> names)
    {
        var folder = new ExplorerNode
        {
            Name = title,
            Kind = folderType,
            Database = database,
            IsLoaded = true,
            ConnectionString = _connectionString
        };
        folder.NodeContextMenu = CreateFolderContextMenu(folder, folderType, title);

        string filter = GetFolderFilter(database, folderType);

        foreach (var name in names)
        {
            if (!string.IsNullOrEmpty(filter) && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;

            var child = new ExplorerNode
            {
                Name = (childKind == "Table" ? "田 " : childKind == "View" ? "👓 " : childKind == "StoredProcedure" ? "⚡ " : "⚙️ ") + name,
                ObjectName = name,
                Database = database,
                Kind = childKind,
                IsLoaded = false
            };
            if (childKind is "Table" or "View")
            {
                child.Children.Add(new ExplorerNode { Name = "Double-click to load...", Kind = "Placeholder" });
            }
            child.NodeContextMenu = CreateObjectContextMenu(child);
            folder.Children.Add(child);
        }
        return folder;
    }

    #endregion

    #region Tab Drag & Drop & Context Menus

    private ContextMenu CreateTabContextMenu(TabItem tabItem)
    {
        var menu = new ContextMenu();
        
        var renameItem = new MenuItem { Header = "Rename" };
        renameItem.Click += (s, e) => RenameTab(tabItem);
        menu.Items.Add(renameItem);

        var closeItem = new MenuItem { Header = "Close" };
        closeItem.Click += (s, e) => CloseTab(tabItem);
        menu.Items.Add(closeItem);

        var closeAllItem = new MenuItem { Header = "Close All" };
        closeAllItem.Click += (s, e) => CloseAllTabs();
        menu.Items.Add(closeAllItem);

        var closeAllButThisItem = new MenuItem { Header = "Close All But This" };
        closeAllButThisItem.Click += (s, e) => CloseAllTabsExcept(tabItem);
        menu.Items.Add(closeAllButThisItem);

        var setColorItem = new MenuItem { Header = "Set Color" };
        var colors = new[]
        {
            ("Red", "#6A1B1B"),
            ("Green", "#1B5E20"),
            ("Blue", "#0D47A1"),
            ("Yellow", "#8D6E63"),
            ("Orange", "#D84315"),
            ("Default (Dark)", "#252526")
        };
        foreach (var (name, hex) in colors)
        {
            var subItem = new MenuItem { Header = name };
            var brush = SolidColorBrush.Parse(hex);
            subItem.Click += (s, e) => tabItem.Background = brush;
            setColorItem.Items.Add(subItem);
        }
        menu.Items.Add(setColorItem);

        return menu;
    }

    private async void RenameTab(TabItem tabItem)
    {
        var currentName = GetTabHeaderText(tabItem);
        var dialog = new InputDialog("Rename Tab", "Enter new tab name:", currentName);
        var newName = await dialog.ShowInputAsync(this);
        if (!string.IsNullOrEmpty(newName))
        {
            if (tabItem.Header is Panel p && p.Children.Count > 0 && p.Children[0] is TextBlock tb)
            {
                tb.Text = newName;
            }
        }
    }

    private void CloseTab(TabItem tabItem)
    {
        if (tabItem.Tag is QueryTabControl query)
        {
            QueryTabContentContainer.Children.Remove(query);
        }
        QueryTabs.Items.Remove(tabItem);
    }

    private void CloseAllTabs()
    {
        QueryTabs.Items.Clear();
        QueryTabContentContainer.Children.Clear();
    }

    private void CloseAllTabsExcept(TabItem tabItem)
    {
        var list = QueryTabs.Items.Cast<TabItem>().ToList();
        foreach (var item in list)
        {
            if (item != tabItem)
            {
                if (item.Tag is QueryTabControl query)
                {
                    QueryTabContentContainer.Children.Remove(query);
                }
                QueryTabs.Items.Remove(item);
            }
        }
    }

    private void TabItem_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is TabItem tabItem)
        {
            var pt = e.GetCurrentPoint(tabItem);
            if (!pt.Properties.IsLeftButtonPressed) return;

            var visualSource = e.Source as Visual;
            if (!IsQueryTabDragHandle(visualSource)) return;

            _draggedTab = tabItem;
            _dragStartPoint = e.GetPosition(QueryTabs);
            _draggedTabGrabOffsetX = _dragStartPoint.X - GetLayoutPosition(tabItem, QueryTabs).X;
            tabItem.ZIndex = 1000;
            e.Pointer.Capture(tabItem);
            e.Handled = true;
        }
    }

    private void TabItem_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_draggedTab != null && sender == _draggedTab)
        {
            var currentPoint = e.GetPosition(QueryTabs);
            double horizontalDelta = Math.Abs(currentPoint.X - _dragStartPoint.X);

            if (horizontalDelta > 5)
            {
                MoveDraggedElementWithCursor(_draggedTab, QueryTabs, currentPoint, _draggedTabGrabOffsetX);

                var tabItems = QueryTabs.Items.OfType<TabItem>().Cast<Visual>().ToList();
                int sourceIndex = tabItems.IndexOf(_draggedTab);
                int targetIndex = GetReorderTargetIndex(tabItems, _draggedTab, currentPoint, sourceIndex, QueryTabs);

                UpdateTabVisualPositions(_draggedTab, sourceIndex, targetIndex);
            }
            e.Handled = true;
        }
    }

    private void TabItem_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_draggedTab != null && sender == _draggedTab)
        {
            CancelTabDrag(e, animateToSlot: true);
            e.Handled = true;
        }
    }

    private void CancelTabDrag(PointerReleasedEventArgs e, bool animateToSlot)
    {
        var tab = _draggedTab;
        _draggedTab = null;
        if (tab == null) return;

        e.Pointer.Capture(null);
        tab.ZIndex = 0;

        if (tab.RenderTransform is TranslateTransform transform)
        {
            transform.X = 0;
        }
        
        CommitTabDrag(tab);
    }

    private void CommitTabDrag(TabItem tab)
    {
        var tabItems = QueryTabs.Items.OfType<TabItem>().Cast<Visual>().ToList();
        int sourceIndex = tabItems.IndexOf(tab);
        if (sourceIndex < 0) return;
        
        int targetIndex = _lastTargetIndex >= 0 ? _lastTargetIndex : sourceIndex;
        
        // Clear all translations
        foreach (var item in QueryTabs.Items.OfType<TabItem>())
        {
            if (item.RenderTransform is TranslateTransform transform)
            {
                transform.X = 0;
            }
        }
        _lastTargetIndex = -1;

        if (sourceIndex != targetIndex && targetIndex >= 0 && targetIndex < QueryTabs.Items.Count)
        {
            QueryTabs.SelectionChanged -= QueryTabs_OnSelectionChanged;
            try
            {
                QueryTabs.Items.RemoveAt(sourceIndex);
                QueryTabs.Items.Insert(targetIndex, tab);
                QueryTabs.SelectedItem = tab;
            }
            finally
            {
                QueryTabs.SelectionChanged += QueryTabs_OnSelectionChanged;
            }
        }
    }

    private void UpdateTabVisualPositions(TabItem draggedTab, int sourceIndex, int targetIndex)
    {
        if (targetIndex == -1) targetIndex = sourceIndex;
        if (targetIndex == _lastTargetIndex) return;
        _lastTargetIndex = targetIndex;

        var tabItems = QueryTabs.Items.OfType<TabItem>().ToList();
        double draggedWidth = draggedTab.Bounds.Width;

        for (int i = 0; i < tabItems.Count; i++)
        {
            var tab = tabItems[i];
            if (tab == draggedTab) continue;

            double targetTranslationX = 0;
            if (sourceIndex < targetIndex)
            {
                if (i > sourceIndex && i <= targetIndex) targetTranslationX = -draggedWidth;
            }
            else if (sourceIndex > targetIndex)
            {
                if (i >= targetIndex && i < sourceIndex) targetTranslationX = draggedWidth;
            }

            var transform = GetOrCreateTranslateTransform(tab);
            transform.X = targetTranslationX;
        }
    }

    private Point GetLayoutPosition(Visual element, Visual relativeTo)
    {
        var point = element.TranslatePoint(new Point(0, 0), relativeTo) ?? new Point(0, 0);
        if (element.RenderTransform is TranslateTransform transform)
        {
            point = new Point(point.X - transform.X, point.Y - transform.Y);
        }
        return point;
    }

    private void MoveDraggedElementWithCursor(Visual element, Visual relativeTo, Point cursorPosition, double grabOffsetX)
    {
        var layoutPosition = GetLayoutPosition(element, relativeTo);
        var transform = GetOrCreateTranslateTransform(element);
        transform.X = cursorPosition.X - grabOffsetX - layoutPosition.X;
    }

    private TranslateTransform GetOrCreateTranslateTransform(Visual element)
    {
        if (element.RenderTransform is TranslateTransform transform) return transform;
        transform = new TranslateTransform();
        element.RenderTransform = transform;
        return transform;
    }

    private bool IsQueryTabDragHandle(Visual? source)
    {
        Visual? current = source;
        while (current != null)
        {
            if (current is Control control && Equals(control.Tag, "QueryTabDragHandle"))
            {
                return true;
            }
            if (current is TabItem) return false;
            current = current.GetVisualParent();
        }
        return false;
    }

    private int GetReorderTargetIndex(
        IReadOnlyList<Visual> elements,
        Visual draggedElement,
        Point cursorPosition,
        int sourceIndex,
        Visual relativeTo)
    {
        if (sourceIndex < 0) return -1;

        for (int i = 0; i < elements.Count; i++)
        {
            var element = elements[i];
            if (element == draggedElement || !element.IsVisible) continue;

            Point position = GetLayoutPosition(element, relativeTo);
            double left = position.X;
            double right = left + element.Bounds.Width;
            if (cursorPosition.X < left || cursorPosition.X > right) continue;

            double threshold = sourceIndex < i
                ? left + element.Bounds.Width * 0.55
                : left + element.Bounds.Width * 0.45;

            if ((sourceIndex < i && cursorPosition.X >= threshold) ||
                (sourceIndex > i && cursorPosition.X <= threshold))
            {
                return i;
            }
        }

        return -1;
    }

    #endregion

    #region Drag & Drop SQL Files to Window

    private void Window_DragOver(object? sender, DragEventArgs e)
    {
        var hasFiles = e.DataTransfer.Contains(DataFormat.File);
        if (hasFiles)
        {
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
            e.Handled = true;
        }
    }

    private async void Window_Drop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(DataFormat.File))
        {
            var files = e.DataTransfer.TryGetFiles();
            if (files != null)
            {
                foreach (var file in files)
                {
                    if (file.Name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                    {
                        var path = file.Path.LocalPath;
                        var sql = await File.ReadAllTextAsync(path);
                        CreateQueryTab(sql, file.Name, filePath: path);
                    }
                }
            }
        }
    }

    #endregion

    private sealed class ActionCommand(Action action) : ICommand
    {
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
}
