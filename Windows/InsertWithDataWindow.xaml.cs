using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SSMS
{
    public partial class InsertWithDataWindow : Window
    {
        private readonly string _connectionString;
        private readonly string _databaseName;
        private readonly string _tableName;

        public string GeneratedSql { get; private set; } = string.Empty;
        public bool IsGenerated { get; private set; } = false;

        public InsertWithDataWindow(string connectionString, string databaseName, string tableName)
        {
            InitializeComponent();
            _connectionString = connectionString;
            _databaseName = databaseName;
            _tableName = tableName;

            TxtTableName.Text = tableName;
        }

        private void HeaderGrid_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                this.DragMove();
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void TxtWhereClause_TextChanged(object sender, TextChangedEventArgs e)
        {
            TxtPlaceholder.Visibility = string.IsNullOrEmpty(TxtWhereClause.Text) ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void BtnGenerate_Click(object sender, RoutedEventArgs e)
        {
            var oldCursor = Cursor;
            Cursor = Cursors.Wait;

            try
            {
                string where = TxtWhereClause.Text;
                bool useVars = ChkUseVariables.IsChecked == true;
                bool excludeIdentityAndComputed = ChkExcludeIdentityAndComputed.IsChecked == true;
                
                GeneratedSql = await GenerateInsertWithDataScriptAsync(_connectionString, _databaseName, _tableName, where, useVars, excludeIdentityAndComputed);
                IsGenerated = true;
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "Failed to generate insert script with data");
                MessageBox.Show($"Failed to generate script: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Cursor = oldCursor;
            }
        }

        private async Task<string> GenerateInsertWithDataScriptAsync(string connectionString, string databaseName, string tableName, string whereClause, bool useVariables, bool excludeIdentityAndComputed)
        {
            string safeTableName = tableName;
            var parts = tableName.Split('.');
            if (parts.Length == 2)
            {
                safeTableName = $"[{parts[0]}].[{parts[1]}]";
            }
            else
            {
                safeTableName = $"[{tableName}]";
            }

            // 1. Get identity and computed columns
            var identityColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var computedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dbConnString = DatabaseHelper.BuildConnectionString(connectionString, databaseName);
            using (var connection = new Microsoft.Data.SqlClient.SqlConnection(dbConnString))
            {
                await connection.OpenAsync();
                var query = @"
                    SELECT c.name, c.is_identity, c.is_computed 
                    FROM sys.columns c 
                    WHERE c.object_id = OBJECT_ID(@TableFullName);";
                using (var command = new Microsoft.Data.SqlClient.SqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@TableFullName", tableName);
                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            string colName = reader.GetString(0);
                            bool isIdentity = reader.GetBoolean(1);
                            bool isComputed = reader.GetBoolean(2);
                            if (isIdentity) identityColumns.Add(colName);
                            if (isComputed) computedColumns.Add(colName);
                        }
                    }
                }
            }

            // 2. Get columns and types
            var columns = await DatabaseHelper.GetColumnsAsync(connectionString, databaseName, tableName);
            if (columns == null || columns.Count == 0)
            {
                throw new Exception("Columns not found or table does not exist.");
            }

            // 3. Fetch the data
            string selectQuery = $"SELECT TOP 1000 * FROM {safeTableName}";
            if (!string.IsNullOrWhiteSpace(whereClause))
            {
                selectQuery += $" WHERE {whereClause}";
            }

            var queryResult = await DatabaseHelper.ExecuteQueryAsync(connectionString, databaseName, selectQuery);
            if (!queryResult.IsSuccess)
            {
                throw new Exception($"Failed to fetch table data: {queryResult.Message}");
            }

            if (queryResult.DataTables.Count == 0 || queryResult.DataTables[0].Rows.Count == 0)
            {
                return $"-- No rows found in {tableName} matching conditions.";
            }

            var dataTable = queryResult.DataTables[0];
            var sb = new StringBuilder();

            // Prepare list of columns that exist in the select results
            var activeCols = new List<(string Name, string DataType, string VarName)>();
            foreach (var col in columns)
            {
                if (dataTable.Columns.Contains(col.ColumnName))
                {
                    if (excludeIdentityAndComputed && (identityColumns.Contains(col.ColumnName) || computedColumns.Contains(col.ColumnName)))
                    {
                        continue;
                    }

                    string varName = col.ColumnName.Replace(" ", "_");
                    varName = new string(varName.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
                    activeCols.Add((col.ColumnName, col.DataType, varName));
                }
            }

            if (activeCols.Count == 0)
            {
                throw new Exception("No matching columns in the result table.");
            }

            // Generate DECLARE statements if useVariables is true
            if (useVariables)
            {
                sb.AppendLine("-- T-SQL Variable Declarations");
                foreach (var col in activeCols)
                {
                    sb.AppendLine($"DECLARE @{col.VarName} {col.DataType};");
                }
                sb.AppendLine();
            }

            bool hasIdentity = activeCols.Any(c => identityColumns.Contains(c.Name));
            if (hasIdentity)
            {
                sb.AppendLine($"SET IDENTITY_INSERT {safeTableName} ON;");
                sb.AppendLine();
            }

            // Build INSERT statement template
            string columnsList = string.Join(", ", activeCols.Select(c => $"[{c.Name}]"));

            for (int r = 0; r < dataTable.Rows.Count; r++)
            {
                var row = dataTable.Rows[r];
                sb.AppendLine($"-- Row {r + 1}");

                if (useVariables)
                {
                    // SET values
                    foreach (var col in activeCols)
                    {
                        var val = row[col.Name];
                        sb.AppendLine($"SET @{col.VarName} = {FormatSqlValue(val)};");
                    }
                    
                    // INSERT
                    sb.AppendLine($"INSERT INTO {safeTableName} ({columnsList})");
                    sb.AppendLine($"VALUES (" + string.Join(", ", activeCols.Select(c => $"@{c.VarName}")) + ");");
                }
                else
                {
                    // Direct values
                    var valStrings = activeCols.Select(c => FormatSqlValue(row[c.Name]));
                    sb.AppendLine($"INSERT INTO {safeTableName} ({columnsList})");
                    sb.AppendLine($"VALUES (" + string.Join(", ", valStrings) + ");");
                }
                sb.AppendLine();
            }

            if (hasIdentity)
            {
                sb.AppendLine($"SET IDENTITY_INSERT {safeTableName} OFF;");
            }

            return sb.ToString();
        }

        private string FormatSqlValue(object value)
        {
            if (value == null || value == DBNull.Value)
            {
                return "NULL";
            }

            if (value is bool b)
            {
                return b ? "1" : "0";
            }

            if (value is int || value is long || value is short || value is byte || value is double || value is float || value is decimal)
            {
                return value.ToString() ?? "NULL";
            }

            if (value is DateTime dt)
            {
                return $"'{dt:yyyy-MM-dd HH:mm:ss.fff}'";
            }

            // string or other types
            string str = value.ToString() ?? "";
            return $"N'{str.Replace("'", "''")}'";
        }
    }
}
