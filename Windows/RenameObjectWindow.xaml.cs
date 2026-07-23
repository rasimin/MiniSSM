using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SSMS
{
    public partial class RenameObjectWindow : Window
    {
        private readonly string _objectType;
        private readonly string _currentName;
        private readonly string _databaseName;
        private readonly string? _parentName;

        public string GeneratedSql { get; private set; } = string.Empty;
        public bool IsGenerated { get; private set; } = false;

        public RenameObjectWindow(string objectType, string currentName, string databaseName, string? parentName = null)
        {
            InitializeComponent();

            _objectType = objectType;
            _currentName = currentName;
            _databaseName = databaseName;
            _parentName = parentName;

            TxtObjectType.Text = _objectType;
            TxtCurrentName.Text = string.IsNullOrEmpty(_parentName) ? _currentName : $"{_parentName}.{_currentName}";

            if (_objectType == "Database")
            {
                TxtHelperNote.Text = "Catatan: Masukkan nama database baru.";
            }
            else if (_objectType == "Column")
            {
                TxtHelperNote.Text = "Catatan: Masukkan nama kolom baru.";
            }
            else if (_objectType == "Index")
            {
                TxtHelperNote.Text = "Catatan: Masukkan nama indeks baru.";
            }
            else
            {
                TxtHelperNote.Text = "Catatan: Cukup masukkan nama objek baru (tanpa nama skema).";
            }

            TxtNewName.Focus();
        }

        private void HeaderGrid_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
                Close();
            }
            else if (e.Key == Key.Enter && BtnGenerate.IsEnabled)
            {
                BtnGenerate_Click(sender, e);
            }
        }

        private void TxtNewName_TextChanged(object sender, TextChangedEventArgs e)
        {
            string text = TxtNewName.Text.Trim();
            TxtPlaceholder.Visibility = string.IsNullOrEmpty(text) ? Visibility.Visible : Visibility.Collapsed;

            string simpleCurrent = GetSimpleName(_currentName);
            string simpleNew = GetSimpleName(text);

            BtnGenerate.IsEnabled = !string.IsNullOrWhiteSpace(text) && 
                                    !string.Equals(simpleCurrent, simpleNew, StringComparison.OrdinalIgnoreCase);
        }

        private void BtnGenerate_Click(object sender, RoutedEventArgs e)
        {
            string rawNewName = TxtNewName.Text.Trim();
            string newSimpleName = GetSimpleName(rawNewName);

            if (string.IsNullOrWhiteSpace(newSimpleName))
            {
                return;
            }

            string sql;
            if (string.Equals(_objectType, "Database", StringComparison.OrdinalIgnoreCase))
            {
                sql = $"USE [master];\nGO\nALTER DATABASE {QuoteSqlIdentifier(_currentName)} MODIFY NAME = {QuoteSqlIdentifier(newSimpleName)};\nGO\n";
            }
            else if (string.Equals(_objectType, "Column", StringComparison.OrdinalIgnoreCase))
            {
                string targetObj = !string.IsNullOrEmpty(_parentName) ? $"{_parentName}.{_currentName}" : _currentName;
                sql = $"USE {QuoteSqlIdentifier(_databaseName)};\nGO\nEXEC sp_rename '{targetObj}', '{newSimpleName}', 'COLUMN';\nGO\n";
            }
            else if (string.Equals(_objectType, "Index", StringComparison.OrdinalIgnoreCase))
            {
                string targetObj = !string.IsNullOrEmpty(_parentName) ? $"{_parentName}.{_currentName}" : _currentName;
                sql = $"USE {QuoteSqlIdentifier(_databaseName)};\nGO\nEXEC sp_rename '{targetObj}', '{newSimpleName}', 'INDEX';\nGO\n";
            }
            else
            {
                // Table, View, StoredProcedure, Function, Trigger
                sql = $"USE {QuoteSqlIdentifier(_databaseName)};\nGO\nEXEC sp_rename '{_currentName}', '{newSimpleName}';\nGO\n";
            }

            GeneratedSql = sql;
            IsGenerated = true;
            DialogResult = true;
            Close();
        }

        private static string GetSimpleName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;

            string cleaned = name.Replace("[", "").Replace("]", "").Trim();
            int lastDot = cleaned.LastIndexOf('.');
            if (lastDot >= 0 && lastDot < cleaned.Length - 1)
            {
                return cleaned.Substring(lastDot + 1);
            }
            return cleaned;
        }

        private static string QuoteSqlIdentifier(string identifier)
        {
            string clean = identifier.Replace("[", "").Replace("]", "");
            return "[" + clean.Replace("]", "]]") + "]";
        }
    }
}
