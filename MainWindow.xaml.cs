using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Data.SqlClient;

namespace SSMS
{
    public partial class MainWindow : Window
    {
        private readonly string _initialConnectionString;
        private int _queryTabCounter = 0;

        // Cache databases list per server connection string to make tab switching instant
        private readonly Dictionary<string, List<string>> _serverDatabasesCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _folderFilters = new(StringComparer.OrdinalIgnoreCase);
        private TabItem? _draggedTab;
        private Point _dragStartPoint;
        private Border? _draggedToolbarItem;
        private Point _toolbarDragStartPoint;

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        public MainWindow(string connectionString)
        {
            InitializeComponent();
            _initialConnectionString = connectionString;

            // Connect TreeView expanded event handler
            TreeObjectExplorer.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler(TreeItem_Expanded));
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try
            {
                IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                int darkMode = 1; // 1 = Enable
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
            }
            catch
            {
                // Ignore DWM failures
            }
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // 1. Add the initial server to Object Explorer
                await AddServerToExplorerAsync(_initialConnectionString);

                // 2. Open the first query tab
                var builder = new SqlConnectionStringBuilder(_initialConnectionString);
                string initialDb = string.IsNullOrEmpty(builder.InitialCatalog) ? "master" : builder.InitialCatalog;
                CreateNewQueryTab(_initialConnectionString, initialDb);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error initializing application: {ex.Message}", "Initialization Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #region Status Bar Updates

        public void UpdateStatusText(string text)
        {
            TxtStatusTime.Text = text;
        }

        public void UpdateStatusTime(string text)
        {
            TxtStatusTime.Text = text;
        }

        #endregion

        #region Tab Management

        public void CreateNewQueryTab(string connectionString, string databaseName, string? initialSql = null, string? customTabTitle = null)
        {
            _queryTabCounter++;

            var builder = new SqlConnectionStringBuilder(connectionString);
            string serverName = builder.DataSource;
            string tabTitle = customTabTitle ?? $"SQLQuery{_queryTabCounter}.sql ({serverName}.{databaseName})";

            var queryTabControl = new QueryTabControl(connectionString, databaseName);
            if (!string.IsNullOrEmpty(initialSql))
            {
                queryTabControl.InitialSql = initialSql;
            }

            var tabItem = new TabItem();
            tabItem.PreviewMouseLeftButtonDown += TabItem_PreviewMouseLeftButtonDown;
            tabItem.PreviewMouseMove += TabItem_PreviewMouseMove;
            tabItem.PreviewMouseLeftButtonUp += TabItem_PreviewMouseLeftButtonUp;

            // Build tab header panel with close button
            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
            var headerText = new TextBlock 
            { 
                Text = tabTitle, 
                VerticalAlignment = VerticalAlignment.Center, 
                Margin = new Thickness(0, 0, 8, 0),
                Foreground = System.Windows.Media.Brushes.LightGray
            };

            var closeBtn = new Button 
            { 
                Content = "✕", 
                Width = 14, 
                Height = 14, 
                Background = System.Windows.Media.Brushes.Transparent, 
                BorderThickness = new Thickness(0),
                Foreground = System.Windows.Media.Brushes.Gray,
                Cursor = Cursors.Hand,
                FontSize = 9,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Close Tab"
            };
            closeBtn.Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };

            // Custom hover effect for close button
            closeBtn.MouseEnter += (s, e) => { closeBtn.Foreground = System.Windows.Media.Brushes.Red; };
            closeBtn.MouseLeave += (s, e) => { closeBtn.Foreground = System.Windows.Media.Brushes.Gray; };

            closeBtn.Click += (s, e) => {
                TabQueryControls.Items.Remove(tabItem);
                if (TabQueryControls.Items.Count == 0)
                {
                    CboDatabases.ItemsSource = null;
                    TxtStatusDatabase.Text = "";
                    TxtStatusServer.Text = "No Connection";
                    TxtStatusTime.Text = "";
                }
            };

            headerPanel.Children.Add(headerText);
            headerPanel.Children.Add(closeBtn);

            tabItem.Header = headerPanel;
            tabItem.Content = queryTabControl;

