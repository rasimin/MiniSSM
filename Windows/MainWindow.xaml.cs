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
        private readonly string _initialConnectionString;
        private readonly TaskCompletionSource<bool> _startupCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _queryTabCounter = 0;

        public Task StartupCompletion => _startupCompletion.Task;

        // Cache databases list per server connection string to make tab switching instant
        private readonly Dictionary<string, List<string>> _serverDatabasesCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _folderFilters = new(StringComparer.OrdinalIgnoreCase);
        private TabItem? _draggedTab;
        private int _lastTargetIndex = -1;
        private Point _dragStartPoint;
        private Point _dragCurrentPoint;
        private double _draggedTabGrabOffsetX;
        private Border? _draggedToolbarItem;
        private Point _toolbarDragStartPoint;
        private double _draggedToolbarGrabOffsetX;
        private GridLength _lastObjectExplorerWidth = new(260);
        private bool _isObjectExplorerVisible = true;
        private bool _useObjectExplorerContextForNewQuery;
        private bool _allowWindowClose;
        private bool _isCloseConfirmationInProgress;
        private QueryHistoryWindow? _queryHistoryWindow;
        private ObjectSearchWindow? _objectSearchWindow;

        private static readonly Duration ReorderAnimationDuration = new(TimeSpan.FromMilliseconds(320));
        private const string QueryTabDragHandleTag = "QueryTabDragHandle";

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int WM_MOUSEHWHEEL = 0x020E;
        private HwndSource? _windowSource;

        public MainWindow(string connectionString)
        {
            InitializeComponent();
            _initialConnectionString = connectionString;
            ApplyToolbarOrder();

            // Connect TreeView expanded event handler
            TreeObjectExplorer.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler(TreeItem_Expanded));
            TreeObjectExplorer.SelectedItemChanged += (_, _) => _useObjectExplorerContextForNewQuery = true;
            TreeObjectExplorer.PreviewMouseDown += (_, _) => _useObjectExplorerContextForNewQuery = true;
        }

        private void ApplyToolbarOrder()
        {
            Border[] defaultItems =
            {
                ToolbarConnect,
                ToolbarDisconnect,
                ToolbarObjectExplorer,
                ToolbarNewQuery,
                ToolbarSave,
                ToolbarDatabase,
                ToolbarExecute,
                ToolbarComment,
                ToolbarUncomment,
                ToolbarSaveAs,
                ToolbarOpen,
                ToolbarQueryHistory,
                ToolbarInsertScript,
                ToolbarQueryTools
            };

            var itemsByName = defaultItems.ToDictionary(item => item.Name, StringComparer.Ordinal);
            var orderedItems = new List<Border>();
            foreach (string itemName in AppSettings.Current.Ui.ToolbarOrder)
            {
                if (itemsByName.Remove(itemName, out Border? item))
                {
                    orderedItems.Add(item);
                }
            }

            foreach (Border item in defaultItems)
            {
                if (itemsByName.Remove(item.Name))
                {
                    orderedItems.Add(item);
                }
            }

            ToolbarPanel.Children.Clear();
            foreach (Border item in orderedItems)
            {
                ToolbarPanel.Children.Add(item);
            }
        }

        private void SaveToolbarOrder()
        {
            try
            {
                AppSettings.Current.Ui.ToolbarOrder = ToolbarPanel.Children
                    .OfType<FrameworkElement>()
                    .Select(item => item.Name)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .ToList();
                AppSettings.Save(AppSettings.Current);
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "Failed to save toolbar order.");
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try
            {
                IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                _windowSource = HwndSource.FromHwnd(hwnd);
                _windowSource?.AddHook(WindowMessageHook);
                int darkMode = 1; // 1 = Enable
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
            }
            catch
            {
                // Ignore DWM failures
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _windowSource?.RemoveHook(WindowMessageHook);
            _windowSource = null;
            base.OnClosed(e);
        }

        protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (_allowWindowClose)
            {
                base.OnClosing(e);
                return;
            }

            e.Cancel = true;
            base.OnClosing(e);
            if (_isCloseConfirmationInProgress)
            {
                return;
            }

            _isCloseConfirmationInProgress = true;
            try
            {
                var dirtyTabs = TabQueryControls.Items.OfType<TabItem>()
                    .Where(t => t.Content is QueryTabControl q && q.IsDirty)
                    .ToList();

                bool skipRemainingUnsaved = false;

                foreach (TabItem tab in TabQueryControls.Items.OfType<TabItem>().ToList())
                {
                    if (!skipRemainingUnsaved && tab.Content is QueryTabControl q && q.IsDirty)
                    {
                        bool showDontSaveAll = dirtyTabs.Count > 1;
                        var choice = await ConfirmTabCanCloseChoiceAsync(tab, showDontSaveAll);
                        if (choice == UnsavedChangesChoice.Cancel)
                        {
                            return;
                        }
                        if (choice == UnsavedChangesChoice.DontSaveAll)
                        {
                            skipRemainingUnsaved = true;
                        }
                        dirtyTabs.Remove(tab);
                    }
                }

                _allowWindowClose = true;
                _ = Dispatcher.BeginInvoke(
                    new Action(Close),
                    DispatcherPriority.Background);
            }
            finally
            {
                _isCloseConfirmationInProgress = false;
            }
        }

        private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message != WM_MOUSEHWHEEL || Mouse.DirectlyOver is not DependencyObject element)
            {
                return IntPtr.Zero;
            }

            var scrollViewer = FindHorizontalScrollViewer(element);
            if (scrollViewer == null)
            {
                return IntPtr.Zero;
            }

            int delta = unchecked((short)((wParam.ToInt64() >> 16) & 0xFFFF));
            double pixelsPerNotch = Math.Max(16, SystemParameters.WheelScrollLines * 16);
            double targetOffset = scrollViewer.HorizontalOffset + (delta / 120.0 * pixelsPerNotch);
            scrollViewer.ScrollToHorizontalOffset(Math.Clamp(targetOffset, 0, scrollViewer.ScrollableWidth));
            handled = true;
            return IntPtr.Zero;
        }

        private static ScrollViewer? FindHorizontalScrollViewer(DependencyObject element)
        {
            DependencyObject? current = element;
            while (current != null)
            {
                if (current is ScrollViewer viewer && viewer.ScrollableWidth > 0)
                {
                    return viewer;
                }

                current = current is Visual
                    ? VisualTreeHelper.GetParent(current)
                    : LogicalTreeHelper.GetParent(current);
            }

            return null;
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
                QueryTabControl firstTab = CreateNewQueryTab(_initialConnectionString, initialDb);
                await firstTab.EditorReady;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error initializing application: {ex.Message}", "Initialization Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                await Dispatcher.Yield(DispatcherPriority.Render);
                _startupCompletion.TrySetResult(true);
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

        public void UpdateStatusRowsAndColumns(int rows, int cols)
        {
            TxtStatusRows.Text = $"{rows} rows";
            TxtStatusColumns.Text = $"{cols} columns";
        }

        #endregion

        #region Tab Management

        public QueryTabControl CreateNewQueryTab(string connectionString, string databaseName, string? initialSql = null, string? customTabTitle = null, bool autoExecute = false, string? filePath = null)
        {
            _queryTabCounter++;

            var builder = new SqlConnectionStringBuilder(connectionString);
            string serverName = builder.DataSource;
            string tabTitle = customTabTitle ?? $"SQLQuery{_queryTabCounter}.sql";
            AppLogger.Info($"Creating query tab: {tabTitle}");

            var queryTabControl = new QueryTabControl(connectionString, databaseName);
            queryTabControl.EditorActivated += (_, _) => _useObjectExplorerContextForNewQuery = false;
            queryTabControl.FilePath = filePath;
            if (!string.IsNullOrEmpty(initialSql))
            {
                queryTabControl.InitialSql = initialSql;
            }
            queryTabControl.AutoExecute = autoExecute;

            var tabItem = new TabItem();
            queryTabControl.DirtyStateChanged += (_, _) => UpdateTabDirtyIndicator(tabItem);
            tabItem.PreviewMouseLeftButtonDown += TabItem_PreviewMouseLeftButtonDown;
            tabItem.PreviewMouseMove += TabItem_PreviewMouseMove;
            tabItem.PreviewMouseLeftButtonUp += TabItem_PreviewMouseLeftButtonUp;

            // Build tab header panel with close button
            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal, Tag = QueryTabDragHandleTag };
            headerPanel.ToolTip = $"Server: {serverName}{Environment.NewLine}Database: {databaseName}";
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

            closeBtn.Click += async (s, e) =>
            {
                e.Handled = true;
                await CloseTabAsync(tabItem);
            };


            headerPanel.Children.Add(headerText);
            headerPanel.Children.Add(closeBtn);

            tabItem.Header = headerPanel;
            tabItem.Content = queryTabControl;

            // Context Menu for Tab Header (Rename & Set Color)
            var tabContextMenu = new ContextMenu();
            var renameTabMenu = new MenuItem { Header = "Rename" };
            renameTabMenu.Click += (s, e) => RenameTab(tabItem);
            tabContextMenu.Items.Add(renameTabMenu);

            var closeTabMenu = new MenuItem { Header = "Close" };
            closeTabMenu.Click += async (s, e) => await CloseTabAsync(tabItem);
            tabContextMenu.Items.Add(closeTabMenu);

            var closeAllMenu = new MenuItem { Header = "Close All" };
            closeAllMenu.Click += async (s, e) => await CloseAllTabsAsync();
            tabContextMenu.Items.Add(closeAllMenu);

            var closeAllButThisMenu = new MenuItem { Header = "Close All But This" };
            closeAllButThisMenu.Click += async (s, e) => await CloseAllTabsExceptAsync(tabItem);
            tabContextMenu.Items.Add(closeAllButThisMenu);

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

            TabQueryControls.Items.Insert(0, tabItem);
            TabQueryControls.SelectedItem = tabItem;
            return queryTabControl;
        }

        private void RenameTab(TabItem tabItem)
        {
            if (tabItem.Header is StackPanel headerPanel && headerPanel.Children[0] is TextBlock headerText)
            {
                string newName = ShowInputDialog("Rename Tab", "Enter new tab name:", GetCleanTabHeaderText(tabItem));
                if (!string.IsNullOrEmpty(newName))
                {
                    headerText.Text = newName +
                        (tabItem.Content is QueryTabControl queryTab && queryTab.IsDirty ? " *" : string.Empty);
                }
            }
        }

        private async Task<bool> CloseTabAsync(TabItem tabItem, bool skipConfirmation = false)
        {
            if (!TabQueryControls.Items.Contains(tabItem))
            {
                return true;
            }

            if (!skipConfirmation)
            {
                var choice = await ConfirmTabCanCloseChoiceAsync(tabItem, showDontSaveAll: false);
                if (choice == UnsavedChangesChoice.Cancel)
                {
                    return false;
                }
            }

            AppLogger.Info($"Closing query tab: {GetTabHeaderText(tabItem)}");
            if (tabItem.Content is QueryTabControl queryTab)
            {
                queryTab.DisposeResources();
            }
            TabQueryControls.Items.Remove(tabItem);
            if (TabQueryControls.Items.Count == 0)
            {
                CboDatabases.ItemsSource = null;
                TxtStatusDatabase.Text = "";
                TxtStatusServer.Text = "No Connection";
                TxtStatusTime.Text = "";
                UpdateStatusRowsAndColumns(0, 0);
            }
            return true;
        }

        private string GetTabHeaderText(TabItem tabItem)
        {
            if (tabItem.Header is StackPanel headerPanel && headerPanel.Children[0] is TextBlock headerText)
            {
                return headerText.Text;
            }
            return tabItem.Header?.ToString() ?? "(untitled)";
        }

        private string GetCleanTabHeaderText(TabItem tabItem)
        {
            string title = GetTabHeaderText(tabItem);
            return title.EndsWith(" *", StringComparison.Ordinal) ? title[..^2] : title;
        }

        private void UpdateTabDirtyIndicator(TabItem tabItem)
        {
            if (tabItem.Header is not StackPanel headerPanel ||
                headerPanel.Children[0] is not TextBlock headerText ||
                tabItem.Content is not QueryTabControl queryTab)
            {
                return;
            }

            string cleanTitle = headerText.Text.EndsWith(" *", StringComparison.Ordinal)
                ? headerText.Text[..^2]
                : headerText.Text;
            headerText.Text = cleanTitle + (queryTab.IsDirty ? " *" : string.Empty);
        }

        private async Task<bool> CloseAllTabsAsync()
        {
            var dirtyTabs = TabQueryControls.Items.OfType<TabItem>()
                .Where(t => t.Content is QueryTabControl q && q.IsDirty)
                .ToList();

            bool skipRemainingUnsaved = false;

            foreach (TabItem item in TabQueryControls.Items.OfType<TabItem>().ToList())
            {
                if (!skipRemainingUnsaved && item.Content is QueryTabControl q && q.IsDirty)
                {
                    bool showDontSaveAll = dirtyTabs.Count > 1;
                    var choice = await ConfirmTabCanCloseChoiceAsync(item, showDontSaveAll);
                    if (choice == UnsavedChangesChoice.Cancel)
                    {
                        return false;
                    }
                    if (choice == UnsavedChangesChoice.DontSaveAll)
                    {
                        skipRemainingUnsaved = true;
                    }
                    dirtyTabs.Remove(item);
                }

                if (!await CloseTabAsync(item, skipConfirmation: true))
                {
                    return false;
                }
            }
            return true;
        }

        private async Task CloseAllTabsExceptAsync(TabItem tabItem)
        {
            var targetTabs = TabQueryControls.Items.OfType<TabItem>().Where(item => item != tabItem).ToList();
            var dirtyTabs = targetTabs.Where(t => t.Content is QueryTabControl q && q.IsDirty).ToList();

            bool skipRemainingUnsaved = false;

            foreach (var item in targetTabs)
            {
                if (!skipRemainingUnsaved && item.Content is QueryTabControl q && q.IsDirty)
                {
                    bool showDontSaveAll = dirtyTabs.Count > 1;
                    var choice = await ConfirmTabCanCloseChoiceAsync(item, showDontSaveAll);
                    if (choice == UnsavedChangesChoice.Cancel)
                    {
                        return;
                    }
                    if (choice == UnsavedChangesChoice.DontSaveAll)
                    {
                        skipRemainingUnsaved = true;
                    }
                    dirtyTabs.Remove(item);
                }

                if (!await CloseTabAsync(item, skipConfirmation: true))
                {
                    return;
                }
            }
            TabQueryControls.SelectedItem = tabItem;
        }

        private async void TabQueryControls_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.Source != TabQueryControls) return;
            _useObjectExplorerContextForNewQuery = false;

            try
            {
                if (TabQueryControls.SelectedItem is TabItem tabItem && tabItem.Content is QueryTabControl activeTab)
                {
                    var builder = new SqlConnectionStringBuilder(activeTab.ConnectionString);
                    string serverName = builder.DataSource;

                    TxtStatusServer.Text = $"Connected: {serverName}";
                    TxtStatusDatabase.Text = activeTab.DatabaseName;

                    await SyncDatabaseContextAsync(activeTab);

                    UpdateStatusRowsAndColumns(activeTab.TotalResultRows, activeTab.TotalResultColumns);
                    activeTab.FocusEditor();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "Tab selection changed failed");
                MessageBox.Show($"Failed to switch query tab. Log saved to: {AppLogger.LogDirectory}", "Tab Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public async Task SyncDatabaseContextAsync(QueryTabControl queryTab)
        {
            if (TabQueryControls.Items.OfType<TabItem>().FirstOrDefault(tab => tab.Content == queryTab) is TabItem ownerTab &&
                ownerTab.Header is StackPanel headerPanel)
            {
                var builder = new SqlConnectionStringBuilder(queryTab.ConnectionString);
                headerPanel.ToolTip = $"Server: {builder.DataSource}{Environment.NewLine}Database: {queryTab.DatabaseName}";
            }

            if (TabQueryControls.SelectedItem is not TabItem selectedTab || selectedTab.Content != queryTab)
            {
                return;
            }

            TxtStatusDatabase.Text = queryTab.DatabaseName;
            CboDatabases.SelectionChanged -= CboDatabases_SelectionChanged;
            try
            {
                if (!_serverDatabasesCache.TryGetValue(queryTab.ConnectionString, out var databases))
                {
                    databases = await DatabaseHelper.GetDatabasesAsync(queryTab.ConnectionString);
                    _serverDatabasesCache[queryTab.ConnectionString] = databases;
                }
                CboDatabases.ItemsSource = databases;
                CboDatabases.SelectedItem = queryTab.DatabaseName;
            }
            finally
            {
                CboDatabases.SelectionChanged += CboDatabases_SelectionChanged;
            }
        }

        #endregion

        #region TreeView (Object Explorer) Loading & Navigation

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

                // Keep the compact title and expose connection context on hover.
                var builder = new SqlConnectionStringBuilder(activeTab.ConnectionString);
                string serverName = builder.DataSource;
                
                if (tabItem.Header is StackPanel headerPanel)
                {
                    headerPanel.ToolTip = $"Server: {serverName}{Environment.NewLine}Database: {selectedDb}";
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

        #endregion

        #region Global Hotkeys

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

        private void BtnSaveQuery_Click(object sender, RoutedEventArgs e)
        {
            SaveActiveTabQuery();
        }

        private void BtnSaveAsQuery_Click(object sender, RoutedEventArgs e)
        {
            SaveActiveTabQuery(saveAs: true);
        }

        private void BtnOpenQuery_Click(object sender, RoutedEventArgs e)
        {
            OpenSqlFile();
        }

        private void BtnCommentQuery_Click(object sender, RoutedEventArgs e)
        {
            RunEditorCommand("commentSelection()");
        }

        private void BtnUncommentQuery_Click(object sender, RoutedEventArgs e)
        {
            RunEditorCommand("uncommentSelection()");
        }

        private async void RunEditorCommand(string script)
        {
            if (TabQueryControls.SelectedItem is TabItem tabItem && tabItem.Content is QueryTabControl activeTab && activeTab.IsWebViewInitialized)
            {
                try
                {
                    await activeTab.SqlEditorWebView.ExecuteScriptAsync(script);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to update SQL editor: {ex.Message}", "Editor Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private void OpenSqlFile()
        {
            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "SQL Files (*.sql)|*.sql|All Files (*.*)|*.*",
                DefaultExt = ".sql",
                Title = "Open SQL Query",
                Multiselect = true
            };

            if (openFileDialog.ShowDialog() != true)
            {
                return;
            }

            OpenSqlFiles(openFileDialog.FileNames);
        }

        private void Window_PreviewDragOver(object sender, DragEventArgs e)
        {
            bool containsSqlFiles = e.Data.GetDataPresent(DataFormats.FileDrop) &&
                e.Data.GetData(DataFormats.FileDrop) is string[] paths &&
                paths.Any(path => string.Equals(Path.GetExtension(path), ".sql", StringComparison.OrdinalIgnoreCase));

            e.Effects = containsSqlFiles ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
            {
                return;
            }

            string[] sqlFiles = paths
                .Where(path => string.Equals(Path.GetExtension(path), ".sql", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (sqlFiles.Length > 0)
            {
                OpenSqlFiles(sqlFiles);
                e.Handled = true;
            }
        }

        private void OpenSqlFiles(IEnumerable<string> filePaths)
        {
            string connectionString = _initialConnectionString;
            string databaseName = "master";

            if (TabQueryControls.SelectedItem is TabItem activeTabItem && activeTabItem.Content is QueryTabControl activeTab)
            {
                connectionString = activeTab.ConnectionString;
                databaseName = activeTab.DatabaseName;
            }
            else
            {
                var builder = new SqlConnectionStringBuilder(_initialConnectionString);
                if (!string.IsNullOrEmpty(builder.InitialCatalog))
                {
                    databaseName = builder.InitialCatalog;
                }
            }

            foreach (string filePath in filePaths)
            {
                try
                {
                    string fullPath = Path.GetFullPath(filePath);
                    string sql = File.ReadAllText(fullPath);
                    CreateNewQueryTab(connectionString, databaseName, sql, Path.GetFileName(fullPath), filePath: fullPath);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to open SQL file '{Path.GetFileName(filePath)}': {ex.Message}", "Open Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void SaveActiveTabQuery(bool saveAs = false)
        {
            if (TabQueryControls.SelectedItem is TabItem tabItem)
            {
                await SaveQueryTabAsync(tabItem, saveAs);
            }
        }

        private async Task<bool> SaveQueryTabAsync(TabItem tabItem, bool saveAs)
        {
            if (tabItem.Content is not QueryTabControl queryTab)
            {
                return true;
            }

            await queryTab.EditorReady;
            string? targetPath = saveAs ? null : queryTab.FilePath;
            if (string.IsNullOrEmpty(targetPath))
            {
                string defaultFileName = queryTab.FilePath == null
                    ? GetCleanTabHeaderText(tabItem)
                    : Path.GetFileName(queryTab.FilePath);
                foreach (char invalidCharacter in Path.GetInvalidFileNameChars())
                {
                    defaultFileName = defaultFileName.Replace(invalidCharacter, '_');
                }

                var saveFileDialog = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "SQL Files (*.sql)|*.sql|All Files (*.*)|*.*",
                    DefaultExt = ".sql",
                    AddExtension = true,
                    Title = saveAs ? "Save SQL Query As" : "Save SQL Query",
                    FileName = defaultFileName
                };

                if (saveFileDialog.ShowDialog() != true)
                {
                    return false;
                }

                targetPath = saveFileDialog.FileName;
            }

            try
            {
                string resultJson = await queryTab.SqlEditorWebView.ExecuteScriptAsync("getAllQueryText()");
                string sqlQuery = JsonSerializer.Deserialize<string>(resultJson) ?? "";
                File.WriteAllText(targetPath, sqlQuery);
                queryTab.FilePath = targetPath;

                if (tabItem.Header is StackPanel headerPanel && headerPanel.Children[0] is TextBlock textBlock)
                {
                    textBlock.Text = Path.GetFileName(targetPath);
                }
                queryTab.MarkSaved(sqlQuery);
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save query file: {ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private async Task<UnsavedChangesChoice> ConfirmTabCanCloseChoiceAsync(TabItem tabItem, bool showDontSaveAll = false)
        {
            if (tabItem.Content is not QueryTabControl queryTab || !queryTab.IsDirty)
            {
                return UnsavedChangesChoice.DontSave;
            }

            var dialog = new UnsavedChangesWindow(GetCleanTabHeaderText(tabItem), showDontSaveAll)
            {
                Owner = this
            };
            dialog.ShowDialog();

            if (dialog.Choice == UnsavedChangesChoice.Save)
            {
                bool saved = await SaveQueryTabAsync(tabItem, saveAs: false);
                return saved ? UnsavedChangesChoice.Save : UnsavedChangesChoice.Cancel;
            }

            return dialog.Choice;
        }

        private async Task<bool> ConfirmTabCanCloseAsync(TabItem tabItem)
        {
            var choice = await ConfirmTabCanCloseChoiceAsync(tabItem, showDontSaveAll: false);
            return choice != UnsavedChangesChoice.Cancel;
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

        private void TabItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _useObjectExplorerContextForNewQuery = false;

            if (sender is TabItem tabItem)
            {
                if (!IsQueryTabDragHandle(e.OriginalSource as DependencyObject))
                {
                    CancelTabDrag();
                    return;
                }

                if (IsInteractiveTabChild(e.OriginalSource as DependencyObject))
                {
                    return;
                }

                if (e.ClickCount == 2)
                {
                    RenameTab(tabItem);
                    e.Handled = true;
                    return;
                }

                _draggedTab = tabItem;
                _dragStartPoint = e.GetPosition(TabQueryControls);
                _dragCurrentPoint = _dragStartPoint;
                _draggedTabGrabOffsetX = _dragStartPoint.X - GetLayoutPosition(tabItem, TabQueryControls).X;
                Panel.SetZIndex(tabItem, 1000);
            }
        }

        private void TabItem_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_draggedTab != null && e.LeftButton != MouseButtonState.Pressed)
            {
                CancelTabDrag();
                return;
            }

            if (_draggedTab != null && e.LeftButton == MouseButtonState.Pressed)
            {
                var currentPoint = e.GetPosition(TabQueryControls);
                _dragCurrentPoint = currentPoint;
                double horizontalDelta = Math.Abs(currentPoint.X - _dragStartPoint.X);

                if (horizontalDelta > SystemParameters.MinimumHorizontalDragDistance)
                {
                    if (!_draggedTab.IsMouseCaptured)
                    {
                        _draggedTab.CaptureMouse();
                    }
                    MoveDraggedElementWithCursor(_draggedTab, TabQueryControls, currentPoint, _draggedTabGrabOffsetX);

                    var tabItems = TabQueryControls.Items.OfType<TabItem>().Cast<FrameworkElement>().ToList();
                    int sourceIndex = tabItems.IndexOf(_draggedTab);
                    int targetIndex = GetReorderTargetIndex(tabItems, _draggedTab, currentPoint, sourceIndex, TabQueryControls);

                    UpdateTabVisualPositions(_draggedTab, sourceIndex, targetIndex);
                }
            }
        }

        private void TabItem_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            CancelTabDrag(animateToSlot: true);
        }

        private void CancelTabDrag(bool animateToSlot = false)
        {
            var tab = _draggedTab;
            _draggedTab = null;
            if (tab == null)
            {
                return;
            }

            try
            {
                if (animateToSlot && tab.IsMouseCaptured)
                {
                    CommitTabDrag(tab);
                }
                else
                {
                    // Reset all tab translations if drag was cancelled without drop (e.g. mouse lost capture)
                    foreach (var item in TabQueryControls.Items.OfType<TabItem>())
                    {
                        if (item.RenderTransform is TranslateTransform transform)
                        {
                            transform.BeginAnimation(TranslateTransform.XProperty, null);
                            transform.X = 0;
                        }
                    }
                    _lastTargetIndex = -1;

                    if (animateToSlot)
                    {
                        AnimateDraggedElementToSlot(tab);
                    }
                }
                if (tab.IsMouseCaptured)
                {
                    tab.ReleaseMouseCapture();
                }
                Panel.SetZIndex(tab, 0);
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "Failed to cancel tab drag");
            }
        }

        private void CommitTabDrag(TabItem tab)
        {
            var tabItems = TabQueryControls.Items.OfType<TabItem>().Cast<FrameworkElement>().ToList();
            int sourceIndex = tabItems.IndexOf(tab);
            int targetIndex = GetReorderTargetIndex(tabItems, tab, _dragCurrentPoint, sourceIndex, TabQueryControls);

            // Get absolute position of the dragged tab before reordering
            Point oldAbsolutePos = new Point(0, 0);
            try
            {
                oldAbsolutePos = tab.TransformToAncestor(TabQueryControls).Transform(new Point(0, 0));
            }
            catch { }

            // Clear all translations of other tabs
            foreach (var item in tabItems)
            {
                if (item != tab && item.RenderTransform is TranslateTransform transform)
                {
                    transform.BeginAnimation(TranslateTransform.XProperty, null);
                    transform.X = 0;
                }
            }

            _lastTargetIndex = -1;

            if (sourceIndex >= 0 && targetIndex >= 0 && sourceIndex != targetIndex)
            {
                TabQueryControls.SelectionChanged -= TabQueryControls_SelectionChanged;
                try
                {
                    TabQueryControls.Items.RemoveAt(sourceIndex);
                    TabQueryControls.Items.Insert(targetIndex, tab);
                    TabQueryControls.SelectedItem = tab;
                }
                finally
                {
                    TabQueryControls.SelectionChanged += TabQueryControls_SelectionChanged;
                }

                // Force layout update so the new layout positions are calculated
                TabQueryControls.UpdateLayout();

                // Calculate the new layout position of the tab (which has TranslateTransform but we subtract it)
                Point newLayoutPos = GetLayoutPosition(tab, TabQueryControls);

                // Set the translation to keep the tab at the same visual position
                var tabTransform = GetOrCreateTranslateTransform(tab);
                tabTransform.BeginAnimation(TranslateTransform.XProperty, null);
                tabTransform.X = oldAbsolutePos.X - newLayoutPos.X;
            }

            AnimateDraggedElementToSlot(tab);
        }

        private void UpdateTabVisualPositions(TabItem draggedTab, int sourceIndex, int targetIndex)
        {
            if (targetIndex == -1)
            {
                targetIndex = sourceIndex;
            }

            if (targetIndex == _lastTargetIndex)
            {
                return;
            }
            _lastTargetIndex = targetIndex;

            var tabItems = TabQueryControls.Items.OfType<TabItem>().ToList();
            double draggedWidth = draggedTab.ActualWidth;

            for (int i = 0; i < tabItems.Count; i++)
            {
                var tab = tabItems[i];
                if (tab == draggedTab)
                {
                    continue;
                }

                double targetTranslationX = 0;

                if (sourceIndex < targetIndex)
                {
                    // Dragging right
                    if (i > sourceIndex && i <= targetIndex)
                    {
                        targetTranslationX = -draggedWidth;
                    }
                }
                else if (sourceIndex > targetIndex)
                {
                    // Dragging left
                    if (i >= targetIndex && i < sourceIndex)
                    {
                        targetTranslationX = draggedWidth;
                    }
                }

                var transform = GetOrCreateTranslateTransform(tab);
                var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
                transform.BeginAnimation(TranslateTransform.XProperty, 
                    new DoubleAnimation(targetTranslationX, TimeSpan.FromMilliseconds(200)) { EasingFunction = easing });
            }
        }

        private bool IsQueryTabDragHandle(DependencyObject? source)
        {
            while (source != null)
            {
                if (source is FrameworkElement element && Equals(element.Tag, QueryTabDragHandleTag))
                {
                    return true;
                }
                if (source is TabItem)
                {
                    return false;
                }
                source = VisualTreeHelper.GetParent(source);
            }
            return false;
        }

        private bool IsInteractiveTabChild(DependencyObject? source)
        {
            while (source != null)
            {
                if (source is Button || source is TextBox || source is ComboBox || source is MenuItem)
                {
                    return true;
                }
                if (source is TabItem)
                {
                    return false;
                }
                source = VisualTreeHelper.GetParent(source);
            }
            return false;
        }

        private int GetReorderTargetIndex(
            IReadOnlyList<FrameworkElement> elements,
            FrameworkElement draggedElement,
            Point cursorPosition,
            int sourceIndex,
            Visual relativeTo)
        {
            if (sourceIndex < 0)
            {
                return -1;
            }

            for (int i = 0; i < elements.Count; i++)
            {
                var element = elements[i];
                if (element == draggedElement || !element.IsVisible)
                {
                    continue;
                }

                Point position;
                try
                {
                    position = GetLayoutPosition(element, relativeTo);
                }
                catch
                {
                    continue;
                }

                double left = position.X;
                double right = left + element.ActualWidth;
                if (cursorPosition.X < left || cursorPosition.X > right)
                {
                    continue;
                }

                double threshold = sourceIndex < i
                    ? left + element.ActualWidth * 0.55
                    : left + element.ActualWidth * 0.45;

                if ((sourceIndex < i && cursorPosition.X >= threshold) ||
                    (sourceIndex > i && cursorPosition.X <= threshold))
                {
                    return i;
                }
            }

            return -1;
        }

        private Dictionary<FrameworkElement, Point> CaptureElementPositions(IEnumerable<FrameworkElement> elements, Visual relativeTo, FrameworkElement? excludedElement = null)
        {
            var positions = new Dictionary<FrameworkElement, Point>();
            foreach (var element in elements)
            {
                if (element == excludedElement || !element.IsVisible)
                {
                    continue;
                }

                try
                {
                    positions[element] = GetLayoutPosition(element, relativeTo);
                }
                catch
                {
                    // Ignore elements while WPF is rebuilding the visual tree.
                }
            }
            return positions;
        }

        private void AnimateElementsToNewPositions(Dictionary<FrameworkElement, Point> oldPositions, Visual relativeTo)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                foreach (var (element, oldPosition) in oldPositions)
                {
                    if (!element.IsVisible)
                    {
                        continue;
                    }

                    Point newPosition;
                    try
                    {
                        newPosition = GetLayoutPosition(element, relativeTo);
                    }
                    catch
                    {
                        continue;
                    }

                    double offsetX = oldPosition.X - newPosition.X;
                    double offsetY = oldPosition.Y - newPosition.Y;
                    if (Math.Abs(offsetX) < 0.5 && Math.Abs(offsetY) < 0.5)
                    {
                        continue;
                    }

                    if (element.RenderTransform is not TranslateTransform transform)
                    {
                        transform = new TranslateTransform();
                        element.RenderTransform = transform;
                    }

                    transform.BeginAnimation(TranslateTransform.XProperty, null);
                    transform.BeginAnimation(TranslateTransform.YProperty, null);
                    transform.X = offsetX;
                    transform.Y = offsetY;

                    var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
                    transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, ReorderAnimationDuration) { EasingFunction = easing });
                    transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, ReorderAnimationDuration) { EasingFunction = easing });
                }
            }), DispatcherPriority.Render);
        }

        private void MoveDraggedElementWithCursor(FrameworkElement element, Visual relativeTo, Point cursorPosition, double grabOffsetX)
        {
            var layoutPosition = GetLayoutPosition(element, relativeTo);
            var transform = GetOrCreateTranslateTransform(element);

            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            transform.X = cursorPosition.X - grabOffsetX - layoutPosition.X;
            transform.Y = 0;
        }

        private void AnimateDraggedElementToSlot(FrameworkElement element)
        {
            if (element.RenderTransform is not TranslateTransform transform)
            {
                return;
            }

            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, ReorderAnimationDuration) { EasingFunction = easing });
            transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, ReorderAnimationDuration) { EasingFunction = easing });
        }

        private TranslateTransform GetOrCreateTranslateTransform(FrameworkElement element)
        {
            if (element.RenderTransform is TranslateTransform transform)
            {
                return transform;
            }

            transform = new TranslateTransform();
            element.RenderTransform = transform;
            return transform;
        }

        private Point GetLayoutPosition(FrameworkElement element, Visual relativeTo)
        {
            var point = element.TransformToAncestor(relativeTo).Transform(new Point(0, 0));
            if (element.RenderTransform is TranslateTransform transform)
            {
                point.X -= transform.X;
                point.Y -= transform.Y;
            }
            return point;
        }

        private void AnimateOpacity(UIElement element, double targetOpacity)
        {
            element.BeginAnimation(OpacityProperty, new DoubleAnimation(targetOpacity, TimeSpan.FromMilliseconds(110))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
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

        private ContextMenu CreateObjectContextMenu(string connectionString, string databaseName, string objectType, string objectName)
        {
            var menu = new ContextMenu();

            var newQueryItem = new MenuItem { Header = "New Query" };
            newQueryItem.Click += (s, e) => CreateNewQueryTab(connectionString, databaseName);
            menu.Items.Add(newQueryItem);

            if (objectType == "Table" || objectType == "View")
            {
                var selectTopItem = new MenuItem { Header = "Select Top 200" };
                selectTopItem.Click += (s, e) =>
                {
                    string safeName = objectName;
                    var parts = objectName.Split('.');
                    if (parts.Length == 2)
                    {
                        safeName = $"[{parts[0]}].[{parts[1]}]";
                    }
                    else
                    {
                        safeName = $"[{objectName}]";
                    }
                    string sql = $"SELECT TOP 200 * FROM {safeName};";
                    CreateNewQueryTab(connectionString, databaseName, sql, $"{objectName} (Top 200)", autoExecute: true);
                };
                menu.Items.Add(selectTopItem);
            }

            var scriptAsMenu = new MenuItem { Header = "Script Object as" };

            var createToMenu = new MenuItem { Header = "CREATE To" };
            var createNewQuery = new MenuItem { Header = "New Query Editor Window" };
            createNewQuery.Click += async (s, e) => await GenerateScriptObjectAsync(connectionString, databaseName, objectType, objectName, "CREATE");
            createToMenu.Items.Add(createNewQuery);
            scriptAsMenu.Items.Add(createToMenu);

            if (objectType == "Table")
            {
                var insertToMenu = new MenuItem { Header = "INSERT To" };
                var insertNewQuery = new MenuItem { Header = "New Query Editor Window (Standard)" };
                insertNewQuery.Click += async (s, e) => await GenerateScriptObjectAsync(connectionString, databaseName, objectType, objectName, "INSERT");
                insertToMenu.Items.Add(insertNewQuery);

                var insertVarsNewQuery = new MenuItem { Header = "New Query Editor Window (with Variables)" };
                insertVarsNewQuery.Click += async (s, e) => await GenerateScriptObjectAsync(connectionString, databaseName, objectType, objectName, "INSERT_VARS");
                insertToMenu.Items.Add(insertVarsNewQuery);

                var insertDataNewQuery = new MenuItem { Header = "New Query Editor Window (with Data)" };
                insertDataNewQuery.Click += (s, e) => ShowInsertWithDataDialog(connectionString, databaseName, objectName);
                insertToMenu.Items.Add(insertDataNewQuery);

                scriptAsMenu.Items.Add(insertToMenu);

                var updateToMenu = new MenuItem { Header = "UPDATE To" };
                var updateNewQuery = new MenuItem { Header = "New Query Editor Window" };
                updateNewQuery.Click += async (s, e) => await GenerateScriptObjectAsync(connectionString, databaseName, objectType, objectName, "UPDATE");
                updateToMenu.Items.Add(updateNewQuery);
                scriptAsMenu.Items.Add(updateToMenu);

                var deleteToMenu = new MenuItem { Header = "DELETE To" };
                var deleteNewQuery = new MenuItem { Header = "New Query Editor Window" };
                deleteNewQuery.Click += async (s, e) => await GenerateScriptObjectAsync(connectionString, databaseName, objectType, objectName, "DELETE");
                deleteToMenu.Items.Add(deleteNewQuery);
                scriptAsMenu.Items.Add(deleteToMenu);
            }

            var alterToMenu = new MenuItem { Header = "ALTER To" };
            var alterNewQuery = new MenuItem { Header = "New Query Editor Window" };
            alterNewQuery.Click += async (s, e) => await GenerateScriptObjectAsync(connectionString, databaseName, objectType, objectName, "ALTER");
            alterToMenu.Items.Add(alterNewQuery);
            scriptAsMenu.Items.Add(alterToMenu);

            var dropToMenu = new MenuItem { Header = "DROP To" };
            var dropNewQuery = new MenuItem { Header = "New Query Editor Window" };
            dropNewQuery.Click += async (s, e) => await GenerateScriptObjectAsync(connectionString, databaseName, objectType, objectName, "DROP");
            dropToMenu.Items.Add(dropNewQuery);
            scriptAsMenu.Items.Add(dropToMenu);

            menu.Items.Add(scriptAsMenu);

            return menu;
        }

        public Task OpenObjectDefinitionFromEditorAsync(string connectionString, string databaseName, string objectType, string objectName)
        {
            return GenerateScriptObjectAsync(connectionString, databaseName, objectType, objectName, "CREATE");
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
                    else if (scriptType == "INSERT" || scriptType == "INSERT_VARS")
                    {
                        var columns = await DatabaseHelper.GetColumnsAsync(connectionString, databaseName, objectName);
                        var sb = new System.Text.StringBuilder();
                        string safeName = objectName;
                        var parts = objectName.Split('.');
                        if (parts.Length == 2)
                        {
                            safeName = $"[{parts[0]}].[{parts[1]}]";
                        }
                        else
                        {
                            safeName = $"[{objectName}]";
                        }

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
                            if (i < columns.Count - 1)
                            {
                                sb.AppendLine(",");
                            }
                            else
                            {
                                sb.AppendLine();
                            }
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

                            if (i < columns.Count - 1)
                            {
                                sb.AppendLine(",");
                            }
                            else
                            {
                                sb.AppendLine();
                            }
                        }
                        sb.AppendLine(");");
                        sql = sb.ToString();
                    }
                    else if (scriptType == "UPDATE" || scriptType == "DELETE")
                    {
                        var columns = await DatabaseHelper.GetColumnsAsync(connectionString, databaseName, objectName);
                        var keyColumns = columns.Where(column => column.IsPrimaryKey).ToList();
                        if (keyColumns.Count == 0 && columns.Count > 0)
                        {
                            keyColumns.Add(columns[0]);
                        }

                        var sb = new System.Text.StringBuilder();
                        string safeName = QuoteMultipartIdentifier(objectName);
                        if (!columns.Any(column => column.IsPrimaryKey))
                        {
                            sb.AppendLine("-- WARNING: No primary key was found. Review the generated WHERE clause before execution.");
                        }
                        sb.AppendLine("SET XACT_ABORT ON;");
                        sb.AppendLine();

                        IEnumerable<(string ColumnName, string DataType, bool IsPrimaryKey)> variables =
                            scriptType == "UPDATE" ? columns : keyColumns;
                        foreach (var column in variables)
                        {
                            sb.AppendLine($"DECLARE @{SanitizeSqlVariableName(column.ColumnName)} {column.DataType} = NULL;");
                        }
                        sb.AppendLine();

                        if (scriptType == "UPDATE")
                        {
                            var updateColumns = columns.Where(column => !column.IsPrimaryKey).ToList();
                            sb.AppendLine($"UPDATE {safeName}");
                            sb.AppendLine("SET");
                            if (updateColumns.Count == 0 && keyColumns.Count > 0)
                            {
                                sb.AppendLine($"    {QuoteSqlIdentifier(keyColumns[0].ColumnName)} = {QuoteSqlIdentifier(keyColumns[0].ColumnName)}");
                            }
                            for (int i = 0; i < updateColumns.Count; i++)
                            {
                                string suffix = i < updateColumns.Count - 1 ? "," : string.Empty;
                                sb.AppendLine($"    {QuoteSqlIdentifier(updateColumns[i].ColumnName)} = @{SanitizeSqlVariableName(updateColumns[i].ColumnName)}{suffix}");
                            }
                        }
                        else
                        {
                            sb.AppendLine($"DELETE FROM {safeName}");
                        }

                        if (keyColumns.Count > 0)
                        {
                            sb.AppendLine("WHERE");
                            for (int i = 0; i < keyColumns.Count; i++)
                            {
                                string prefix = i == 0 ? "    " : "    AND ";
                                sb.AppendLine($"{prefix}{QuoteSqlIdentifier(keyColumns[i].ColumnName)} = @{SanitizeSqlVariableName(keyColumns[i].ColumnName)}");
                            }
                        }
                        sb.AppendLine(";");
                        sql = sb.ToString();
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

        private static string QuoteMultipartIdentifier(string objectName)
        {
            return string.Join(".", objectName.Split('.').Select(QuoteSqlIdentifier));
        }

        private static string QuoteSqlIdentifier(string identifier)
        {
            return "[" + identifier.Replace("]", "]]") + "]";
        }

        private static string SanitizeSqlVariableName(string columnName)
        {
            string name = new(columnName.Select(character => char.IsLetterOrDigit(character) || character == '_' ? character : '_').ToArray());
            if (string.IsNullOrWhiteSpace(name)) return "Value";
            return char.IsDigit(name[0]) ? $"Value_{name}" : name;
        }

        private void ShowInsertWithDataDialog(string connectionString, string databaseName, string tableName)
        {
            var dialog = new InsertWithDataWindow(connectionString, databaseName, tableName);
            dialog.Owner = this;
            if (dialog.ShowDialog() == true && dialog.IsGenerated)
            {
                CreateNewQueryTab(connectionString, databaseName, dialog.GeneratedSql, $"{tableName}_InsertData.sql");
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

        #endregion
    }
}
