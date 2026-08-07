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
using Microsoft.Web.WebView2.Core;

namespace SSMS
{
    public partial class MainWindow : Window
    {
        public static MainWindow? Instance { get; private set; }

        private readonly string _initialConnectionString;
        private readonly TaskCompletionSource<bool> _startupCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _queryTabCounter = 0;

        public Task StartupCompletion => _startupCompletion.Task;

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
        private string? _lastSaveOrOpenFolder;

        private bool _isSharedWebViewInitialized;
        private readonly TaskCompletionSource<bool> _sharedWebViewReadyCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task SharedWebViewReady => _sharedWebViewReadyCompletion.Task;
        private QueryTabControl? _currentAttachedTab;

        private static readonly Duration ReorderAnimationDuration = new(TimeSpan.FromMilliseconds(320));
        private const string QueryTabDragHandleTag = "QueryTabDragHandle";

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int WM_MOUSEHWHEEL = 0x020E;
        private HwndSource? _windowSource;

        public MainWindow(string connectionString)
        {
            Instance = this;
            InitializeComponent();
            ApplyDarkMode();
            _initialConnectionString = connectionString;
            ApplyToolbarOrder();

            TreeObjectExplorer.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler(TreeItem_Expanded));
            TreeObjectExplorer.SelectedItemChanged += (_, _) => _useObjectExplorerContextForNewQuery = true;
            TreeObjectExplorer.PreviewMouseDown += (_, _) => _useObjectExplorerContextForNewQuery = true;
        }

        private void ApplyDarkMode()
        {
            try
            {
                var helper = new WindowInteropHelper(this);
                helper.EnsureHandle();
                int darkMode = 1;
                DwmSetWindowAttribute(helper.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
            }
            catch { }
        }

        private void BtnTitleMinimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void BtnTitleMaximize_Click(object sender, RoutedEventArgs e)
        {
            if (WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
                if (BtnTitleMaximize != null) BtnTitleMaximize.Content = "🗖";
            }
            else
            {
                WindowState = WindowState.Maximized;
                if (BtnTitleMaximize != null) BtnTitleMaximize.Content = "🗗";
            }
        }

        private void BtnTitleClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            if (BtnTitleMaximize != null)
            {
                BtnTitleMaximize.Content = (WindowState == WindowState.Maximized) ? "🗗" : "🗖";
            }
        }

