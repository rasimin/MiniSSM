using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SSMS
{
    public sealed class ObjectSearchServerOption
    {
        public string ServerName { get; init; } = string.Empty;
        public string ConnectionString { get; init; } = string.Empty;
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
        private int _detailLoadVersion;

        public string ConnectionString =>
            (ServerComboBox.SelectedItem as ObjectSearchServerOption)?.ConnectionString ??
            _initialConnectionString;

        public event EventHandler<DatabaseObjectSearchResult>? OpenRequested;

        public ObjectSearchWindow(
            IReadOnlyList<ObjectSearchServerOption> servers,
            string initialConnectionString,
            string? initialDatabaseName = null)
        {
            InitializeComponent();
            _servers = servers;
            _initialConnectionString = initialConnectionString;
            _initialDatabaseName = initialDatabaseName;
            Loaded += ObjectSearchWindow_Loaded;
            Closed += ObjectSearchWindow_Closed;
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

            _searchCancellation?.Dispose();
            var source = new CancellationTokenSource();
            _searchCancellation = source;
            _isSearching = true;
            ClearObjectDetails();
            SearchButton.IsEnabled = false;
            CancelSearchButton.IsEnabled = true;
            ServerComboBox.IsEnabled = false;
            DatabaseComboBox.IsEnabled = false;
            StatusText.Text = databaseFilter == null
                ? $"Searching all accessible databases on {server.ServerName}..."
                : $"Searching {server.ServerName} / {databaseFilter}...";

            try
            {
                List<DatabaseObjectSearchResult> results = await DatabaseHelper.SearchObjectsAcrossDatabasesAsync(
                    server.ConnectionString,
                    searchText,
                    databaseFilter,
                    source.Token);
                ResultsGrid.ItemsSource = results;
                StatusText.Text = results.Count >= 1000
                    ? "Showing the first 1,000 matches. Refine the search for more specific results."
                    : $"{results.Count} match(es).";
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

        private async void ResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ResultsGrid.SelectedItem is not DatabaseObjectSearchResult result)
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

        private void OpenSelectedResult()
        {
            if (ResultsGrid.SelectedItem is DatabaseObjectSearchResult result)
            {
                OpenRequested?.Invoke(this, result);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
