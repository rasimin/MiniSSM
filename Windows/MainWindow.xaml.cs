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
            ApplyDarkMode();
            _initialConnectionString = connectionString;
            ApplyToolbarOrder();

            // Connect TreeView expanded event handler
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
                int darkMode = 1; // 1 = Enable
                DwmSetWindowAttribute(helper.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
            }
            catch { }
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
            }
            catch
            {
                // Ignore failures
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
                // 0. Initialize keep-alive WebView to ensure Chromium engine never shuts down
                _ = KeepAliveWebView.EnsureCoreWebView2Async(await QueryTabControl.GetSharedEnvironmentAsync());

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