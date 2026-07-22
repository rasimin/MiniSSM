using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Data.SqlClient;

namespace SSMS
{
    public partial class MainWindow : Window
    {

        private string _typeAheadBuffer = string.Empty;
        private DispatcherTimer? _typeAheadTimer;

        private void OnTreeViewItemPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is TreeViewItem item)
            {
                _useObjectExplorerContextForNewQuery = true;
                item.IsSelected = true;
                item.Focus();
            }
        }

        private void TreeObjectExplorer_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var selectedItem = TreeObjectExplorer.SelectedItem as TreeViewItem;
            var visibleItems = GetVisibleTreeViewItems(TreeObjectExplorer);

            if (e.Key == Key.Down)
            {
                e.Handled = true;
                if (visibleItems.Count == 0) return;
                int currentIndex = selectedItem != null ? visibleItems.IndexOf(selectedItem) : -1;
                int nextIndex = currentIndex < visibleItems.Count - 1 ? currentIndex + 1 : currentIndex;
                if (nextIndex >= 0 && nextIndex < visibleItems.Count)
                {
                    SelectAndFocusTreeViewItem(visibleItems[nextIndex]);
                }
            }
            else if (e.Key == Key.Up)
            {
                e.Handled = true;
                if (visibleItems.Count == 0) return;
                int currentIndex = selectedItem != null ? visibleItems.IndexOf(selectedItem) : -1;
                int prevIndex = currentIndex > 0 ? currentIndex - 1 : 0;
                if (prevIndex >= 0 && prevIndex < visibleItems.Count)
                {
                    SelectAndFocusTreeViewItem(visibleItems[prevIndex]);
                }
            }
            else if (e.Key == Key.Right)
            {
                if (selectedItem != null)
                {
                    e.Handled = true;
                    if (selectedItem.HasItems && !selectedItem.IsExpanded)
                    {
                        selectedItem.IsExpanded = true;
                    }
                    else if (selectedItem.HasItems && selectedItem.IsExpanded)
                    {
                        if (selectedItem.Items.Count > 0 && selectedItem.Items[0] is TreeViewItem firstChild)
                        {
                            SelectAndFocusTreeViewItem(firstChild);
                        }
                    }
                }
            }
            else if (e.Key == Key.Left)
            {
                if (selectedItem != null)
                {
                    e.Handled = true;
                    if (selectedItem.HasItems && selectedItem.IsExpanded)
                    {
                        selectedItem.IsExpanded = false;
                    }
                    else
                    {
                        var parentItem = FindParentTreeViewItem(TreeObjectExplorer, selectedItem);
                        if (parentItem != null)
                        {
                            SelectAndFocusTreeViewItem(parentItem);
                        }
                    }
                }
            }
            else if (e.Key == Key.Enter)
            {
                if (selectedItem != null && selectedItem.HasItems)
                {
                    e.Handled = true;
                    selectedItem.IsExpanded = !selectedItem.IsExpanded;
                }
            }
            else
            {
                char keyChar = GetCharFromKey(e.Key);
                if (keyChar != '\0' && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
                {
                    e.Handled = true;
                    ProcessTypeAheadSearch(keyChar, selectedItem, visibleItems);
                }
            }
        }

        private void SelectAndFocusTreeViewItem(TreeViewItem item)
        {
            _useObjectExplorerContextForNewQuery = true;
            item.IsSelected = true;
            item.Focus();
            item.BringIntoView();
        }

        private List<TreeViewItem> GetVisibleTreeViewItems(ItemsControl parent)
        {
            var list = new List<TreeViewItem>();
            foreach (var child in parent.Items.OfType<TreeViewItem>())
            {
                list.Add(child);
                if (child.IsExpanded && child.Items.Count > 0)
                {
                    list.AddRange(GetVisibleTreeViewItems(child));
                }
            }
            return list;
        }

        private TreeViewItem? FindParentTreeViewItem(ItemsControl parent, TreeViewItem target)
        {
            foreach (var child in parent.Items.OfType<TreeViewItem>())
            {
                if (child.Items.Contains(target))
                {
                    return child;
                }
                var foundInChild = FindParentTreeViewItem(child, target);
                if (foundInChild != null)
                {
                    return foundInChild;
                }
            }
            return null;
        }

        private void ProcessTypeAheadSearch(char c, TreeViewItem? currentItem, List<TreeViewItem> visibleItems)
        {
            if (_typeAheadTimer == null)
            {
                _typeAheadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
                _typeAheadTimer.Tick += (_, _) =>
                {
                    _typeAheadBuffer = string.Empty;
                    _typeAheadTimer.Stop();
                };
            }
            _typeAheadTimer.Stop();
            _typeAheadTimer.Start();

            _typeAheadBuffer += c;
            string query = _typeAheadBuffer.ToLowerInvariant();

            if (visibleItems.Count == 0) return;

            int startIndex = currentItem != null ? visibleItems.IndexOf(currentItem) : 0;
            if (query.Length == 1 && startIndex >= 0 && startIndex < visibleItems.Count)
            {
                string currentName = GetNodeDisplayText(visibleItems[startIndex]).ToLowerInvariant();
                if (currentName.StartsWith(query))
                {
                    startIndex = (startIndex + 1) % visibleItems.Count;
                }
            }

            for (int i = 0; i < visibleItems.Count; i++)
            {
                int evalIndex = (startIndex + i) % visibleItems.Count;
                var item = visibleItems[evalIndex];
                string name = GetNodeDisplayText(item).ToLowerInvariant();

                if (MatchesTypeAhead(name, item, query))
                {
                    SelectAndFocusTreeViewItem(item);
                    break;
                }
            }
        }

        private bool MatchesTypeAhead(string cleanName, TreeViewItem item, string query)
        {
            if (cleanName.StartsWith(query)) return true;
            if (item.Tag is ObjectExplorerNode node)
            {
                if (!string.IsNullOrEmpty(node.DetailName))
                {
                    string detail = node.DetailName.ToLowerInvariant();
                    if (detail.StartsWith(query)) return true;
                    var dotParts = detail.Split('.');
                    if (dotParts.Length > 1 && dotParts[1].StartsWith(query)) return true;
                }
            }
            return false;
        }

        private string GetNodeDisplayText(TreeViewItem item)
        {
            if (item.Tag is ObjectExplorerNode node && !string.IsNullOrWhiteSpace(node.DetailName))
            {
                return node.DetailName;
            }

            if (item.Header is string strHeader)
            {
                return StripIconPrefix(strHeader);
            }

            if (item.Header is Grid grid)
            {
                foreach (var child in grid.Children)
                {
                    if (child is TextBlock tb) return StripIconPrefix(tb.Text);
                    if (child is StackPanel sp)
                    {
                        foreach (var spChild in sp.Children)
                        {
                            if (spChild is TextBlock spTb) return StripIconPrefix(spTb.Text);
                        }
                    }
                }
            }

            return StripIconPrefix(item.Header?.ToString() ?? string.Empty);
        }

        private string StripIconPrefix(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            int firstLetter = 0;
            while (firstLetter < text.Length && !char.IsLetterOrDigit(text[firstLetter]))
            {
                firstLetter++;
            }
            return firstLetter < text.Length ? text.Substring(firstLetter) : text;
        }

        private char GetCharFromKey(Key key)
        {
            if (key >= Key.A && key <= Key.Z)
            {
                return (char)('a' + (key - Key.A));
            }
            if (key >= Key.D0 && key <= Key.D9)
            {
                return (char)('0' + (key - Key.D0));
            }
            if (key >= Key.NumPad0 && key <= Key.NumPad9)
            {
                return (char)('0' + (key - Key.NumPad0));
            }
            if (key == Key.OemMinus) return '-';
            return '\0';
        }

        private string? GetSelectedObjectExplorerDatabase()
        {
            if (TreeObjectExplorer.SelectedItem is TreeViewItem item && item.Tag is ObjectExplorerNode node)
            {
                return node.DatabaseName;
            }
            return null;
        }

        private void TreeObjectExplorer_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
        {
            if (e.TargetObject is TreeViewItem item && item.IsExpanded)
            {
                e.Handled = true;
            }
        }

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

            var refreshMenu = new MenuItem { Header = "Refresh" };
            refreshMenu.Click += async (s, e) => await RefreshObjectExplorerNodeAsync(serverNode);
            contextMenu.Items.Add(refreshMenu);

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
                var (loadingItem, loadingTimer) = CreateAnimatedLoadingItem();
                item.Items.Add(loadingItem);
                await Dispatcher.Yield(DispatcherPriority.Render);
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
                            Tag = new ObjectExplorerNode { NodeType = "DatabasesFolder", ConnectionString = connStr, DatabaseName = "master" }
                        };
                        SetFilterableFolderHeader(dbsFolder, connStr, "DatabasesFolder", "Databases");
                        dbsFolder.Items.Add(new TreeViewItem { Header = "Loading..." });
                        item.Items.Add(dbsFolder);

                        // Defer expansion so it is processed after rendering, triggering the load
                        _ = Dispatcher.BeginInvoke(new Action(() => dbsFolder.IsExpanded = true), System.Windows.Threading.DispatcherPriority.Background);
                    }
                    else if (type == "DatabasesFolder")
                    {
                        string filter = GetFolderFilter(connStr, "DatabasesFolder");
                        var dbs = await DatabaseHelper.GetDatabasesAsync(connStr);
                        _serverDatabasesCache[connStr] = dbs;

                        foreach (var db in dbs)
                        {
                            if (!string.IsNullOrEmpty(filter) && !db.Contains(filter, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }
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
                            var refreshItem = new MenuItem { Header = "Refresh" };
                            refreshItem.Click += async (s, ev) => await RefreshObjectExplorerNodeAsync(dbItem);
                            contextMenu.Items.Add(refreshItem);
                            dbItem.ContextMenu = contextMenu;

                            item.Items.Add(dbItem);
                        }
                    }
                    else if (type == "Database")
                    {
                        var tablesFolder = new TreeViewItem
                        {
                            Tag = new ObjectExplorerNode { NodeType = "TablesFolder", ConnectionString = connStr, DatabaseName = dbName }
                        };
                        SetFilterableFolderHeader(tablesFolder, dbName, "TablesFolder", "Tables");
                        tablesFolder.Items.Add(new TreeViewItem { Header = "Loading..." });
                        tablesFolder.ContextMenu = CreateFolderContextMenu(tablesFolder, connStr, dbName, "TablesFolder", "Tables");
                        item.Items.Add(tablesFolder);

                        var viewsFolder = new TreeViewItem
                        {
                            Tag = new ObjectExplorerNode { NodeType = "ViewsFolder", ConnectionString = connStr, DatabaseName = dbName }
                        };
                        SetFilterableFolderHeader(viewsFolder, dbName, "ViewsFolder", "Views");
                        viewsFolder.Items.Add(new TreeViewItem { Header = "Loading..." });
                        viewsFolder.ContextMenu = CreateFolderContextMenu(viewsFolder, connStr, dbName, "ViewsFolder", "Views");
                        item.Items.Add(viewsFolder);

                        var spsFolder = new TreeViewItem
                        {
                            Tag = new ObjectExplorerNode { NodeType = "SpsFolder", ConnectionString = connStr, DatabaseName = dbName }
                        };
                        SetFilterableFolderHeader(spsFolder, dbName, "SpsFolder", "Stored Procedures");
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
                        var scalarFolder = new TreeViewItem
                        {
                            Tag = new ObjectExplorerNode { NodeType = "ScalarFunctionsFolder", ConnectionString = connStr, DatabaseName = dbName }
                        };
                        SetFilterableFolderHeader(scalarFolder, dbName, "ScalarFunctionsFolder", "Scalar-valued Functions");
                        scalarFolder.Items.Add(new TreeViewItem { Header = "Loading..." });
                        scalarFolder.ContextMenu = CreateFolderContextMenu(scalarFolder, connStr, dbName, "ScalarFunctionsFolder", "Scalar-valued Functions");
                        item.Items.Add(scalarFolder);

                        var tableFolder = new TreeViewItem
                        {
                            Tag = new ObjectExplorerNode { NodeType = "TableFunctionsFolder", ConnectionString = connStr, DatabaseName = dbName }
                        };
                        SetFilterableFolderHeader(tableFolder, dbName, "TableFunctionsFolder", "Table-valued Functions");
                        tableFolder.Items.Add(new TreeViewItem { Header = "Loading..." });
                        tableFolder.ContextMenu = CreateFolderContextMenu(tableFolder, connStr, dbName, "TableFunctionsFolder", "Table-valued Functions");
                        item.Items.Add(tableFolder);
                    }
                    else if (type == "ScalarFunctionsFolder" || type == "TableFunctionsFolder")
                    {
                        string filter = GetFolderFilter(dbName, type);
                        var funcs = await DatabaseHelper.GetFunctionsAsync(connStr, dbName, type == "TableFunctionsFolder");
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

                        if (type == "Table")
                        {
                            var indexesFolder = new TreeViewItem
                            {
                                Header = "📁 Indexes",
                                Tag = new ObjectExplorerNode { NodeType = "IndexesFolder", ConnectionString = connStr, DatabaseName = dbName, DetailName = detailName }
                            };
                            indexesFolder.Items.Add(new TreeViewItem { Header = "Loading..." });
                            item.Items.Add(indexesFolder);

                            var triggersFolder = new TreeViewItem
                            {
                                Header = "📁 Triggers",
                                Tag = new ObjectExplorerNode { NodeType = "TriggersFolder", ConnectionString = connStr, DatabaseName = dbName, DetailName = detailName }
                            };
                            triggersFolder.Items.Add(new TreeViewItem { Header = "Loading..." });
                            item.Items.Add(triggersFolder);
                        }
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
                    else if (type == "IndexesFolder")
                    {
                        var indexes = await DatabaseHelper.GetIndexesAsync(connStr, dbName, detailName);
                        foreach (var index in indexes)
                        {
                            string uniqueSuffix = index.IsUnique ? ", Unique" : "";
                            string pkSuffix = index.IsPrimaryKey ? ", PK" : "";
                            var indexItem = new TreeViewItem
                            {
                                Header = $"🔖 {index.IndexName} ({index.IndexType}{uniqueSuffix}{pkSuffix})",
                                Tag = new ObjectExplorerNode { NodeType = "Index", ConnectionString = connStr, DatabaseName = dbName, DetailName = index.IndexName }
                            };
                            item.Items.Add(indexItem);
                        }
                    }
                    else if (type == "TriggersFolder")
                    {
                        var triggers = await DatabaseHelper.GetTriggersAsync(connStr, dbName, detailName);
                        foreach (var trigger in triggers)
                        {
                            string disabledSuffix = trigger.IsDisabled ? " (Disabled)" : "";
                            var triggerItem = new TreeViewItem
                            {
                                Header = $"⚡ {trigger.TriggerName}{disabledSuffix}",
                                Tag = new ObjectExplorerNode { NodeType = "Trigger", ConnectionString = connStr, DatabaseName = dbName, DetailName = trigger.TriggerName }
                            };
                            triggerItem.ContextMenu = CreateObjectContextMenu(connStr, dbName, "Trigger", trigger.TriggerName);
                            item.Items.Add(triggerItem);
                        }
                    }
                }
                catch (Exception ex)
                {
                    item.Items.Add(new TreeViewItem { Header = $"⚠️ Error: {ex.Message}" });
                }
                finally
                {
                    loadingTimer.Stop();
                    item.Items.Remove(loadingItem);
                }
            }
        }

        private static (TreeViewItem Item, DispatcherTimer Timer) CreateAnimatedLoadingItem()
        {
            var loadingItem = new TreeViewItem
            {
                Header = "Loading.",
                IsHitTestVisible = false,
                Foreground = Brushes.Gray
            };
            int frame = 1;
            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(280)
            };
            timer.Tick += (_, _) =>
            {
                frame = frame % 3 + 1;
                loadingItem.Header = $"Loading{new string('.', frame)}";
            };
            timer.Start();
            return (loadingItem, timer);
        }

        private async void BtnRefreshObjectExplorer_Click(object sender, RoutedEventArgs e)
        {
            if (TreeObjectExplorer.SelectedItem is TreeViewItem selectedItem)
            {
                await RefreshObjectExplorerNodeAsync(selectedItem);
                return;
            }

            if (TreeObjectExplorer.Items.OfType<TreeViewItem>().FirstOrDefault() is TreeViewItem firstServer)
            {
                await RefreshObjectExplorerNodeAsync(firstServer);
            }
        }

        private void BtnSearchObjects_Click(object sender, RoutedEventArgs e)
        {
            string? connectionString = null;
            if (TreeObjectExplorer.SelectedItem is TreeViewItem selectedItem &&
                selectedItem.Tag is ObjectExplorerNode selectedNode)
            {
                connectionString = selectedNode.ConnectionString;
            }
            connectionString ??= TreeObjectExplorer.Items.OfType<TreeViewItem>()
                .Select(item => item.Tag)
                .OfType<ObjectExplorerNode>()
                .FirstOrDefault(node => node.NodeType == "Server")?.ConnectionString;

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                MessageBox.Show("Connect to a server before searching database objects.", "Object Search", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_objectSearchWindow != null)
            {
                _objectSearchWindow.Activate();
                return;
            }

            var serverOptions = TreeObjectExplorer.Items.OfType<TreeViewItem>()
                .Select(item => item.Tag)
                .OfType<ObjectExplorerNode>()
                .Where(node => node.NodeType == "Server")
                .Select(node => new ObjectSearchServerOption
                {
                    ServerName = new SqlConnectionStringBuilder(node.ConnectionString).DataSource,
                    ConnectionString = node.ConnectionString
                })
                .GroupBy(server => server.ConnectionString, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(server => server.ServerName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            string? targetDatabaseName = null;
            if (_useObjectExplorerContextForNewQuery)
            {
                targetDatabaseName = GetSelectedObjectExplorerDatabase();
            }
            if (string.IsNullOrEmpty(targetDatabaseName))
            {
                targetDatabaseName = (TabQueryControls.SelectedItem is TabItem tabItem && tabItem.Content is QueryTabControl activeTab)
                    ? activeTab.DatabaseName
                    : CboDatabases.SelectedItem?.ToString();
            }

            _objectSearchWindow = new ObjectSearchWindow(serverOptions, connectionString, targetDatabaseName) { Owner = this };
            _objectSearchWindow.OpenRequested += ObjectSearchWindow_OpenRequested;
            _objectSearchWindow.Closed += (_, _) => _objectSearchWindow = null;
            _objectSearchWindow.Show();
        }

        private async void ObjectSearchWindow_OpenRequested(object? sender, DatabaseObjectSearchResult result)
        {
            if (sender is not ObjectSearchWindow searchWindow)
            {
                return;
            }

            if (searchWindow.Owner is not MainWindow)
            {
                return;
            }
            string connectionString = searchWindow.ConnectionString;

            if (result.ObjectType is "Table" or "View")
            {
                string sql = $"SELECT TOP 200 * FROM {QuoteMultipartIdentifier(result.FullName)};";
                CreateNewQueryTab(connectionString, result.DatabaseName, sql, $"{result.FullName} (Top 200)");
            }
            else
            {
                await OpenObjectDefinitionFromEditorAsync(
                    connectionString,
                    result.DatabaseName,
                    result.ObjectType,
                    result.FullName);
            }
        }


        private async Task RefreshObjectExplorerNodeAsync(TreeViewItem item)
        {
            if (item.Tag is not ObjectExplorerNode node)
            {
                return;
            }

            _serverDatabasesCache.Remove(node.ConnectionString);
            var matchingTabs = TabQueryControls.Items.OfType<TabItem>()
                .Select(tab => tab.Content)
                .OfType<QueryTabControl>()
                .Where(tab => string.Equals(tab.ConnectionString, node.ConnectionString, StringComparison.OrdinalIgnoreCase) &&
                              (node.NodeType is "Server" or "DatabasesFolder" ||
                               string.Equals(tab.DatabaseName, node.DatabaseName, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            foreach (QueryTabControl tab in matchingTabs)
            {
                await tab.RefreshAutocompleteAsync();
            }

            if (CanReloadObjectExplorerNode(node.NodeType))
            {
                item.IsExpanded = false;
                item.Items.Clear();
                item.Items.Add(new TreeViewItem { Header = "Loading..." });
                item.IsExpanded = true;
            }

            UpdateStatusText($"Refreshed {node.NodeType}.");
        }

        private static bool CanReloadObjectExplorerNode(string nodeType)
        {
            return nodeType is "Server" or "DatabasesFolder" or "Database" or
                "TablesFolder" or "ViewsFolder" or "SpsFolder" or "FuncsFolder" or
                "ScalarFunctionsFolder" or "TableFunctionsFolder" or "Table" or "View" or
                "ColumnsFolder" or "IndexesFolder" or "TriggersFolder";
        }


        private void BtnToggleObjectExplorer_Click(object sender, RoutedEventArgs e)
        {
            ToggleObjectExplorer();
        }

        private void ToggleObjectExplorer()
        {
            if (_isObjectExplorerVisible)
            {
                if (ObjectExplorerColumn.ActualWidth > 0)
                {
                    _lastObjectExplorerWidth = ObjectExplorerColumn.Width;
                }

                ObjectExplorerPanel.Visibility = Visibility.Collapsed;
                ObjectExplorerSplitter.Visibility = Visibility.Collapsed;
                ObjectExplorerColumn.MinWidth = 0;
                ObjectExplorerColumn.Width = new GridLength(0);
                ObjectExplorerSplitterColumn.Width = new GridLength(0);
                BtnToggleObjectExplorer.ToolTip = "Show Object Explorer (F8)";
                _isObjectExplorerVisible = false;
            }
            else
            {
                ObjectExplorerColumn.MinWidth = 180;
                ObjectExplorerColumn.Width = _lastObjectExplorerWidth.Value > 0 ? _lastObjectExplorerWidth : new GridLength(260);
                ObjectExplorerSplitterColumn.Width = new GridLength(3);
                ObjectExplorerPanel.Visibility = Visibility.Visible;
                ObjectExplorerSplitter.Visibility = Visibility.Visible;
                BtnToggleObjectExplorer.ToolTip = "Hide Object Explorer (F8)";
                _isObjectExplorerVisible = true;
            }
        }

        private string GetFolderFilter(string filterScope, string folderType)
        {
            string key = $"{filterScope}_{folderType}";
            return _folderFilters.TryGetValue(key, out var val) ? val : string.Empty;
        }

        private string GetFolderHeader(string filterScope, string folderType, string baseName)
        {
            string filter = GetFolderFilter(filterScope, folderType);
            return string.IsNullOrEmpty(filter) ? $"📁 {baseName}" : $"📁 {baseName} (filtered: '{filter}')";
        }

        private void SetFilterableFolderHeader(
            TreeViewItem folderItem,
            string filterScope,
            string folderType,
            string baseName)
        {
            var header = new Grid
            {
                VerticalAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent
            };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            header.Children.Add(new TextBlock
            {
                Text = GetFolderHeader(filterScope, folderType, baseName),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            var filterButton = new Button
            {
                Content = new TextBlock
                {
                    Text = "\uE71C",
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 8
                },
                Width = 14,
                Height = 14,
                Margin = new Thickness(3, 0, 0, 0),
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = Brushes.LightGray,
                Cursor = Cursors.Hand,
                ToolTip = $"Filter {baseName}",
                Opacity = 0,
                IsHitTestVisible = false
            };
            filterButton.Click += (_, e) =>
            {
                e.Handled = true;
                OpenFilterDialog(folderItem, filterScope, folderType, baseName);
            };
            Grid.SetColumn(filterButton, 1);
            header.Children.Add(filterButton);
            header.MouseEnter += (_, _) =>
            {
                filterButton.Opacity = 1;
                filterButton.IsHitTestVisible = true;
            };
            header.MouseLeave += (_, _) =>
            {
                filterButton.Opacity = 0;
                filterButton.IsHitTestVisible = false;
            };
            folderItem.Header = header;
        }

        private ContextMenu CreateFolderContextMenu(TreeViewItem folderItem, string connStr, string dbName, string folderType, string baseName)
        {
            var menu = new ContextMenu();

            if (TryGetCreateObjectLabel(folderType, out string createLabel))
            {
                var createItem = new MenuItem { Header = $"Create New {createLabel}" };
                createItem.Click += (s, e) => CreateNewObjectScript(connStr, dbName, folderType);
                menu.Items.Add(createItem);
                menu.Items.Add(new Separator());
            }

            var newQueryItem = new MenuItem { Header = "New Query" };
            newQueryItem.Click += (s, e) => CreateNewQueryTab(connStr, dbName);
            menu.Items.Add(newQueryItem);

            var refreshItem = new MenuItem { Header = "Refresh" };
            refreshItem.Click += async (s, e) => await RefreshObjectExplorerNodeAsync(folderItem);
            menu.Items.Add(refreshItem);
            
            var filterItem = new MenuItem { Header = "Filter..." };
            filterItem.Click += (s, e) => OpenFilterDialog(folderItem, dbName, folderType, baseName);
            menu.Items.Add(filterItem);

            var clearFilterItem = new MenuItem { Header = "Clear Filter" };
            clearFilterItem.Click += (s, e) => ClearFilter(folderItem, dbName, folderType, baseName);
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

        private void CreateNewObjectScript(string connectionString, string databaseName, string folderType)
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
                CreateNewQueryTab(connectionString, databaseName, sql, $"Create {folderType.Replace("Folder", "")}.sql");
            }
        }

        private void OpenFilterDialog(TreeViewItem folderItem, string filterScope, string folderType, string baseName)
        {
            string key = $"{filterScope}_{folderType}";
            string currentFilter = GetFolderFilter(filterScope, folderType);
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

                SetFilterableFolderHeader(folderItem, filterScope, folderType, baseName);

                // Reload the folder
                folderItem.IsExpanded = false;
                folderItem.Items.Clear();
                folderItem.Items.Add(new TreeViewItem { Header = "Loading..." });
                folderItem.IsExpanded = true;
            }
        }

        private void ClearFilter(TreeViewItem folderItem, string filterScope, string folderType, string baseName)
        {
            string key = $"{filterScope}_{folderType}";
            if (_folderFilters.ContainsKey(key))
            {
                _folderFilters.Remove(key);
                SetFilterableFolderHeader(folderItem, filterScope, folderType, baseName);

                // Reload the folder
                folderItem.IsExpanded = false;
                folderItem.Items.Clear();
                folderItem.Items.Add(new TreeViewItem { Header = "Loading..." });
                folderItem.IsExpanded = true;
            }
        }

        private void OnTreeViewItemPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is TreeViewItem treeViewItem && e.OriginalSource is DependencyObject dep)
            {
                var clickedItem = FindAncestor<TreeViewItem>(dep);
                if (clickedItem == treeViewItem)
                {
                    treeViewItem.IsSelected = true;
                    treeViewItem.Focus();
                    EnsureCopyNameContextMenu(treeViewItem);
                }
                e.Handled = false;
            }
        }

        private void EnsureCopyNameContextMenu(TreeViewItem item)
        {
            if (item.Tag is not ObjectExplorerNode node)
            {
                return;
            }

            string copyName = GetObjectExplorerCopyName(node);
            if (string.IsNullOrWhiteSpace(copyName))
            {
                return;
            }

            item.ContextMenu ??= new ContextMenu();
            if (item.ContextMenu.Items.OfType<MenuItem>().Any(menuItem => Equals(menuItem.Tag, "CopyObjectName")))
            {
                return;
            }

            var copyNameItem = new MenuItem
            {
                Header = "Copy Name",
                Tag = "CopyObjectName"
            };
            copyNameItem.Click += (_, _) =>
            {
                try
                {
                    Clipboard.SetText(copyName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to copy name: {ex.Message}", "Copy Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };

            item.ContextMenu.Items.Insert(0, copyNameItem);
            if (item.ContextMenu.Items.Count > 1)
            {
                item.ContextMenu.Items.Insert(1, new Separator());
            }
        }

        private static string GetObjectExplorerCopyName(ObjectExplorerNode node)
        {
            if (!string.IsNullOrWhiteSpace(node.DetailName))
            {
                return node.DetailName;
            }

            return node.NodeType switch
            {
                "Server" => new SqlConnectionStringBuilder(node.ConnectionString).DataSource,
                "Database" => node.DatabaseName,
                "DatabasesFolder" => "Databases",
                "TablesFolder" => "Tables",
                "ViewsFolder" => "Views",
                "SpsFolder" => "Stored Procedures",
                "FuncsFolder" => "Functions",
                "ScalarFunctionsFolder" => "Scalar-valued Functions",
                "TableFunctionsFolder" => "Table-valued Functions",
                "ColumnsFolder" => "Columns",
                "IndexesFolder" => "Indexes",
                "TriggersFolder" => "Triggers",
                _ => string.Empty
            };
        }

    }
}