            // Context Menu for Tab Header (Rename & Set Color)
            var tabContextMenu = new ContextMenu();
            var renameTabMenu = new MenuItem { Header = "Rename" };
            renameTabMenu.Click += (s, e) => {
                string newName = ShowInputDialog("Rename Tab", "Enter new tab name:", headerText.Text);
                if (!string.IsNullOrEmpty(newName))
                {
                    headerText.Text = newName;
                }
            };
            tabContextMenu.Items.Add(renameTabMenu);

            var colorTabMenu = new MenuItem { Header = "Set Color" };
            var tabColors = new[] {
                ("Red", "#6A1B1B"),
                ("Green", "#1B5E20"),
                ("Blue", "#0D47A1"),
                ("Yellow", "#8D6E63"),
                ("Orange", "#D84315"),
                ("Default (Dark)", "#252526")
            };
            foreach (var (cName, cHex) in tabColors)
            {
                var colorSubItem = new MenuItem { Header = cName };
                string hexColor = cHex;
                colorSubItem.Click += (s, e) => {
                    var brush = (System.Windows.Media.SolidColorBrush)new System.Windows.Media.BrushConverter().ConvertFromString(hexColor)!;
                    tabItem.Background = brush;
                };
                colorTabMenu.Items.Add(colorSubItem);
            }
            tabContextMenu.Items.Add(colorTabMenu);
            tabItem.ContextMenu = tabContextMenu;

            TabQueryControls.Items.Add(tabItem);
            TabQueryControls.SelectedItem = tabItem;
        }

        private async void TabQueryControls_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Only respond when the selection changes on TabQueryControls directly
            if (e.Source != TabQueryControls) return;

            if (TabQueryControls.SelectedItem is TabItem tabItem && tabItem.Content is QueryTabControl activeTab)
            {
                var builder = new SqlConnectionStringBuilder(activeTab.ConnectionString);
                string serverName = builder.DataSource;

                TxtStatusServer.Text = $"Connected: {serverName}";
                TxtStatusDatabase.Text = activeTab.DatabaseName;

                // Sync the Toolbar database selection list
                CboDatabases.SelectionChanged -= CboDatabases_SelectionChanged;
                try
                {
                    if (!_serverDatabasesCache.TryGetValue(activeTab.ConnectionString, out var dbs))
                    {
                        dbs = await DatabaseHelper.GetDatabasesAsync(activeTab.ConnectionString);
                        _serverDatabasesCache[activeTab.ConnectionString] = dbs;
                    }
                    CboDatabases.ItemsSource = dbs;
                    CboDatabases.SelectedItem = activeTab.DatabaseName;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to load databases for server: {ex.Message}", "Database Load Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                finally
                {
                    CboDatabases.SelectionChanged += CboDatabases_SelectionChanged;
                }
            }
        }

        #endregion

        #region TreeView (Object Explorer) Loading

        private async Task AddServerToExplorerAsync(string connectionString)
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            string serverName = builder.DataSource;

            // Check if server is already connected in the TreeView
            foreach (TreeViewItem item in TreeObjectExplorer.Items)
            {
                if (item.Tag is ObjectExplorerNode node && node.ConnectionString == connectionString)
                {
                    item.IsSelected = true;
                    return;
                }
            }

            var serverNode = new TreeViewItem
            {
                Header = $"🖥️ {serverName}",
                Tag = new ObjectExplorerNode { NodeType = "Server", ConnectionString = connectionString, DatabaseName = "master", DetailName = "" }
            };
            serverNode.Items.Add(new TreeViewItem { Header = "Loading..." });

            // Create Context Menu for Server Root Node
            var contextMenu = new ContextMenu();
            var newQueryMenu = new MenuItem { Header = "New Query" };
            newQueryMenu.Click += (s, e) => CreateNewQueryTab(connectionString, "master");
            contextMenu.Items.Add(newQueryMenu);

            var disconnectMenu = new MenuItem { Header = "Disconnect" };
            disconnectMenu.Click += (s, e) => DisconnectServer(serverNode);
            contextMenu.Items.Add(disconnectMenu);

            serverNode.ContextMenu = contextMenu;

