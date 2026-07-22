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

        private async void CboDatabases_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CboDatabases.SelectedItem == null) return;

            string selectedDb = CboDatabases.SelectedItem.ToString() ?? "master";
            if (TabQueryControls.SelectedItem is TabItem tabItem && tabItem.Content is QueryTabControl activeTab)
            {
                activeTab.DatabaseName = selectedDb;
                TxtStatusDatabase.Text = selectedDb;

                // Keep the compact title and expose connection context on hover.
                var builder = new SqlConnectionStringBuilder(activeTab.ConnectionString);
                string serverName = builder.DataSource;
                
                if (tabItem.Header is StackPanel headerPanel)
                {
                    headerPanel.ToolTip = $"Server: {serverName}{Environment.NewLine}Database: {selectedDb}";
                }

                // Cache metadata & update autocompletion suggestions
                await activeTab.CacheAndRefreshAutocompleteAsync();

                activeTab.FocusEditor();
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

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            var settingsWindow = new SettingsWindow
            {
                Owner = this
            };
            settingsWindow.ShowDialog();
        }

        private void BtnQueryHistory_Click(object sender, RoutedEventArgs e)
        {
            if (_queryHistoryWindow != null)
            {
                if (_queryHistoryWindow.WindowState == WindowState.Minimized)
                {
                    _queryHistoryWindow.WindowState = WindowState.Normal;
                }
                _queryHistoryWindow.Activate();
                return;
            }

            _queryHistoryWindow = new QueryHistoryWindow { Owner = this };
            _queryHistoryWindow.OpenInNewQueryRequested += (_, entry) => OpenHistoryEntryInNewQuery(entry);
            _queryHistoryWindow.Closed += (_, _) => _queryHistoryWindow = null;
            _queryHistoryWindow.Show();
        }

        private void OpenHistoryEntryInNewQuery(QueryHistoryEntry entry)
        {
            string? matchingConnectionString = TabQueryControls.Items
                .OfType<TabItem>()
                .Select(tab => tab.Content)
                .OfType<QueryTabControl>()
                .Select(tab => tab.ConnectionString)
                .FirstOrDefault(connectionString =>
                    string.Equals(
                        new SqlConnectionStringBuilder(connectionString).DataSource,
                        entry.ServerName,
                        StringComparison.OrdinalIgnoreCase));

            if (matchingConnectionString == null)
            {
                matchingConnectionString = TreeObjectExplorer.Items
                    .OfType<TreeViewItem>()
                    .Select(item => item.Tag)
                    .OfType<ObjectExplorerNode>()
                    .Where(node => node.NodeType == "Server")
                    .Select(node => node.ConnectionString)
                    .FirstOrDefault(connectionString =>
                        string.Equals(
                            new SqlConnectionStringBuilder(connectionString).DataSource,
                            entry.ServerName,
                            StringComparison.OrdinalIgnoreCase));
            }

            if (matchingConnectionString == null)
            {
                MessageBox.Show(
                    $"Connect to server '{entry.ServerName}' before opening this history entry.",
                    "Server Not Connected",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            string databaseName = string.IsNullOrWhiteSpace(entry.EffectiveDatabaseName)
                ? entry.StartedDatabaseName
                : entry.EffectiveDatabaseName;
            string tabTitle = $"History_{entry.ExecutedAtUtc.ToLocalTime():yyyyMMdd_HHmmss}.sql";
            CreateNewQueryTab(matchingConnectionString, databaseName, entry.QueryText, tabTitle);
            Activate();
        }

        private void ExecuteActiveTabQuery(QueryExecutionMode mode = QueryExecutionMode.Execute)
        {
            if (TabQueryControls.SelectedItem is TabItem tabItem && tabItem.Content is QueryTabControl activeTab)
            {
                activeTab.ExecuteQuery(mode);
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



        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F5 &&
                (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                ExecuteActiveTabQuery(QueryExecutionMode.Parse);
                e.Handled = true;
            }
            else if (e.Key == Key.F5)
            {
                ExecuteActiveTabQuery();
                e.Handled = true;
            }
            else if (e.Key == Key.L &&
                     (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control &&
                     (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
            {
                ExecuteActiveTabQuery(QueryExecutionMode.ActualPlan);
                e.Handled = true;
            }
            else if (e.Key == Key.L &&
                     (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                ExecuteActiveTabQuery(QueryExecutionMode.EstimatedPlan);
                e.Handled = true;
            }
            else if (e.Key == Key.F8)
            {
                ToggleObjectExplorer();
                e.Handled = true;
            }
            else if (e.Key == Key.R &&
                     (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control &&
                     (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
            {
                BtnRefreshObjectExplorer_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Key.F &&
                     (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control &&
                     (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
            {
                FormatActiveQuery();
                e.Handled = true;
            }
            else if (e.Key == Key.N && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                BtnNewQuery_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Key.S &&
                     (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control &&
                     (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
            {
                SaveActiveTabQuery(saveAs: true);
                e.Handled = true;
            }
            else if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                SaveActiveTabQuery();
                e.Handled = true;
            }
            else if (e.Key == Key.O && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                OpenSqlFile();
                e.Handled = true;
            }
            else if (e.Key == Key.K && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && (Keyboard.Modifiers & ModifierKeys.Shift) != ModifierKeys.Shift)
            {
                RunEditorCommand("commentSelection()");
                e.Handled = true;
            }
            else if (e.Key == Key.K && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
            {
                RunEditorCommand("uncommentSelection()");
                e.Handled = true;
            }
        }

        private void BtnNewQuery_Click(object sender, RoutedEventArgs e)
        {
            CreateNewQueryFromCurrentContext();
        }

        private void BtnQueryTools_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button) return;

            var menu = new ContextMenu();
            var parseItem = new MenuItem { Header = "Parse / Check Syntax", InputGestureText = "Ctrl+F5" };
            parseItem.Click += (_, _) => ExecuteActiveTabQuery(QueryExecutionMode.Parse);
            menu.Items.Add(parseItem);

            var estimatedItem = new MenuItem { Header = "Display Estimated Plan", InputGestureText = "Ctrl+L" };
            estimatedItem.Click += (_, _) => ExecuteActiveTabQuery(QueryExecutionMode.EstimatedPlan);
            menu.Items.Add(estimatedItem);

            var actualItem = new MenuItem { Header = "Execute with Actual Plan", InputGestureText = "Ctrl+Alt+L" };
            actualItem.Click += (_, _) => ExecuteActiveTabQuery(QueryExecutionMode.ActualPlan);
            menu.Items.Add(actualItem);

            menu.Items.Add(new Separator());
            var formatItem = new MenuItem { Header = "Format SQL", InputGestureText = "Ctrl+Shift+F" };
            formatItem.Click += (_, _) => FormatActiveQuery();
            menu.Items.Add(formatItem);

            button.ContextMenu = menu;
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }

        public void CreateNewQueryFromCurrentContext()
        {
            if (_useObjectExplorerContextForNewQuery &&
                TryGetSelectedObjectExplorerContext(out string connectionString, out string databaseName))
            {
                CreateNewQueryTab(connectionString, databaseName);
                return;
            }

            if (TabQueryControls.SelectedItem is TabItem activeTabItem && activeTabItem.Content is QueryTabControl activeTab)
            {
                CreateNewQueryTab(activeTab.ConnectionString, activeTab.DatabaseName);
            }
            else
            {
                CreateNewQueryTab(_initialConnectionString, "master");
            }
        }

        private async void TabQueryControls_SelectionChanged(object sender, RoutedEventArgs e)
        {
            if (e.Source != TabQueryControls) return;
            _useObjectExplorerContextForNewQuery = false;

            try
            {
                if (TabQueryControls.SelectedItem is TabItem tabItem && tabItem.Content is QueryTabControl activeTab)
                {
                    await activeTab.EditorReady;
                    activeTab.FocusEditor();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error activating tab: {ex.Message}");
            }
        }

        private void FormatActiveQuery()
        {
            if (TabQueryControls.SelectedItem is TabItem tabItem && tabItem.Content is QueryTabControl activeTab)
            {
                activeTab.FormatSql();
            }
        }

        private bool TryGetSelectedObjectExplorerContext(
            out string connectionString,
            out string databaseName)
        {
            connectionString = string.Empty;
            databaseName = string.Empty;

            if (TreeObjectExplorer.SelectedItem is not TreeViewItem selectedItem ||
                selectedItem.Tag is not ObjectExplorerNode node ||
                string.IsNullOrWhiteSpace(node.ConnectionString))
            {
                return false;
            }

            connectionString = node.ConnectionString;
            databaseName = node.NodeType is "Server" or "DatabasesFolder"
                ? "master"
                : string.IsNullOrWhiteSpace(node.DatabaseName) ? "master" : node.DatabaseName;
            return true;
        }

        private void BtnInsertScript_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                var menu = new ContextMenu();
                
                var scopeIdentityItem = new MenuItem { Header = "Scope Identity (DECLARE @NewID = SCOPE_IDENTITY())" };
                scopeIdentityItem.Click += (s, ev) =>
                {
                    if (TabQueryControls.SelectedItem is TabItem tabItem && tabItem.Content is QueryTabControl activeTab)
                    {
                        activeTab.InsertText("DECLARE @NewID BIGINT = SCOPE_IDENTITY();");
                    }
                };
                menu.Items.Add(scopeIdentityItem);

                var getDateItem = new MenuItem { Header = "Get Date ('YYYY-MM-DD HH:mm:ss')" };
                getDateItem.Click += (s, ev) =>
                {
                    if (TabQueryControls.SelectedItem is TabItem tabItem && tabItem.Content is QueryTabControl activeTab)
                    {
                        string dateStr = $"'{DateTime.Now:yyyy-MM-dd HH:mm:ss}'";
                        activeTab.InsertText(dateStr);
                    }
                };
                menu.Items.Add(getDateItem);

                menu.PlacementTarget = btn;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                menu.IsOpen = true;
            }
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
                    _draggedToolbarGrabOffsetX = _toolbarDragStartPoint.X - GetLayoutPosition(border, ToolbarPanel).X;
                    Panel.SetZIndex(border, 1000);
                    border.CaptureMouse();
                    AnimateOpacity(border, 0.7);
                }
            }
        }

        private void ToolbarItem_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_draggedToolbarItem != null && e.LeftButton == MouseButtonState.Pressed)
            {
                var currentPoint = e.GetPosition(ToolbarPanel);
                MoveDraggedElementWithCursor(_draggedToolbarItem, ToolbarPanel, currentPoint, _draggedToolbarGrabOffsetX);
                
                if (Math.Abs(currentPoint.X - _toolbarDragStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance)
                {
                    var toolbarItems = ToolbarPanel.Children.OfType<FrameworkElement>().ToList();
                    int sourceIndex = toolbarItems.IndexOf(_draggedToolbarItem);
                    int targetIndex = GetReorderTargetIndex(toolbarItems, _draggedToolbarItem, currentPoint, sourceIndex, ToolbarPanel);

                    if (sourceIndex >= 0 && targetIndex >= 0)
                    {
                        var oldPositions = CaptureElementPositions(toolbarItems, ToolbarPanel, _draggedToolbarItem);
                        ToolbarPanel.Children.RemoveAt(sourceIndex);
                        ToolbarPanel.Children.Insert(targetIndex, _draggedToolbarItem);
                        AnimateElementsToNewPositions(oldPositions, ToolbarPanel);
                    }
                }
            }
        }

        private void ToolbarItem_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_draggedToolbarItem != null)
            {
                AnimateOpacity(_draggedToolbarItem, 1.0);
                AnimateDraggedElementToSlot(_draggedToolbarItem);
                Panel.SetZIndex(_draggedToolbarItem, 0);
                _draggedToolbarItem.ReleaseMouseCapture();
                _draggedToolbarItem = null;
                SaveToolbarOrder();
            }
        }

    }
}