        private async Task InitializeSharedWebViewAsync()
        {
            if (_isSharedWebViewInitialized) return;
            _isSharedWebViewInitialized = true;

            try
            {
                var env = await QueryTabControl.GetSharedEnvironmentAsync();
                await SharedSqlEditorWebView.EnsureCoreWebView2Async(env);
                SharedSqlEditorWebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(30, 30, 30);

                string editorDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Editor");
                string htmlPath = Path.Combine(editorDirectory, "sql_editor.html");
                SharedSqlEditorWebView.WebMessageReceived += SharedSqlEditorWebView_WebMessageReceived;
                SharedSqlEditorWebView.Source = new Uri(htmlPath);
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "Failed to initialize Shared Monaco SQL Editor WebView2");
                _sharedWebViewReadyCompletion.TrySetResult(false);
            }
        }

        private void SharedSqlEditorWebView_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string json = e.WebMessageAsJson;
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("action", out var actionProp))
                {
                    return;
                }

                string action = actionProp.GetString() ?? string.Empty;
                string? msgTabId = doc.RootElement.TryGetProperty("tabId", out var tProp) ? tProp.GetString() : null;

                QueryTabControl? targetTab = null;
                if (!string.IsNullOrEmpty(msgTabId))
                {
                    targetTab = TabQueryControls.Items.OfType<TabItem>()
                        .Select(t => t.Content as QueryTabControl)
                        .FirstOrDefault(q => q != null && q.TabId == msgTabId);
                }
                targetTab ??= (TabQueryControls.SelectedItem as TabItem)?.Content as QueryTabControl;

                if (action == "editorReady")
                {
                    _sharedWebViewReadyCompletion.TrySetResult(true);
                    if (_currentAttachedTab != null)
                    {
                        _ = AttachSharedWebViewToTabAsync(_currentAttachedTab);
                    }
                }
                else if (action == "execute")
                {
                    targetTab?.ExecuteQuery();
                }
                else if (action == "newQuery")
                {
                    CreateNewQueryFromCurrentContext();
                }
                else if (action == "editorFocused")
                {
                    targetTab?.NotifyEditorFocused();
                }
                else if (action == "contentChanged")
                {
                    targetTab?.ScheduleDirtyCheck();
                }
                else if (action == "requestMetadata")
                {
                    targetTab?.RequestAutocompleteMetadata();
                }
                else if (action == "loadDatabaseMetadata" && doc.RootElement.TryGetProperty("databaseName", out var dbProp))
                {
                    string? dbName = dbProp.GetString();
                    if (!string.IsNullOrWhiteSpace(dbName) && targetTab != null)
                    {
                        _ = targetTab.LoadCrossDatabaseMetadataAsync(dbName);
                    }
                }
                else if (action == "fetchObjectScript" && doc.RootElement.TryGetProperty("objectName", out var fetchObjProp))
                {
                    string? objName = fetchObjProp.GetString();
                    string statementType = doc.RootElement.TryGetProperty("statementType", out var stProp) ? (stProp.GetString() ?? "ALTER") : "ALTER";
                    if (!string.IsNullOrWhiteSpace(objName) && targetTab != null)
                    {
                        _ = targetTab.FetchObjectScriptAndReplaceAsync(objName, statementType);
                    }
                }
                else if (action == "viewObjectDefinition" &&
                         doc.RootElement.TryGetProperty("objectName", out var objProp) &&
                         doc.RootElement.TryGetProperty("objectType", out var typeProp))
                {
                    string? objName = objProp.GetString();
                    string? objType = typeProp.GetString();
                    if (!string.IsNullOrWhiteSpace(objName) && !string.IsNullOrWhiteSpace(objType) && targetTab != null)
                    {
                        _ = targetTab.ShowObjectDefinitionTabAsync(objName, objType);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "Error handling shared webview message");
            }
        }

        public async Task AttachSharedWebViewToTabAsync(QueryTabControl targetTab)
        {
            if (targetTab == null) return;
            await SharedWebViewReady;

            if (SharedSqlEditorWebView.Parent is Grid oldParentGrid)
            {
                oldParentGrid.Children.Remove(SharedSqlEditorWebView);
            }

            _currentAttachedTab = targetTab;
            targetTab.EditorHostGrid.Children.Add(SharedSqlEditorWebView);
            SharedSqlEditorWebView.Visibility = Visibility.Visible;
            targetTab.EditorLoadingPanel.Visibility = Visibility.Collapsed;
            targetTab.IsWebViewInitialized = true;
            await targetTab.CompleteEditorInitializationAsync();

            await SharedSqlEditorWebView.ExecuteScriptAsync($"switchTabModel('{targetTab.TabId}');");
            SharedSqlEditorWebView.Focus();
            _ = targetTab.CacheAndRefreshAutocompleteAsync();
        }

        public void DetachSharedWebViewFromTab(QueryTabControl tab)
        {
            if (_currentAttachedTab == tab)
            {
                if (SharedSqlEditorWebView.Parent is Grid parentGrid)
                {
                    parentGrid.Children.Remove(SharedSqlEditorWebView);
                }
                SharedWebViewHost.Children.Add(SharedSqlEditorWebView);
                SharedSqlEditorWebView.Visibility = Visibility.Collapsed;
                _currentAttachedTab = null;
            }
            if (_isSharedWebViewInitialized)
            {
                _ = SharedSqlEditorWebView.ExecuteScriptAsync($"disposeTabModel('{tab.TabId}');");
            }
        }

        private void ApplyToolbarOrder()
        {
            Border[] defaultItems =
            {
                ToolbarObjectExplorer,
                ToolbarDatabase,
                ToolbarExecute,
                ToolbarNewQuery,
                ToolbarComment,
                ToolbarUncomment,
                ToolbarSave,
                ToolbarSaveAs,
                ToolbarOpen,
                ToolbarQueryHistory,
                ToolbarInsertScript,
                ToolbarQueryTools,
                ToolbarSettings
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
            _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _windowSource?.AddHook(WindowMessageHook);
        }

        protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (_allowWindowClose)
            {
                base.OnClosing(e);
                return;
            }

            e.Cancel = true;
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
                await InitializeSharedWebViewAsync();
                await AddServerToExplorerAsync(_initialConnectionString);

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
    }
}
