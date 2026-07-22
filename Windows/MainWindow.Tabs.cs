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

    }
}