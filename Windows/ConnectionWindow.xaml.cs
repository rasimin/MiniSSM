using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace SSMS
{
    public partial class ConnectionWindow : Window
    {
        public string ConnectionString { get; private set; } = string.Empty;
        public event EventHandler? ConnectionAccepted;

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        private static readonly string SettingsFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "connection_settings.json");

        public class SavedConnection
        {
            public string Server { get; set; } = string.Empty;
            public int AuthIndex { get; set; } = 0;
            public string Login { get; set; } = string.Empty;
            public string Password { get; set; } = string.Empty;
            public bool RememberMe { get; set; } = false;
        }

        public class ConnectionHistory
        {
            public System.Collections.Generic.List<SavedConnection> Connections { get; set; } = new System.Collections.Generic.List<SavedConnection>();
        }

        private ConnectionHistory _history = new ConnectionHistory();

        public ConnectionWindow()
        {
            InitializeComponent();
            LoadConnectionSettings();
        }

        private void LoadConnectionSettings()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    string json = File.ReadAllText(SettingsFile);
                    var history = JsonSerializer.Deserialize<ConnectionHistory>(json);
                    if (history != null)
                    {
                        _history = history;
                    }
                }
            }
            catch { }

            CboServer.SelectionChanged -= CboServer_SelectionChanged;
            try
            {
                var servers = new System.Collections.Generic.List<string>();
                foreach (var conn in _history.Connections)
                {
                    if (!string.IsNullOrEmpty(conn.Server) && !servers.Contains(conn.Server))
                    {
                        servers.Add(conn.Server);
                    }
                }
                CboServer.ItemsSource = servers;

                if (_history.Connections.Count > 0)
                {
                    var first = _history.Connections[0];
                    CboServer.Text = first.Server;
                    CboAuth.SelectedIndex = first.AuthIndex;
                    TxtLogin.Text = first.Login;
                    TxtPassword.Password = first.Password;
                    ChkRemember.IsChecked = first.RememberMe;
                    
                    CboAuth_SelectionChanged(null!, null!);
                }
            }
            finally
            {
                CboServer.SelectionChanged += CboServer_SelectionChanged;
            }
        }

        private void CboServer_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e != null && e.AddedItems.Count > 0 && e.AddedItems[0] is string selectedServer)
            {
                SavedConnection? match = null;
                foreach (var conn in _history.Connections)
                {
                    if (conn.Server.Equals(selectedServer, StringComparison.OrdinalIgnoreCase))
                    {
                        match = conn;
                        break;
                    }
                }

                if (match != null)
                {
                    CboAuth.SelectedIndex = match.AuthIndex;
                    TxtLogin.Text = match.Login;
                    TxtPassword.Password = match.Password;
                    ChkRemember.IsChecked = match.RememberMe;
                    CboAuth_SelectionChanged(null!, null!);
                }
            }
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
                // Ignore DWM failures on older Windows versions
            }
        }

        private void CboAuth_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TxtLogin == null || TxtPassword == null || LblLogin == null || LblPassword == null)
                return;

            bool isSqlAuth = CboAuth.SelectedIndex == 1;
            TxtLogin.IsEnabled = isSqlAuth;
            TxtPassword.IsEnabled = isSqlAuth;
            LblLogin.IsEnabled = isSqlAuth;
            LblPassword.IsEnabled = isSqlAuth;
        }

        private string BuildConnectionString()
        {
            string server = CboServer.Text.Trim();
            if (string.IsNullOrEmpty(server))
            {
                throw new ArgumentException("Server name cannot be empty.");
            }

            if (CboAuth.SelectedIndex == 0) // Windows Authentication
            {
                return $"Server={server};Database=master;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;Connect Timeout=5;";
            }
            else // SQL Server Authentication
            {
                string login = TxtLogin.Text.Trim();
                string password = TxtPassword.Password;
                if (string.IsNullOrEmpty(login))
                {
                    throw new ArgumentException("Login username cannot be empty.");
                }
                return $"Server={server};Database=master;User Id={login};Password={password};Encrypt=True;TrustServerCertificate=True;Connect Timeout=5;";
            }
        }

        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            TxtStatus.Text = "";
            BtnConnect.IsEnabled = false;
            BtnTest.IsEnabled = false;

            try
            {
                string connString = BuildConnectionString();
                TxtStatus.Foreground = System.Windows.Media.Brushes.LightBlue;
                TxtStatus.Text = "Connecting...";

                await Task.Run(() => DatabaseHelper.TestConnectionAsync(connString));

                // Save connection settings in history list
                try
                {
                    string serverName = CboServer.Text.Trim();
                    _history.Connections.RemoveAll(c => c.Server.Equals(serverName, StringComparison.OrdinalIgnoreCase));

                    if (ChkRemember.IsChecked == true)
                    {
                        var settings = new SavedConnection
                        {
                            Server = serverName,
                            AuthIndex = CboAuth.SelectedIndex,
                            Login = TxtLogin.Text.Trim(),
                            Password = TxtPassword.Password,
                            RememberMe = true
                        };
                        _history.Connections.Insert(0, settings);
                    }
                    
                    if (_history.Connections.Count > 10)
                    {
                        _history.Connections = _history.Connections.GetRange(0, 10);
                    }

                    string json = JsonSerializer.Serialize(_history);
                    File.WriteAllText(SettingsFile, json);
                }
                catch { }

                ConnectionString = connString;
                ConnectionAccepted?.Invoke(this, EventArgs.Empty);
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                TxtStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF4, 0x8B, 0x8B));
                TxtStatus.Text = $"Connection failed: {ex.Message}";
            }
            finally
            {
                BtnConnect.IsEnabled = true;
                BtnTest.IsEnabled = true;
            }
        }

        private async void BtnTest_Click(object sender, RoutedEventArgs e)
        {
            TxtStatus.Text = "";
            BtnConnect.IsEnabled = false;
            BtnTest.IsEnabled = false;

            try
            {
                string connString = BuildConnectionString();
                TxtStatus.Foreground = System.Windows.Media.Brushes.LightBlue;
                TxtStatus.Text = "Testing connection...";

                await Task.Run(() => DatabaseHelper.TestConnectionAsync(connString));

                TxtStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8B, 0xF4, 0x8B)); // light green
                TxtStatus.Text = "Test connection succeeded.";
            }
            catch (Exception ex)
            {
                TxtStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF4, 0x8B, 0x8B));
                TxtStatus.Text = $"Connection failed: {ex.Message}";
            }
            finally
            {
                BtnConnect.IsEnabled = true;
                BtnTest.IsEnabled = true;
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
