using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace SSMS;

public partial class ConnectionWindow : Window
{
    private static readonly string SettingsFile =
        Path.Combine(AppContext.BaseDirectory, "connection_settings.json");
    private readonly TaskCompletionSource<string?> _result = new();
    private ConnectionHistory _history = new();
    private bool _resultSet;

    public ConnectionWindow()
    {
        InitializeComponent();
        LoadSettings();
        UpdateAuthenticationState();
        Closing += (_, _) => Complete(null);
    }

    public Task<string?> WaitForResultAsync() => _result.Task;

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsFile))
                _history = JsonSerializer.Deserialize<ConnectionHistory>(
                    File.ReadAllText(SettingsFile)) ?? new();
        }
        catch (Exception ex)
        {
            AppLogger.Error(ex, "Failed to load connection history.");
        }

        ServerBox.ItemsSource = _history.Connections
            .Select(x => x.Server).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (_history.Connections.FirstOrDefault() is { } first)
            ApplyConnection(first);
    }

    private void ApplyConnection(SavedConnection connection)
    {
        ServerBox.Text = connection.Server;
        AuthenticationBox.SelectedIndex = connection.AuthIndex;
        LoginBox.Text = connection.Login;
        PasswordBox.Text = connection.Password;
        RememberBox.IsChecked = connection.RememberMe;
        UpdateAuthenticationState();
    }

    private string BuildConnectionString()
    {
        var server = ServerBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(server))
            throw new ArgumentException("Server name cannot be empty.");
        if (AuthenticationBox.SelectedIndex == 0)
            return $"Server={server};Database=master;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;Connect Timeout=15;";

        var login = LoginBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(login))
            throw new ArgumentException("Login username cannot be empty.");
        return $"Server={server};Database=master;User Id={login};Password={PasswordBox.Text};Encrypt=True;TrustServerCertificate=True;Connect Timeout=15;";
    }

    private async Task TestAsync(bool connect)
    {
        TestButton.IsEnabled = ConnectButton.IsEnabled = false;
        try
        {
            StatusText.Foreground = Brushes.LightBlue;
            StatusText.Text = connect ? "Connecting..." : "Testing connection...";
            var connectionString = BuildConnectionString();
            await DatabaseHelper.TestConnectionAsync(connectionString);
            StatusText.Foreground = Brushes.LightGreen;
            StatusText.Text = "Connection succeeded.";
            if (!connect) return;

            SaveCurrentConnection();
            Complete(connectionString);
            Close();
        }
        catch (Exception ex)
        {
            AppLogger.Error(ex, "SQL Server connection failed.");
            StatusText.Foreground = new SolidColorBrush(Color.Parse("#F48B8B"));
            StatusText.Text = $"Connection failed: {ex.Message}";
        }
        finally
        {
            TestButton.IsEnabled = ConnectButton.IsEnabled = true;
        }
    }

    private void SaveCurrentConnection()
    {
        var server = ServerBox.Text?.Trim() ?? "";
        _history.Connections.RemoveAll(x =>
            x.Server.Equals(server, StringComparison.OrdinalIgnoreCase));
        if (RememberBox.IsChecked == true)
        {
            _history.Connections.Insert(0, new SavedConnection
            {
                Server = server,
                AuthIndex = AuthenticationBox.SelectedIndex,
                Login = LoginBox.Text?.Trim() ?? "",
                Password = PasswordBox.Text ?? "",
                RememberMe = true
            });
        }
        _history.Connections = _history.Connections.Take(10).ToList();
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(_history));
    }

    private void Complete(string? value)
    {
        if (_resultSet) return;
        _resultSet = true;
        _result.TrySetResult(value);
    }

    private void UpdateAuthenticationState()
    {
        // SelectedIndex in XAML raises SelectionChanged while InitializeComponent
        // is still assigning named controls. Ignore that early notification.
        if (AuthenticationBox is null || LoginLabel is null || PasswordLabel is null ||
            LoginBox is null || PasswordBox is null)
            return;

        var enabled = AuthenticationBox.SelectedIndex == 1;
        LoginLabel.IsEnabled = PasswordLabel.IsEnabled =
            LoginBox.IsEnabled = PasswordBox.IsEnabled = enabled;
    }

    private void ServerBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ServerBox.SelectedItem is string server &&
            _history.Connections.FirstOrDefault(x =>
                x.Server.Equals(server, StringComparison.OrdinalIgnoreCase)) is { } connection)
            ApplyConnection(connection);
    }

    private void AuthenticationBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        UpdateAuthenticationState();
    private async void TestButton_OnClick(object? sender, RoutedEventArgs e) => await TestAsync(false);
    private async void ConnectButton_OnClick(object? sender, RoutedEventArgs e) => await TestAsync(true);
    private void CancelButton_OnClick(object? sender, RoutedEventArgs e)
    {
        Complete(null);
        Close();
    }

    private sealed class SavedConnection
    {
        public string Server { get; set; } = "";
        public int AuthIndex { get; set; }
        public string Login { get; set; } = "";
        public string Password { get; set; } = "";
        public bool RememberMe { get; set; }
    }

    private sealed class ConnectionHistory
    {
        public List<SavedConnection> Connections { get; set; } = [];
    }
}
