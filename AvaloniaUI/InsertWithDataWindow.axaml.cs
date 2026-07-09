using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Data.SqlClient;

namespace SSMS;

public partial class InsertWithDataWindow : Window
{
    private readonly string _connectionString;
    private readonly string _databaseName;
    private readonly string _tableName;

    public InsertWithDataWindow() : this("", "", "") { }

    public InsertWithDataWindow(string connectionString, string databaseName, string tableName)
    {
        _connectionString = connectionString;
        _databaseName = databaseName;
        _tableName = tableName;
        InitializeComponent();
        TableNameText.Text = tableName;
    }

    private async void Generate_OnClick(object? sender, RoutedEventArgs e)
    {
        GenerateButton.IsEnabled = false;
        StatusText.Text = "Generating script...";
        try
        {
            var sql = await GenerateAsync(WhereBox.Text ?? "", VariablesBox.IsChecked == true);
            Close(sql);
        }
        catch (Exception ex)
        {
            AppLogger.Error(ex, "Failed to generate insert script with data.");
            StatusText.Text = ex.Message;
        }
        finally
        {
            GenerateButton.IsEnabled = true;
        }
    }

    private async Task<string> GenerateAsync(string whereClause, bool useVariables)
    {
        var safeTableName = string.Join(".", _tableName.Split('.').Select(x => $"[{x.Replace("]", "]]")}]"));
        var identityColumns = new List<string>();
        await using (var connection = new SqlConnection(
            DatabaseHelper.BuildConnectionString(_connectionString, _databaseName)))
        {
            await connection.OpenAsync();
            const string query = """
                SELECT c.name
                FROM sys.columns c
                JOIN sys.identity_columns ic ON c.object_id=ic.object_id AND c.column_id=ic.column_id
                WHERE c.object_id=OBJECT_ID(@TableFullName);
                """;
            await using var command = new SqlCommand(query, connection);
            command.Parameters.AddWithValue("@TableFullName", _tableName);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) identityColumns.Add(reader.GetString(0));
        }

        var columns = await DatabaseHelper.GetColumnsAsync(
            _connectionString, _databaseName, _tableName);
        if (columns.Count == 0) throw new InvalidOperationException("Table columns were not found.");

        var select = $"SELECT TOP 1000 * FROM {safeTableName}";
        if (!string.IsNullOrWhiteSpace(whereClause)) select += $" WHERE {whereClause}";
        var result = await DatabaseHelper.ExecuteQueryAsync(_connectionString, _databaseName, select);
        if (!result.IsSuccess) throw new InvalidOperationException(result.Message);
        if (result.DataTables.Count == 0 || result.DataTables[0].Rows.Count == 0)
            return $"-- No rows found in {_tableName} matching conditions.";

        var table = result.DataTables[0];
        var active = columns.Where(x => table.Columns.Contains(x.ColumnName))
            .Select(x => (x.ColumnName, x.DataType,
                Variable: new string(x.ColumnName.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray())))
            .ToList();
        var builder = new StringBuilder();
        if (useVariables)
        {
            builder.AppendLine("-- T-SQL Variable Declarations");
            foreach (var column in active)
                builder.AppendLine($"DECLARE @{column.Variable} {column.DataType};");
            builder.AppendLine();
        }
        if (identityColumns.Count > 0)
            builder.AppendLine($"SET IDENTITY_INSERT {safeTableName} ON;\n");

        var columnList = string.Join(", ", active.Select(x => $"[{x.ColumnName}]"));
        for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
        {
            var row = table.Rows[rowIndex];
            builder.AppendLine($"-- Row {rowIndex + 1}");
            if (useVariables)
            {
                foreach (var column in active)
                    builder.AppendLine($"SET @{column.Variable} = {FormatSqlValue(row[column.ColumnName])};");
                builder.AppendLine($"INSERT INTO {safeTableName} ({columnList})");
                builder.AppendLine($"VALUES ({string.Join(", ", active.Select(x => $"@{x.Variable}"))});\n");
            }
            else
            {
                builder.AppendLine($"INSERT INTO {safeTableName} ({columnList})");
                builder.AppendLine($"VALUES ({string.Join(", ", active.Select(x => FormatSqlValue(row[x.ColumnName]))) });\n");
            }
        }
        if (identityColumns.Count > 0)
            builder.AppendLine($"SET IDENTITY_INSERT {safeTableName} OFF;");
        return builder.ToString();
    }

    private static string FormatSqlValue(object? value) => value switch
    {
        null or DBNull => "NULL",
        bool boolean => boolean ? "1" : "0",
        byte or short or int or long or float or double or decimal =>
            Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "NULL",
        DateTime dateTime => $"'{dateTime:yyyy-MM-dd HH:mm:ss.fff}'",
        byte[] bytes => $"0x{Convert.ToHexString(bytes)}",
        _ => $"N'{(value.ToString() ?? "").Replace("'", "''")}'"
    };

    private void Cancel_OnClick(object? sender, RoutedEventArgs e) => Close(null);
}
