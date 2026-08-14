using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace SSMS
{
    public sealed class ObjectSearchServerOption
    {
        public string ServerName { get; init; } = string.Empty;
        public string ConnectionString { get; init; } = string.Empty;
    }

    public class ObjectSearchOpenEventArgs : EventArgs
    {
        public DatabaseObjectSearchResult Result { get; }
        public bool Success { get; set; }

        public ObjectSearchOpenEventArgs(DatabaseObjectSearchResult result)
        {
            Result = result;
        }
    }

    public partial class ObjectSearchWindow : Window
    {
        private const string AllDatabasesLabel = "(All accessible databases)";
        private readonly IReadOnlyList<ObjectSearchServerOption> _servers;
        private readonly string _initialConnectionString;
        private readonly string? _initialDatabaseName;
        private CancellationTokenSource? _searchCancellation;
        private CancellationTokenSource? _databaseLoadCancellation;
        private bool _windowLoaded;
        private bool _isSearching;
        private bool _isCustomMaximized;
        private Rect _restoreBounds;
        private int _detailLoadVersion;

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DwmwaUseImmersiveDarkMode = 20;

        public string ConnectionString =>
            (ServerComboBox.SelectedItem as ObjectSearchServerOption)?.ConnectionString ??
            _initialConnectionString;

        public event EventHandler<ObjectSearchOpenEventArgs>? OpenRequested;

        public ObjectSearchWindow(
            IReadOnlyList<ObjectSearchServerOption> servers,
            string initialConnectionString,
            string? initialDatabaseName = null)
        {
            InitializeComponent();
            ApplyDarkMode();
            _servers = servers;
            _initialConnectionString = initialConnectionString;
            _initialDatabaseName = initialDatabaseName;
            Loaded += ObjectSearchWindow_Loaded;
            Closed += ObjectSearchWindow_Closed;
        }

        private void ApplyDarkMode()
        {
            try
            {
                var helper = new WindowInteropHelper(this);
                helper.EnsureHandle();
                int darkMode = 1;
                DwmSetWindowAttribute(helper.Handle, DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int));
            }
            catch
            {
                // Dark title bar is best effort on older Windows versions.
            }
        }



        private async void ObjectSearchWindow_Loaded(object sender, RoutedEventArgs e)
        {
            ServerComboBox.ItemsSource = _servers;
            ServerComboBox.SelectedItem = _servers.FirstOrDefault(server =>
                string.Equals(server.ConnectionString, _initialConnectionString, StringComparison.OrdinalIgnoreCase)) ??
                _servers.FirstOrDefault();
            _windowLoaded = true;
            await LoadDatabasesAsync();
            SearchTextBox.Focus();
        }

        private void ObjectSearchWindow_Closed(object? sender, EventArgs e)
        {
            _detailLoadVersion++;
            _searchCancellation?.Cancel();
            _databaseLoadCancellation?.Cancel();
            _searchCancellation?.Dispose();
            _databaseLoadCancellation?.Dispose();
        }

        private async void ServerComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_windowLoaded) return;
            CancelActiveSearch("Search cancelled because the server filter changed.");
            ResultsGrid.ItemsSource = null;
            ClearObjectDetails();
            await LoadDatabasesAsync();
        }

        private async Task LoadDatabasesAsync()
        {
            _databaseLoadCancellation?.Cancel();
            _databaseLoadCancellation?.Dispose();
            var source = new CancellationTokenSource();
            _databaseLoadCancellation = source;
            DatabaseComboBox.IsEnabled = false;
            DatabaseComboBox.ItemsSource = new[] { "Loading databases..." };
            DatabaseComboBox.SelectedIndex = 0;

            try
            {
                List<string> databases = await DatabaseHelper.GetDatabasesAsync(ConnectionString, source.Token);
                var options = new List<string> { AllDatabasesLabel };
                options.AddRange(databases);
                DatabaseComboBox.ItemsSource = options;

                if (!string.IsNullOrEmpty(_initialDatabaseName) &&
                    databases.Any(db => string.Equals(db, _initialDatabaseName, StringComparison.OrdinalIgnoreCase)))
                {
                    string targetDb = databases.First(db => string.Equals(db, _initialDatabaseName, StringComparison.OrdinalIgnoreCase));
                    DatabaseComboBox.SelectedItem = targetDb;
                }
                else
                {
                    DatabaseComboBox.SelectedIndex = 0;
                }

                DatabaseComboBox.IsEnabled = true;
                StatusText.Text = $"{databases.Count} accessible database(s) loaded.";
            }
            catch (OperationCanceledException)
            {
                // A newer server selection is loading its own database list.
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "Failed to load database filters for object search");
                DatabaseComboBox.ItemsSource = new[] { AllDatabasesLabel };
                DatabaseComboBox.SelectedIndex = 0;
                DatabaseComboBox.IsEnabled = true;
                StatusText.Text = $"Failed to load databases: {ex.Message}";
            }
        }

        private async void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            await SearchAsync();
        }

        private void CancelSearchButton_Click(object sender, RoutedEventArgs e)
        {
            CancelActiveSearch("Cancelling search...");
        }

        private async void SearchTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !_isSearching)
            {
                await SearchAsync();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _isSearching)
            {
                CancelActiveSearch("Cancelling search...");
                e.Handled = true;
            }
        }

        private async Task SearchAsync()
        {
            if (_isSearching) return;

            string searchText = SearchTextBox.Text.Trim();
            if (searchText.Length < 2)
            {
                StatusText.Text = "Enter at least 2 characters.";
                return;
            }
            if (ServerComboBox.SelectedItem is not ObjectSearchServerOption server)
            {
                StatusText.Text = "Select a server first.";
                return;
            }

            string? databaseFilter = DatabaseComboBox.SelectedItem as string;
            if (string.Equals(databaseFilter, AllDatabasesLabel, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(databaseFilter))
            {
                databaseFilter = null;
            }

            string? objectTypeFilter = (ObjectTypeComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
            if (string.IsNullOrWhiteSpace(objectTypeFilter))
            {
                objectTypeFilter = null;
            }

            _searchCancellation?.Dispose();
            var source = new CancellationTokenSource();
            _searchCancellation = source;
            _isSearching = true;
            ClearObjectDetails();
            SearchButton.IsEnabled = false;
            CancelSearchButton.IsEnabled = true;
            ServerComboBox.IsEnabled = false;
            DatabaseComboBox.IsEnabled = false;
            string typeDescription = objectTypeFilter ?? "all object types";
            StatusText.Text = databaseFilter == null
                ? $"Searching {typeDescription} in all accessible databases on {server.ServerName}..."
                : $"Searching {typeDescription} in {server.ServerName} / {databaseFilter}...";

            try
            {
                List<DatabaseObjectSearchResult> results = await DatabaseHelper.SearchObjectsAcrossDatabasesAsync(
                    server.ConnectionString,
                    searchText,
                    databaseFilter,
                    objectTypeFilter,
                    source.Token);
                ResultsGrid.ItemsSource = results;
                StatusText.Text = results.Count >= 1000
                    ? "Showing rows 1-1,000. Refine the search for more objects."
                    : $"{results.Count} object(s) found. Row numbers are shown on the left.";
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "Search cancelled.";
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "Database object search failed");
                StatusText.Text = $"Search failed: {ex.Message}";
            }
            finally
            {
                if (ReferenceEquals(_searchCancellation, source))
                {
                    _isSearching = false;
                    _searchCancellation = null;
                    source.Dispose();
                    SearchButton.IsEnabled = true;
                    CancelSearchButton.IsEnabled = false;
                    ServerComboBox.IsEnabled = true;
                    DatabaseComboBox.IsEnabled = true;
                }
            }
        }

        private void CancelActiveSearch(string status)
        {
            if (!_isSearching || _searchCancellation == null) return;
            StatusText.Text = status;
            CancelSearchButton.IsEnabled = false;
            _searchCancellation.Cancel();
        }

        private void OpenButton_Click(object sender, RoutedEventArgs e) => OpenSelectedResult();

        private void ResultsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedResult();

        private void ResultsGrid_LoadingRow(object sender, DataGridRowEventArgs e)
        {
            e.Row.Header = e.Row.GetIndex() + 1;
        }

        private async void ResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            DatabaseObjectSearchResult? result = GetSelectedResult();
            if (result == null)
            {
                ClearObjectDetails();
                return;
            }

            int loadVersion = ++_detailLoadVersion;
            ShowObjectDetails(result);
            DefinitionHeaderText.Text = $"Schema / Definition: {result.FullName}";
            DefinitionTextBox.Text = "Loading schema / definition...";
            try
            {
                string definition = result.ObjectType == "Table"
                    ? await DatabaseHelper.GenerateTableCreateScriptAsync(
                        ConnectionString,
                        result.DatabaseName,
                        result.FullName)
                    : await DatabaseHelper.GetObjectDefinitionAsync(
                        ConnectionString,
                        result.DatabaseName,
                        result.FullName);

                if (loadVersion != _detailLoadVersion) return;
                DefinitionTextBox.Text = string.IsNullOrWhiteSpace(definition)
                    ? $"-- Definition is unavailable for {result.FullName}. The object may be encrypted or not expose module text."
                    : definition;
                DefinitionTextBox.ScrollToHome();
            }
            catch (Exception ex)
            {
                if (loadVersion != _detailLoadVersion) return;
                AppLogger.Error(ex, $"Failed to load search detail for '{result.DatabaseName}.{result.FullName}'");
                DefinitionTextBox.Text = $"-- Failed to load definition: {ex.Message}";
            }
        }

        private void ShowObjectDetails(DatabaseObjectSearchResult result)
        {
            string serverName = (ServerComboBox.SelectedItem as ObjectSearchServerOption)?.ServerName ?? string.Empty;
            ObjectDetailsTextBox.Text =
                $"Server       : {serverName}{Environment.NewLine}" +
                $"Database     : {result.DatabaseName}{Environment.NewLine}" +
                $"Schema       : {result.SchemaName}{Environment.NewLine}" +
                $"Object       : {result.ObjectName}{Environment.NewLine}" +
                $"Type         : {result.ObjectType}{Environment.NewLine}" +
                $"Object ID    : {result.ObjectId}{Environment.NewLine}" +
                $"Created date : {result.CreateDate:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
                $"Modified date: {result.ModifyDate:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
                $"Matched in   : {result.MatchLocation}{Environment.NewLine}" +
                $"Match detail : {result.MatchDetail}";
            ObjectDetailsTextBox.ScrollToHome();
        }

        private void ClearObjectDetails()
        {
            _detailLoadVersion++;
            DefinitionHeaderText.Text = "Schema / Definition";
            DefinitionTextBox.Text = string.Empty;
            ObjectDetailsTextBox.Text = string.Empty;
        }

        private DatabaseObjectSearchResult? GetSelectedResult()
        {
            if (ResultsGrid.SelectedItem is DatabaseObjectSearchResult selectedItem)
            {
                return selectedItem;
            }

            foreach (DataGridCellInfo cell in ResultsGrid.SelectedCells)
            {
                if (cell.Item is DatabaseObjectSearchResult result)
                {
                    return result;
                }
            }

            return null;
        }

        private void CopyDefinitionButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(DefinitionTextBox.Text)) return;
            try
            {
                Clipboard.SetText(DefinitionTextBox.Text);
                StatusText.Text = "Schema / definition copied.";
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "Failed to copy object search definition");
                StatusText.Text = $"Copy failed: {ex.Message}";
            }
        }

        private void CopySelectedGridCells_Click(object sender, RoutedEventArgs e)
        {
            var selectedCells = ResultsGrid.SelectedCells
                .Where(cell => cell.Item is DatabaseObjectSearchResult && cell.Column.Visibility == Visibility.Visible)
                .ToList();

            if (selectedCells.Count == 0)
            {
                CopySelectedGridRow_Click(sender, e);
                return;
            }

            var rows = selectedCells
                .Select(cell => (DatabaseObjectSearchResult)cell.Item)
                .Distinct()
                .OrderBy(result => ResultsGrid.Items.IndexOf(result))
                .ToList();
            var columns = selectedCells
                .Select(cell => cell.Column)
                .Distinct()
                .OrderBy(column => ResultsGrid.Columns.IndexOf(column))
                .ToList();

            CopyGridText(BuildGridClipboardText(rows, columns, includeHeaders: false),
                $"Copied {rows.Count} row(s), {columns.Count} column(s).");
        }

        private void CopySelectedGridRow_Click(object sender, RoutedEventArgs e)
        {
            DatabaseObjectSearchResult? result = ResultsGrid.CurrentCell.Item as DatabaseObjectSearchResult ??
                ResultsGrid.SelectedItem as DatabaseObjectSearchResult;
            if (result == null)
            {
                StatusText.Text = "Select a result row first.";
                return;
            }

            var columns = ResultsGrid.Columns
                .Where(column => column.Visibility == Visibility.Visible)
                .ToList();
            CopyGridText(BuildGridClipboardText(new[] { result }, columns, includeHeaders: true),
                "Copied selected result row.");
        }

        private void CopySelectedGridColumn_Click(object sender, RoutedEventArgs e)
        {
            DataGridColumn? column = ResultsGrid.CurrentCell.Column;
            if (column == null && ResultsGrid.SelectedCells.Count > 0)
            {
                column = ResultsGrid.SelectedCells[0].Column;
            }
            if (column == null || column.Visibility != Visibility.Visible)
            {
                StatusText.Text = "Select a result column first.";
                return;
            }

            var rows = ResultsGrid.Items
                .Cast<object>()
                .OfType<DatabaseObjectSearchResult>()
                .ToList();
            CopyGridText(BuildGridClipboardText(rows, new[] { column }, includeHeaders: true),
                $"Copied column '{GetGridColumnHeader(column)}'.");
        }

        private void CopyAllGridResults_Click(object sender, RoutedEventArgs e)
        {
            var rows = ResultsGrid.Items
                .Cast<object>()
                .OfType<DatabaseObjectSearchResult>()
                .ToList();
            var columns = ResultsGrid.Columns
                .Where(column => column.Visibility == Visibility.Visible)
                .ToList();
            CopyGridText(BuildGridClipboardText(rows, columns, includeHeaders: true),
                $"Copied {rows.Count} result(s) with headers.");
        }

        private string BuildGridClipboardText(
            IReadOnlyList<DatabaseObjectSearchResult> rows,
            IReadOnlyList<DataGridColumn> columns,
            bool includeHeaders)
        {
            var lines = new List<string>();
            if (includeHeaders)
            {
                lines.Add(string.Join("\t", columns.Select(GetGridColumnHeader)));
            }

            foreach (DatabaseObjectSearchResult result in rows)
            {
                lines.Add(string.Join("\t", columns.Select(column =>
                    NormalizeClipboardValue(GetGridCellValue(result, column)))));
            }

            return string.Join(Environment.NewLine, lines);
        }

        private static string GetGridColumnHeader(DataGridColumn column)
        {
            return column.Header?.ToString() ?? string.Empty;
        }

        private static string GetGridCellValue(DatabaseObjectSearchResult result, DataGridColumn column)
        {
            return GetGridColumnHeader(column) switch
            {
                "Database" => result.DatabaseName,
                "Schema" => result.SchemaName,
                "Object" => result.ObjectName,
                "Type" => result.ObjectType,
                "Matched In" => result.MatchLocation,
                "Match Detail" => result.MatchDetail,
                _ => string.Empty
            };
        }

        private static string NormalizeClipboardValue(string? value)
        {
            return (value ?? string.Empty)
                .Replace("\t", " ", StringComparison.Ordinal)
                .Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal);
        }

        private void CopyGridText(string text, string status)
        {
            if (string.IsNullOrEmpty(text))
            {
                StatusText.Text = "There is no result data to copy.";
                return;
            }

            try
            {
                Clipboard.SetText(text);
                StatusText.Text = status;
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "Failed to copy object search results");
                StatusText.Text = $"Copy failed: {ex.Message}";
            }
        }

        private void HeaderGrid_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void BtnMaximize_Click(object sender, RoutedEventArgs e)
        {
            if (_isCustomMaximized)
            {
                Left = _restoreBounds.Left;
                Top = _restoreBounds.Top;
                Width = _restoreBounds.Width;
                Height = _restoreBounds.Height;
                _isCustomMaximized = false;
            }
            else
            {
                _restoreBounds = new Rect(Left, Top, Width, Height);
                Rect workArea = SystemParameters.WorkArea;
                Left = workArea.Left;
                Top = workArea.Top;
                Width = workArea.Width;
                Height = workArea.Height;
                _isCustomMaximized = true;
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        }

        private void OpenSelectedResult()
        {
            if (ResultsGrid.SelectedItem is DatabaseObjectSearchResult result)
            {
                var args = new ObjectSearchOpenEventArgs(result);
                OpenRequested?.Invoke(this, args);
                if (args.Success)
                {
                    Close();
                }
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