            TreeObjectExplorer.Items.Add(serverNode);
            serverNode.IsExpanded = true;
            await Task.CompletedTask;
        }

        private void DisconnectServer(TreeViewItem serverNode)
        {
            TreeObjectExplorer.Items.Remove(serverNode);

            // If no servers left, prompt user or show connection window
            if (TreeObjectExplorer.Items.Count == 0)
            {
                BtnConnect_Click(this, new RoutedEventArgs());
            }
        }

        private async void TreeItem_Expanded(object sender, RoutedEventArgs e)
        {
            var item = e.OriginalSource as TreeViewItem;
            if (item == null || item.Tag == null) return;

            // Check for dummy loading node
            if (item.Items.Count == 1 && (item.Items[0] as TreeViewItem)?.Header.ToString() == "Loading...")
            {
                item.Items.Clear();
                var node = (ObjectExplorerNode)item.Tag;
                string type = node.NodeType;
                string connStr = node.ConnectionString;
                string dbName = node.DatabaseName;
                string detailName = node.DetailName;

                try
                {
                    if (type == "Server")
                    {
                        var dbsFolder = new TreeViewItem
                        {
                            Header = "📁 Databases",
                            Tag = new ObjectExplorerNode { NodeType = "DatabasesFolder", ConnectionString = connStr, DatabaseName = "master" }
                        };
                        dbsFolder.Items.Add(new TreeViewItem { Header = "Loading..." });
                        item.Items.Add(dbsFolder);

                        // Defer expansion so it is processed after rendering, triggering the load
                        _ = Dispatcher.BeginInvoke(new Action(() => dbsFolder.IsExpanded = true), System.Windows.Threading.DispatcherPriority.Background);
                    }
                    else if (type == "DatabasesFolder")
                    {
                        var dbs = await DatabaseHelper.GetDatabasesAsync(connStr);
                        _serverDatabasesCache[connStr] = dbs;

                        foreach (var db in dbs)
                        {
                            var dbItem = new TreeViewItem
                            {
                                Header = $"🛢️ {db}",
                                Tag = new ObjectExplorerNode { NodeType = "Database", ConnectionString = connStr, DatabaseName = db }
                            };
                            dbItem.Items.Add(new TreeViewItem { Header = "Loading..." });

                            // Context Menu for database node
                            var contextMenu = new ContextMenu();
                            var newQueryItem = new MenuItem { Header = "New Query" };
                            newQueryItem.Click += (s, ev) => CreateNewQueryTab(connStr, db);
                            contextMenu.Items.Add(newQueryItem);
                            dbItem.ContextMenu = contextMenu;

                            item.Items.Add(dbItem);
                        }
                    }
                    else if (type == "Database")
                    {
                        var tablesFolder = new TreeViewItem
                        {
                            Header = GetFolderHeader(dbName, "TablesFolder", "Tables"),
                            Tag = new ObjectExplorerNode { NodeType = "TablesFolder", ConnectionString = connStr, DatabaseName = dbName }
                        };
                        tablesFolder.Items.Add(new TreeViewItem { Header = "Loading..." });
                        tablesFolder.ContextMenu = CreateFolderContextMenu(tablesFolder, connStr, dbName, "TablesFolder", "Tables");
                        item.Items.Add(tablesFolder);

                        var viewsFolder = new TreeViewItem
                        {
                            Header = GetFolderHeader(dbName, "ViewsFolder", "Views"),
                            Tag = new ObjectExplorerNode { NodeType = "ViewsFolder", ConnectionString = connStr, DatabaseName = dbName }
                        };
                        viewsFolder.Items.Add(new TreeViewItem { Header = "Loading..." });
                        viewsFolder.ContextMenu = CreateFolderContextMenu(viewsFolder, connStr, dbName, "ViewsFolder", "Views");
                        item.Items.Add(viewsFolder);

                        var spsFolder = new TreeViewItem
                        {
                            Header = GetFolderHeader(dbName, "SpsFolder", "Stored Procedures"),
                            Tag = new ObjectExplorerNode { NodeType = "SpsFolder", ConnectionString = connStr, DatabaseName = dbName }
                        };
                        spsFolder.Items.Add(new TreeViewItem { Header = "Loading..." });
                        spsFolder.ContextMenu = CreateFolderContextMenu(spsFolder, connStr, dbName, "SpsFolder", "Stored Procedures");
                        item.Items.Add(spsFolder);

                        var funcsFolder = new TreeViewItem
                        {
                            Header = GetFolderHeader(dbName, "FuncsFolder", "Functions"),
                            Tag = new ObjectExplorerNode { NodeType = "FuncsFolder", ConnectionString = connStr, DatabaseName = dbName }
                        };
                        funcsFolder.Items.Add(new TreeViewItem { Header = "Loading..." });
                        funcsFolder.ContextMenu = CreateFolderContextMenu(funcsFolder, connStr, dbName, "FuncsFolder", "Functions");
                        item.Items.Add(funcsFolder);
                    }
                    else if (type == "TablesFolder")
                    {
                        string filter = GetFolderFilter(dbName, "TablesFolder");
                        var tables = await DatabaseHelper.GetTablesAsync(connStr, dbName);
                        foreach (var table in tables)
                        {
                            if (!string.IsNullOrEmpty(filter) && !table.Contains(filter, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }
                            var tableItem = new TreeViewItem
                            {
                                Header = $"田 {table}",
                                Tag = new ObjectExplorerNode { NodeType = "Table", ConnectionString = connStr, DatabaseName = dbName, DetailName = table }
                            };
                            tableItem.Items.Add(new TreeViewItem { Header = "Loading..." });
                            tableItem.ContextMenu = CreateObjectContextMenu(connStr, dbName, "Table", table);
                            item.Items.Add(tableItem);
                        }
                    }
                    else if (type == "ViewsFolder")
                    {
                        string filter = GetFolderFilter(dbName, "ViewsFolder");
                        var views = await DatabaseHelper.GetViewsAsync(connStr, dbName);
                        foreach (var view in views)
                        {
                            if (!string.IsNullOrEmpty(filter) && !view.Contains(filter, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }
                            var viewItem = new TreeViewItem
                            {
                                Header = $"👓 {view}",
                                Tag = new ObjectExplorerNode { NodeType = "View", ConnectionString = connStr, DatabaseName = dbName, DetailName = view }
                            };
                            viewItem.Items.Add(new TreeViewItem { Header = "Loading..." });
                            viewItem.ContextMenu = CreateObjectContextMenu(connStr, dbName, "View", view);
                            item.Items.Add(viewItem);
                        }
                    }
                    else if (type == "SpsFolder")
                    {
                        string filter = GetFolderFilter(dbName, "SpsFolder");
                        var sps = await DatabaseHelper.GetStoredProceduresAsync(connStr, dbName);
                        foreach (var sp in sps)
                        {
                            if (!string.IsNullOrEmpty(filter) && !sp.Contains(filter, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }
                            var spItem = new TreeViewItem
                            {
                                Header = $"⚡ {sp}",
                                Tag = new ObjectExplorerNode { NodeType = "StoredProcedure", ConnectionString = connStr, DatabaseName = dbName, DetailName = sp }
                            };
                            spItem.ContextMenu = CreateObjectContextMenu(connStr, dbName, "StoredProcedure", sp);
                            item.Items.Add(spItem);
                        }
                    }
                    else if (type == "FuncsFolder")
                    {
                        string filter = GetFolderFilter(dbName, "FuncsFolder");
                        var funcs = await DatabaseHelper.GetFunctionsAsync(connStr, dbName);
                        foreach (var func in funcs)
                        {
                            if (!string.IsNullOrEmpty(filter) && !func.Contains(filter, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }
                            var funcItem = new TreeViewItem
                            {
                                Header = $"⚙️ {func}",
                                Tag = new ObjectExplorerNode { NodeType = "Function", ConnectionString = connStr, DatabaseName = dbName, DetailName = func }
                            };
                            funcItem.ContextMenu = CreateObjectContextMenu(connStr, dbName, "Function", func);
                            item.Items.Add(funcItem);
                        }
                    }
                    else if (type == "Table" || type == "View")
                    {
                        var colsFolder = new TreeViewItem
                        {
                            Header = "📁 Columns",
                            Tag = new ObjectExplorerNode { NodeType = "ColumnsFolder", ConnectionString = connStr, DatabaseName = dbName, DetailName = detailName }
                        };
                        colsFolder.Items.Add(new TreeViewItem { Header = "Loading..." });
                        item.Items.Add(colsFolder);
                    }
                    else if (type == "ColumnsFolder")
                    {
                        var columns = await DatabaseHelper.GetColumnsAsync(connStr, dbName, detailName);
                        foreach (var col in columns)
                        {
                            string pkSuffix = col.IsPrimaryKey ? ", PK" : "";
                            string icon = col.IsPrimaryKey ? "🔑" : "🔹";
                            var colItem = new TreeViewItem
                            {
                                Header = $"{icon} {col.ColumnName} ({col.DataType}{pkSuffix})",
                                Tag = new ObjectExplorerNode { NodeType = "Column", ConnectionString = connStr, DatabaseName = dbName, DetailName = col.ColumnName }
                            };
                            item.Items.Add(colItem);
                        }
                    }
                }
                catch (Exception ex)
                {
                    item.Items.Add(new TreeViewItem { Header = $"⚠️ Error: {ex.Message}" });
                }
            }
        }

        #endregion

        #region Toolbar Events

        private async void CboDatabases_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CboDatabases.SelectedItem == null) return;

            string selectedDb = CboDatabases.SelectedItem.ToString() ?? "master";
            if (TabQueryControls.SelectedItem is TabItem tabItem && tabItem.Content is QueryTabControl activeTab)
            {
                activeTab.DatabaseName = selectedDb;
                TxtStatusDatabase.Text = selectedDb;

                // Dynamically update the header tab title
                var builder = new SqlConnectionStringBuilder(activeTab.ConnectionString);
                string serverName = builder.DataSource;
                
                if (tabItem.Header is StackPanel headerPanel && headerPanel.Children[0] is TextBlock textBlock)
                {
                    textBlock.Text = $"SQLQuery{GetTabNumber(tabItem)}.sql ({serverName}.{selectedDb})";
                }

                // Cache metadata & update autocompletion suggestions
                await activeTab.CacheAndRefreshAutocompleteAsync();
            }
        }

        private int GetTabNumber(TabItem item)
        {
            // Simple helper to guess tab number from current index or order
            int index = TabQueryControls.Items.IndexOf(item);
            return index >= 0 ? index + 1 : 1;
        }

        private void BtnExecute_Click(object sender, RoutedEventArgs e)
        {
            ExecuteActiveTabQuery();
        }

        private void ExecuteActiveTabQuery()
        {
            if (TabQueryControls.SelectedItem is TabItem tabItem && tabItem.Content is QueryTabControl activeTab)
            {
                activeTab.ExecuteQuery();
            }
            else
            {
                MessageBox.Show("No active query window is open.", "Execution Error", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            var connWindow = new ConnectionWindow();
            if (connWindow.ShowDialog() == true)
            {
                try
                {
                    await AddServerToExplorerAsync(connWindow.ConnectionString);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to connect to server: {ex.Message}", "Connection Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            // Disconnect selected server node in the Object Explorer
            if (TreeObjectExplorer.SelectedItem is TreeViewItem selectedItem)
            {
                // Find parent server node
                TreeViewItem serverNode = selectedItem;
                while (serverNode.Parent is TreeViewItem parentNode)
                {
                    serverNode = parentNode;
                }

                if (serverNode.Tag is ObjectExplorerNode node && node.NodeType == "Server")
                {
                    DisconnectServer(serverNode);
                    return;
                }
            }

            // Fallback: Disconnect first server node
            if (TreeObjectExplorer.Items.Count > 0 && TreeObjectExplorer.Items[0] is TreeViewItem firstServerNode)
            {
                DisconnectServer(firstServerNode);
            }
        }

        #endregion

        #region Global Hotkeys

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F5)
            {
                ExecuteActiveTabQuery();
                e.Handled = true;
            }
            else if (e.Key == Key.N && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                BtnNewQuery_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                SaveActiveTabQuery();
                e.Handled = true;
            }
        }

        private void BtnNewQuery_Click(object sender, RoutedEventArgs e)
        {
            if (TabQueryControls.SelectedItem is TabItem activeTabItem && activeTabItem.Content is QueryTabControl activeTab)
            {
                CreateNewQueryTab(activeTab.ConnectionString, activeTab.DatabaseName);
            }
            else
            {
                CreateNewQueryTab(_initialConnectionString, "master");
            }
        }

        private void BtnSaveQuery_Click(object sender, RoutedEventArgs e)
        {
            SaveActiveTabQuery();
        }

        private async void SaveActiveTabQuery()
        {
            if (TabQueryControls.SelectedItem is TabItem tabItem && tabItem.Content is QueryTabControl activeTab)
            {
                var saveFileDialog = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "SQL Files (*.sql)|*.sql|All Files (*.*)|*.*",
                    DefaultExt = ".sql",
                    Title = "Save SQL Query"
                };

                if (saveFileDialog.ShowDialog() == true)
                {
                    try
                    {
                        string resultJson = await activeTab.SqlEditorWebView.ExecuteScriptAsync("getQueryText()");
                        string sqlQuery = JsonSerializer.Deserialize<string>(resultJson) ?? "";

                        File.WriteAllText(saveFileDialog.FileName, sqlQuery);

                        string fileName = Path.GetFileName(saveFileDialog.FileName);
                        if (tabItem.Header is StackPanel headerPanel && headerPanel.Children[0] is TextBlock textBlock)
                        {
                            textBlock.Text = fileName;
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to save query file: {ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }

        private string ShowInputDialog(string title, string prompt, string defaultText)
        {
            var dialog = new Window
            {
                Title = title,
                Width = 360,
                Height = 175,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                BorderBrush = (System.Windows.Media.SolidColorBrush)new System.Windows.Media.BrushConverter().ConvertFromString("#2D2D30")!,
                BorderThickness = new Thickness(1),
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = (System.Windows.Media.SolidColorBrush)new System.Windows.Media.BrushConverter().ConvertFromString("#1E1E1E")!,
                Foreground = System.Windows.Media.Brushes.White
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // Custom Title Bar
            var titleBar = new Border
            {
                Background = (System.Windows.Media.SolidColorBrush)new System.Windows.Media.BrushConverter().ConvertFromString("#252526")!,
                BorderBrush = (System.Windows.Media.SolidColorBrush)new System.Windows.Media.BrushConverter().ConvertFromString("#2D2D30")!,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(12, 6, 12, 6)
            };
            titleBar.MouseLeftButtonDown += (s, e) => { if (e.LeftButton == MouseButtonState.Pressed) dialog.DragMove(); };

            var titleText = new TextBlock
            {
                Text = title,
                Foreground = System.Windows.Media.Brushes.White,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            titleBar.Child = titleText;
            Grid.SetRow(titleBar, 0);
            grid.Children.Add(titleBar);

            // Content Grid
            var contentGrid = new Grid { Margin = new Thickness(15) };
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(contentGrid, 1);
            grid.Children.Add(contentGrid);

            var label = new TextBlock { Text = prompt, Foreground = System.Windows.Media.Brushes.LightGray, Margin = new Thickness(0, 0, 0, 8), FontSize = 12 };
            Grid.SetRow(label, 0);
            contentGrid.Children.Add(label);

            var textBox = new TextBox 
            { 
                Text = defaultText, 
                Background = (System.Windows.Media.SolidColorBrush)new System.Windows.Media.BrushConverter().ConvertFromString("#252526")!, 
                Foreground = System.Windows.Media.Brushes.White, 
                CaretBrush = System.Windows.Media.Brushes.White,
                BorderBrush = (System.Windows.Media.SolidColorBrush)new System.Windows.Media.BrushConverter().ConvertFromString("#3E3E42")!, 
                BorderThickness = new Thickness(1), 
                Padding = new Thickness(6, 4, 6, 4), 
                Margin = new Thickness(0, 0, 0, 15),
                FontSize = 12
            };
            Grid.SetRow(textBox, 1);
            contentGrid.Children.Add(textBox);

            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetRow(buttonPanel, 2);
            contentGrid.Children.Add(buttonPanel);

            var okButton = new Button 
            { 
                Content = "OK", 
                Width = 75, 
                Height = 25, 
                IsDefault = true, 
                Background = (System.Windows.Media.SolidColorBrush)new System.Windows.Media.BrushConverter().ConvertFromString("#007ACC")!, 
                Foreground = System.Windows.Media.Brushes.White, 
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 0, 8, 0)
            };
            okButton.Click += (s, e) => { dialog.DialogResult = true; dialog.Close(); };

            var cancelButton = new Button 
            { 
                Content = "Cancel", 
                Width = 75, 
                Height = 25, 
                IsCancel = true, 
                Background = (System.Windows.Media.SolidColorBrush)new System.Windows.Media.BrushConverter().ConvertFromString("#3E3E42")!, 
                Foreground = System.Windows.Media.Brushes.White, 
                BorderThickness = new Thickness(0) 
            };
            cancelButton.Click += (s, e) => { dialog.DialogResult = false; dialog.Close(); };

            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);

            dialog.Content = grid;
            
            dialog.Activated += (s, e) =>
            {
                textBox.Focus();
                textBox.SelectAll();
            };

            if (dialog.ShowDialog() == true)
            {
                return textBox.Text.Trim();
            }
            return string.Empty;
        }

        private void ToolbarItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border)
            {
                var hitTestResult = System.Windows.Media.VisualTreeHelper.HitTest(border, e.GetPosition(border));
                if (hitTestResult != null && hitTestResult.VisualHit is TextBlock textBlock && textBlock.Text == "⋮")
                {
                    _draggedToolbarItem = border;
                    _toolbarDragStartPoint = e.GetPosition(ToolbarPanel);
                    border.Opacity = 0.6;
                }
            }
        }

        private void ToolbarItem_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_draggedToolbarItem != null && e.LeftButton == MouseButtonState.Pressed)
            {
                var currentPoint = e.GetPosition(ToolbarPanel);
                
                if (Math.Abs(currentPoint.X - _toolbarDragStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance)
                {
                    var hitTestResult = System.Windows.Media.VisualTreeHelper.HitTest(ToolbarPanel, e.GetPosition(ToolbarPanel));
                    if (hitTestResult != null)
                    {
                        var hoveredBorder = FindAncestor<Border>(hitTestResult.VisualHit);
                        if (hoveredBorder != null && hoveredBorder != _draggedToolbarItem && ToolbarPanel.Children.Contains(hoveredBorder))
                        {
                            int sourceIndex = ToolbarPanel.Children.IndexOf(_draggedToolbarItem);
                            int targetIndex = ToolbarPanel.Children.IndexOf(hoveredBorder);

                            if (sourceIndex >= 0 && targetIndex >= 0)
                            {
                                ToolbarPanel.Children.RemoveAt(sourceIndex);
                                ToolbarPanel.Children.Insert(targetIndex, _draggedToolbarItem);
                            }
                        }
                    }
                }
            }
        }

        private void ToolbarItem_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_draggedToolbarItem != null)
            {
                _draggedToolbarItem.Opacity = 1.0;
                _draggedToolbarItem = null;
            }
        }

        private void TabItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is TabItem tabItem)
            {
                _draggedTab = tabItem;
                _dragStartPoint = e.GetPosition(TabQueryControls);
            }
        }

        private void TabItem_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_draggedTab != null && e.LeftButton == MouseButtonState.Pressed)
            {
                var currentPoint = e.GetPosition(TabQueryControls);
                
                if (Math.Abs(currentPoint.X - _dragStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(currentPoint.Y - _dragStartPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    var hitTestResult = System.Windows.Media.VisualTreeHelper.HitTest(TabQueryControls, e.GetPosition(TabQueryControls));
                    if (hitTestResult != null)
                    {
                        var hoveredTabItem = FindAncestor<TabItem>(hitTestResult.VisualHit);
                        if (hoveredTabItem != null && hoveredTabItem != _draggedTab)
                        {
                            int sourceIndex = TabQueryControls.Items.IndexOf(_draggedTab);
                            int targetIndex = TabQueryControls.Items.IndexOf(hoveredTabItem);

                            if (sourceIndex >= 0 && targetIndex >= 0)
                            {
                                TabQueryControls.Items.RemoveAt(sourceIndex);
                                TabQueryControls.Items.Insert(targetIndex, _draggedTab);
                                TabQueryControls.SelectedItem = _draggedTab;
                            }
                        }
                    }
                }
            }
        }

        private void TabItem_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _draggedTab = null;
        }

        private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            do
            {
                if (current is T ancestor)
                {
                    return ancestor;
                }
                current = System.Windows.Media.VisualTreeHelper.GetParent(current);
            }
            while (current != null);
            return null;
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

        private ContextMenu CreateFolderContextMenu(TreeViewItem folderItem, string connStr, string dbName, string folderType, string baseName)
        {
            var menu = new ContextMenu();
            
            var filterItem = new MenuItem { Header = "Filter..." };
            filterItem.Click += (s, e) => OpenFilterDialog(folderItem, connStr, dbName, folderType, baseName);
            menu.Items.Add(filterItem);

            var clearFilterItem = new MenuItem { Header = "Clear Filter" };
            clearFilterItem.Click += (s, e) => ClearFilter(folderItem, connStr, dbName, folderType, baseName);
            menu.Items.Add(clearFilterItem);

            return menu;
        }

        private void OpenFilterDialog(TreeViewItem folderItem, string connStr, string dbName, string folderType, string baseName)
        {
            string key = $"{dbName}_{folderType}";
            string currentFilter = GetFolderFilter(dbName, folderType);
            string filterText = ShowInputDialog("Filter Objects", "Enter filter query (wildcard/substring):", currentFilter);

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

                folderItem.Header = GetFolderHeader(dbName, folderType, baseName);

                // Reload the folder
                folderItem.IsExpanded = false;
                folderItem.Items.Clear();
                folderItem.Items.Add(new TreeViewItem { Header = "Loading..." });
                folderItem.IsExpanded = true;
            }
        }

        private void ClearFilter(TreeViewItem folderItem, string connStr, string dbName, string folderType, string baseName)
        {
            string key = $"{dbName}_{folderType}";
            if (_folderFilters.ContainsKey(key))
            {
                _folderFilters.Remove(key);
                folderItem.Header = GetFolderHeader(dbName, folderType, baseName);

                // Reload the folder
                folderItem.IsExpanded = false;
                folderItem.Items.Clear();
                folderItem.Items.Add(new TreeViewItem { Header = "Loading..." });
                folderItem.IsExpanded = true;
            }
        }

        private ContextMenu CreateObjectContextMenu(string connectionString, string databaseName, string objectType, string objectName)
        {
            var menu = new ContextMenu();

            var scriptAsMenu = new MenuItem { Header = "Script Object as" };

            var createToMenu = new MenuItem { Header = "CREATE To" };
            var createNewQuery = new MenuItem { Header = "New Query Editor Window" };
            createNewQuery.Click += async (s, e) => await GenerateScriptObjectAsync(connectionString, databaseName, objectType, objectName, "CREATE");
            createToMenu.Items.Add(createNewQuery);

            var alterToMenu = new MenuItem { Header = "ALTER To" };
            var alterNewQuery = new MenuItem { Header = "New Query Editor Window" };
            alterNewQuery.Click += async (s, e) => await GenerateScriptObjectAsync(connectionString, databaseName, objectType, objectName, "ALTER");
            alterToMenu.Items.Add(alterNewQuery);

            var dropToMenu = new MenuItem { Header = "DROP To" };
            var dropNewQuery = new MenuItem { Header = "New Query Editor Window" };
            dropNewQuery.Click += async (s, e) => await GenerateScriptObjectAsync(connectionString, databaseName, objectType, objectName, "DROP");
            dropToMenu.Items.Add(dropNewQuery);

            scriptAsMenu.Items.Add(createToMenu);
            scriptAsMenu.Items.Add(alterToMenu);
            scriptAsMenu.Items.Add(dropToMenu);

            menu.Items.Add(scriptAsMenu);

            return menu;
        }

        private async Task GenerateScriptObjectAsync(string connectionString, string databaseName, string objectType, string objectName, string scriptType)
        {
            string sql = "";
            try
            {
                if (scriptType == "DROP")
                {
                    string dropKeyword = objectType;
                    if (objectType == "StoredProcedure") dropKeyword = "PROCEDURE";
                    sql = $"DROP {dropKeyword.ToUpper()} {objectName};";
                }
                else if (objectType == "Table")
                {
                    if (scriptType == "CREATE")
                    {
                        sql = await DatabaseHelper.GenerateTableCreateScriptAsync(connectionString, databaseName, objectName);
                    }
                    else // ALTER Table
                    {
                        sql = $"-- Alter Table Script for {objectName}\n-- ALTER TABLE {objectName} ADD [NewColumnName] DataType;";
                    }
                }
                else // View, StoredProcedure, Function
                {
                    string def = await DatabaseHelper.GetObjectDefinitionAsync(connectionString, databaseName, objectName);
                    if (string.IsNullOrEmpty(def))
                    {
                        sql = $"-- Could not retrieve definition for {objectName}.";
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

            CreateNewQueryTab(connectionString, databaseName, sql, $"{objectName}_{scriptType}.sql");
        }

        #endregion
    }
}