using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace SSMS
{
    public class ToolbarOrderItem
    {
        public string Name { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;

        public override string ToString() => DisplayName;
    }

    public partial class ToolbarOrderWindow : Window
    {
        private const int DwmwaUseImmersiveDarkMode = 20;
        private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd,
            int attribute,
            ref int attributeValue,
            int attributeSize);

        private static readonly Dictionary<string, string> KnownToolbarItems = new()
        {
            { "ToolbarObjectExplorer", "☰  Toggle Object Explorer (F8)" },
            { "ToolbarDatabase", "🗄️  Database Selector Dropdown" },
            { "ToolbarExecute", "▶  Execute Query (F5)" },
            { "ToolbarNewQuery", "📄  New Query Tab (Ctrl+N)" },
            { "ToolbarComment", "--  Comment Selection (Ctrl+K)" },
            { "ToolbarUncomment", "--× Uncomment Selection (Ctrl+Shift+K)" },
            { "ToolbarSave", "💾  Save Query File (Ctrl+S)" },
            { "ToolbarSaveAs", "💾+ Save Query As (Ctrl+Shift+S)" },
            { "ToolbarOpen", "📂  Open SQL File (Ctrl+O)" },
            { "ToolbarQueryTools", "\u2692 Tools (Query actions / SQL Agent)" },
            { "ToolbarSettings", "⚙️  Settings Button" },
            { "ToolbarQueryHistory", "🕘  Query Execution History" },
            { "ToolbarInsertScript", "📝  Insert Script Snippet" }
        };

        private List<ToolbarOrderItem> _items = new();

        public ToolbarOrderWindow()
        {
            InitializeComponent();
            LoadCurrentToolbarOrder();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            int enabled = 1;
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            int result = DwmSetWindowAttribute(
                hwnd,
                DwmwaUseImmersiveDarkMode,
                ref enabled,
                sizeof(int));

            if (result != 0)
            {
                DwmSetWindowAttribute(
                    hwnd,
                    DwmwaUseImmersiveDarkModeBefore20H1,
                    ref enabled,
                    sizeof(int));
            }
        }

        private void LoadCurrentToolbarOrder()
        {
            var currentOrder = AppSettings.Current.Ui.ToolbarOrder ?? new List<string>();
            var itemDict = KnownToolbarItems.ToDictionary(k => k.Key, v => v.Value);

            _items.Clear();

            // Load saved order first
            foreach (string name in currentOrder)
            {
                if (itemDict.Remove(name, out string? displayName))
                {
                    _items.Add(new ToolbarOrderItem { Name = name, DisplayName = displayName });
                }
            }

            // Append any remaining known items
            foreach (var kvp in itemDict)
            {
                _items.Add(new ToolbarOrderItem { Name = kvp.Key, DisplayName = kvp.Value });
            }

            RefreshListBox();
        }

        private void RefreshListBox(int selectedIndex = -1)
        {
            LstToolbarItems.ItemsSource = null;
            LstToolbarItems.ItemsSource = _items;
            if (selectedIndex >= 0 && selectedIndex < _items.Count)
            {
                LstToolbarItems.SelectedIndex = selectedIndex;
            }
        }

        private void HeaderGrid_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
                Close();
            }
        }

        private void BtnMoveUp_Click(object sender, RoutedEventArgs e)
        {
            int index = LstToolbarItems.SelectedIndex;
            if (index > 0)
            {
                var item = _items[index];
                _items.RemoveAt(index);
                _items.Insert(index - 1, item);
                RefreshListBox(index - 1);
            }
        }

        private void BtnMoveDown_Click(object sender, RoutedEventArgs e)
        {
            int index = LstToolbarItems.SelectedIndex;
            if (index >= 0 && index < _items.Count - 1)
            {
                var item = _items[index];
                _items.RemoveAt(index);
                _items.Insert(index + 1, item);
                RefreshListBox(index + 1);
            }
        }

        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            _items = KnownToolbarItems
                .Select(kvp => new ToolbarOrderItem { Name = kvp.Key, DisplayName = kvp.Value })
                .ToList();
            RefreshListBox(0);
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var settings = AppSettings.Current;
                settings.Ui.ToolbarOrder = _items.Select(i => i.Name).ToList();
                AppSettings.Save(settings);

                // Apply instantly on active MainWindow
                if (Application.Current.MainWindow is MainWindow mainWindow)
                {
                    mainWindow.ApplyToolbarOrder();
                }

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "Failed to save toolbar order.");
                MessageBox.Show($"Failed to save toolbar order: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
