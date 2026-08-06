using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ExcelDataReader;
using Microsoft.Data.SqlClient;
using SSMS.Utilities;

namespace SSMS
{
    public partial class ImportExcelWindow : Window
    {
        private readonly string _connectionString;
        private readonly string _databaseName;
        private DataSet? _excelDataSet;
        private string? _loadedFilePath;

        public bool IsImportSuccessful { get; private set; }
        public string ImportedTableName { get; private set; } = string.Empty;

        static ImportExcelWindow()
        {
            // Register CodePagesEncodingProvider for ExcelDataReader legacy .xls files support
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        public ImportExcelWindow(string connectionString, string databaseName)
        {
            InitializeComponent();
            _connectionString = connectionString;
            _databaseName = databaseName;
            TxtTargetDatabase.Text = databaseName;
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
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
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

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private int GetMaxPreviewRows()
        {
            if (int.TryParse(TxtMaxRows.Text, out int rows) && rows > 0)
            {
                return Math.Clamp(rows, 1, 100000);
            }
            return 100;
        }

        private void TxtMaxRows_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TxtPreviewHeader != null)
            {
                int rows = GetMaxPreviewRows();
                TxtPreviewHeader.Text = $"Preview Data & Kolom Terdeteksi (Max {rows} Sample Baris):";
            }
        }

        private async void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var (result, fileNames) = await FileDialogHelper.ShowOpenFileDialogAsync(
                    filter: "Excel Files (*.xlsx;*.xls)|*.xlsx;*.xls|All Files (*.*)|*.*",
                    defaultExt: ".xlsx",
                    title: "Pilih File Excel",
                    multiselect: false,
                    initialDirectory: null,
                    ownerWindow: this);

                if (!result || fileNames.Length == 0) return;

                string filePath = fileNames[0];
                await LoadExcelFileAsync(filePath);
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "Error browsing Excel file");
                DarkMessageBoxWindow.Show(this, $"Terjadi kesalahan saat memilih file Excel: {ex.Message}", "Error Browse", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            _excelDataSet = null;
            _loadedFilePath = null;
            TxtFilePath.Text = string.Empty;
            CmbSheet.Items.Clear();
            GridPreview.ItemsSource = null;
            TxtTableName.Text = string.Empty;

            TxtMaxRows.IsEnabled = true;
            BtnReset.IsEnabled = false;
            BtnImport.IsEnabled = false;

            TxtStatus.Text = "Form direset. Silakan atur Max Sample Baris lalu pilih file Excel.";
        }

        private async Task LoadExcelFileAsync(string filePath)
        {
            TxtStatus.Text = "Membaca file Excel...";
            ProgressBarImport.Visibility = Visibility.Visible;
            ProgressBarImport.IsIndeterminate = true;

            try
            {
                await Task.Run(() =>
                {
                    using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = ExcelReaderFactory.CreateReader(stream);

                    _excelDataSet = reader.AsDataSet(new ExcelDataSetConfiguration
                    {
                        ConfigureDataTable = _ => new ExcelDataTableConfiguration
                        {
                            UseHeaderRow = false // We handle header row and deduplication manually
                        }
                    });
                });

                _loadedFilePath = filePath;
                TxtFilePath.Text = filePath;

                // Disable MaxRows textbox once file is loaded and enable Reset button
                TxtMaxRows.IsEnabled = false;
                BtnReset.IsEnabled = true;

                // Populate Sheets dropdown
                CmbSheet.Items.Clear();
                if (_excelDataSet != null && _excelDataSet.Tables.Count > 0)
                {
                    foreach (DataTable table in _excelDataSet.Tables)
                    {
                        CmbSheet.Items.Add(table.TableName);
                    }
                    CmbSheet.SelectedIndex = 0;

                    // Set default target table name from file name or sheet name
                    string defaultName = Path.GetFileNameWithoutExtension(filePath);
                    TxtTableName.Text = CleanSqlIdentifier(defaultName);
                }
                else
                {
                    DarkMessageBoxWindow.Show(this, "Engine/Provider Excel tidak mendukung file ini atau format tidak valid.", "Engine Tidak Mendukung", MessageBoxButton.OK, MessageBoxImage.Warning);
                    TxtStatus.Text = "File Excel kosong atau tidak dapat dibaca.";
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"Failed to load Excel file: {filePath}");
                DarkMessageBoxWindow.Show(this, $"Engine/Provider Excel tidak mendukung file ini atau format tidak valid.\n\nDetail Error: {ex.Message}", "Engine Tidak Mendukung", MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtStatus.Text = "Gagal membaca file Excel.";
            }
            finally
            {
                ProgressBarImport.Visibility = Visibility.Collapsed;
            }
        }

        private void CmbSheet_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdatePreview();
        }

        private void ChkFirstRowHeader_Click(object sender, RoutedEventArgs e)
        {
            UpdatePreview();
        }

        private void TxtTableName_TextChanged(object sender, TextChangedEventArgs e)
        {
            ValidateInputs();
        }

        private void ValidateInputs()
        {
            bool isValid = !string.IsNullOrWhiteSpace(TxtTableName.Text) &&
                           _excelDataSet != null &&
                           CmbSheet.SelectedIndex >= 0;

            BtnImport.IsEnabled = isValid;
        }

        private void UpdatePreview()
        {
            if (_excelDataSet == null || CmbSheet.SelectedIndex < 0)
            {
                GridPreview.ItemsSource = null;
                ValidateInputs();
                return;
            }

            try
            {
                DataTable rawTable = _excelDataSet.Tables[CmbSheet.SelectedIndex]!;
                bool hasHeaders = ChkFirstRowHeader.IsChecked == true;
                int maxRows = GetMaxPreviewRows();

                TxtPreviewHeader.Text = $"Preview Data & Kolom Terdeteksi (Max {maxRows} Sample Baris):";

                DataTable previewTable = ProcessExcelData(rawTable, hasHeaders, maxRows: maxRows);
                GridPreview.ItemsSource = previewTable.DefaultView;

                TxtStatus.Text = $"Siap import. Sheet '{CmbSheet.SelectedItem}' berisi {rawTable.Rows.Count} baris data.";
                ValidateInputs();
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "Error updating Excel preview");
                TxtStatus.Text = $"Error preview: {ex.Message}";
                BtnImport.IsEnabled = false;
            }
        }

        private static bool IsRowCompletelyEmpty(DataRow row, int totalCols)
        {
            for (int c = 0; c < totalCols; c++)
            {
                if (!IsNullValue(row[c])) return false;
            }
            return true;
        }

        private DataTable ProcessExcelData(DataTable rawTable, bool hasHeaders, int maxRows = -1)
        {
            var resultTable = new DataTable();
            if (rawTable.Rows.Count == 0) return resultTable;

            int startRowIndex = hasHeaders ? 1 : 0;
            var columnNames = new List<string>();
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Determine Column Names with Auto-Deduplication
            int totalCols = rawTable.Columns.Count;
            for (int col = 0; col < totalCols; col++)
            {
                string rawHeader = "";
                if (hasHeaders && rawTable.Rows.Count > 0)
                {
                    rawHeader = rawTable.Rows[0][col]?.ToString()?.Trim() ?? "";
                }

                if (string.IsNullOrWhiteSpace(rawHeader))
                {
                    rawHeader = $"Column_{col + 1}";
                }
                else
                {
                    rawHeader = CleanSqlIdentifier(rawHeader);
                }

                // Deduplicate header if duplicate name exists (e.g. Nama, Nama -> Nama, Nama_2)
                string uniqueHeader = rawHeader;
                int suffix = 2;
                while (usedNames.Contains(uniqueHeader))
                {
                    uniqueHeader = $"{rawHeader}_{suffix}";
                    suffix++;
                }

                usedNames.Add(uniqueHeader);
                columnNames.Add(uniqueHeader);
                resultTable.Columns.Add(uniqueHeader, typeof(string));
            }

            // Populate Rows - skipping completely empty rows
            int endRow = rawTable.Rows.Count;
            int rowCounter = 0;
            for (int r = startRowIndex; r < endRow; r++)
            {
                DataRow rawRow = rawTable.Rows[r];
                if (IsRowCompletelyEmpty(rawRow, totalCols))
                {
                    continue; // Skip blank / space-only rows
                }

                DataRow newRow = resultTable.NewRow();
                for (int c = 0; c < totalCols; c++)
                {
                    object? val = rawRow[c];
                    newRow[c] = IsNullValue(val) ? DBNull.Value : val?.ToString()?.Trim();
                }
                resultTable.Rows.Add(newRow);
                rowCounter++;

                if (maxRows > 0 && rowCounter >= maxRows) break;
            }

            return resultTable;
        }

        private async void BtnImport_Click(object sender, RoutedEventArgs e)
        {
            if (_excelDataSet == null || CmbSheet.SelectedIndex < 0) return;

            string tableName = TxtTableName.Text.Trim();
            if (string.IsNullOrWhiteSpace(tableName))
            {
                DarkMessageBoxWindow.Show(this, "Silakan masukkan nama tabel yang valid.", "Nama Tabel Kosong", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Prepare connection string targeting selected database
            var builder = new SqlConnectionStringBuilder(_connectionString)
            {
                InitialCatalog = _databaseName
            };
            string dbConnString = builder.ConnectionString;

            BtnImport.IsEnabled = false;
            BtnBrowse.IsEnabled = false;
            BtnReset.IsEnabled = false;
            CmbSheet.IsEnabled = false;
            TxtTableName.IsEnabled = false;

            ProgressBarImport.Visibility = Visibility.Visible;
            ProgressBarImport.IsIndeterminate = true;
            TxtStatus.Text = $"Membuat tabel [{tableName}] di database '{_databaseName}'...";

            bool tableCreated = false;

            try
            {
                DataTable rawTable = _excelDataSet.Tables[CmbSheet.SelectedIndex]!;
                bool hasHeaders = ChkFirstRowHeader.IsChecked == true;
                int maxSamples = GetMaxPreviewRows();

                // Process full data (filtering empty rows)
                DataTable dataToImport = await Task.Run(() => ProcessExcelData(rawTable, hasHeaders, maxRows: -1));

                // 1. Create SQL Table (analyzing up to maxSamples non-empty rows)
                string createTableSql = BuildCreateTableScript(tableName, dataToImport, maxSamples);
                await using (var connection = new SqlConnection(dbConnString))
                {
                    await connection.OpenAsync();
                    await using var command = new SqlCommand(createTableSql, connection);
                    await command.ExecuteNonQueryAsync();
                }

                tableCreated = true;

                // 2. Perform SqlBulkCopy
                TxtStatus.Text = $"Mengisi {dataToImport.Rows.Count} baris data ke tabel [{tableName}]...";
                await Task.Run(() =>
                {
                    using var bulkCopy = new SqlBulkCopy(dbConnString, SqlBulkCopyOptions.Default)
                    {
                        DestinationTableName = $"[{tableName}]",
                        BatchSize = 5000,
                        BulkCopyTimeout = 300
                    };

                    foreach (DataColumn col in dataToImport.Columns)
                    {
                        bulkCopy.ColumnMappings.Add(col.ColumnName, col.ColumnName);
                    }

                    bulkCopy.WriteToServer(dataToImport);
                });

                IsImportSuccessful = true;
                ImportedTableName = tableName;

                TxtStatus.Text = "Import berhasil!";
                DarkMessageBoxWindow.Show(this, $"Berhasil mengimpor {dataToImport.Rows.Count} baris data ke tabel [{tableName}] pada database '{_databaseName}'.", "Import Sukses", MessageBoxButton.OK, MessageBoxImage.Information);
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                if (tableCreated)
                {
                    try
                    {
                        await using var dropConn = new SqlConnection(dbConnString);
                        await dropConn.OpenAsync();
                        await using var dropCmd = new SqlCommand($"IF OBJECT_ID('dbo.[{tableName}]', 'U') IS NOT NULL DROP TABLE [{tableName}];", dropConn);
                        await dropCmd.ExecuteNonQueryAsync();
                    }
                    catch { }
                }

                AppLogger.Error(ex, $"Failed to import Excel to table [{tableName}]");
                DarkMessageBoxWindow.Show(this, $"Gagal mengimpor data ke database: {ex.Message}", "Error Import", MessageBoxButton.OK, MessageBoxImage.Error);
                TxtStatus.Text = "Proses import gagal.";
            }
            finally
            {
                BtnImport.IsEnabled = true;
                BtnBrowse.IsEnabled = true;
                BtnReset.IsEnabled = true;
                CmbSheet.IsEnabled = true;
                TxtTableName.IsEnabled = true;
                ProgressBarImport.Visibility = Visibility.Collapsed;
            }
        }

        private string BuildCreateTableScript(string tableName, DataTable dataTable, int maxSamples)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"CREATE TABLE [{tableName}] (");

            for (int i = 0; i < dataTable.Columns.Count; i++)
            {
                DataColumn col = dataTable.Columns[i];
                string colSqlType = DetectSqlColumnType(dataTable, col.ColumnName, maxSamples);

                sb.Append($"    [{col.ColumnName}] {colSqlType}");
                if (i < dataTable.Columns.Count - 1)
                {
                    sb.AppendLine(",");
                }
                else
                {
                    sb.AppendLine();
                }
            }

            sb.AppendLine(");");
            return sb.ToString();
        }

        private static bool IsNullValue(object? val)
        {
            if (val == null || val == DBNull.Value) return true;
            string str = val.ToString()?.Trim() ?? "";
            if (string.IsNullOrEmpty(str)) return true;

            return string.Equals(str, "NULL", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(str, "null", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(str, "#N/A", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(str, "N/A", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(str, "NaN", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(str, "-", StringComparison.Ordinal);
        }

        private string DetectSqlColumnType(DataTable dataTable, string columnName, int maxSamples)
        {
            bool isInt = true;
            bool isFloat = true;
            bool isDateTime = true;
            bool isBool = true;
            bool hasLeadingZero = false;
            int maxLen = 0;
            int samples = 0;

            foreach (DataRow row in dataTable.Rows)
            {
                object cellVal = row[columnName];
                if (IsNullValue(cellVal)) continue;

                string val = cellVal.ToString()?.Trim() ?? "";
                if (IsNullValue(val)) continue;

                samples++;
                maxLen = Math.Max(maxLen, val.Length);

                // Code/Reference check: Values starting with '0' (like "000000238642") must remain NVARCHAR to preserve format!
                if (val.Length > 1 && val[0] == '0' && !val.StartsWith("0.", StringComparison.Ordinal))
                {
                    hasLeadingZero = true;
                }

                // Strip thousand-separator commas like 1,548,763.63 for numeric parsing
                string cleanNum = val.Replace(",", "");

                if (!int.TryParse(cleanNum, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) isInt = false;
                if (!double.TryParse(cleanNum, NumberStyles.Any, CultureInfo.InvariantCulture, out _)) isFloat = false;
                if (!DateTime.TryParse(val, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) isDateTime = false;
                if (!bool.TryParse(val, out _)) isBool = false;

                if (maxSamples > 0 && samples >= maxSamples) break;
            }

            if (samples == 0) return "NVARCHAR(255) NULL";
            if (hasLeadingZero)
            {
                return maxLen <= 255 ? "NVARCHAR(255) NULL" : "NVARCHAR(MAX) NULL";
            }
            if (isBool) return "BIT NULL";
            if (isInt) return "INT NULL";
            if (isFloat) return "FLOAT NULL";
            if (isDateTime) return "DATETIME2 NULL";

            if (maxLen <= 255) return "NVARCHAR(255) NULL";
            return "NVARCHAR(MAX) NULL";
        }

        private static string CleanSqlIdentifier(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "Table1";
            string clean = Regex.Replace(text, @"[^\w]", "_");
            if (char.IsDigit(clean[0]))
            {
                clean = "_" + clean;
            }
            return clean;
        }
    }
}
