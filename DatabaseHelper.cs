using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace SSMS
{
    public class QueryResult
    {
        public List<DataTable> DataTables { get; set; } = new List<DataTable>();
        public string Message { get; set; } = string.Empty;
        public bool IsSuccess { get; set; }
        public TimeSpan ExecutionTime { get; set; }
    }

    public static class DatabaseHelper
    {
        /// <summary>
        /// Builds a connection string targeting a specific database from a base connection string.
        /// </summary>
        public static string BuildConnectionString(string baseConnString, string databaseName)
        {
            var builder = new SqlConnectionStringBuilder(baseConnString);
            if (!string.IsNullOrEmpty(databaseName))
            {
                builder.InitialCatalog = databaseName;
            }
            return builder.ConnectionString;
        }

        /// <summary>
        /// Tests a connection string to verify it is valid.
        /// </summary>
        public static async Task TestConnectionAsync(string connectionString)
        {
            using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();
            }
        }

        /// <summary>
        /// Gets a list of all databases available on the server.
        /// </summary>
        public static async Task<List<string>> GetDatabasesAsync(string connectionString)
        {
            var databases = new List<string>();
            using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();
                var query = "SELECT name FROM sys.databases ORDER BY name;";
                using (var command = new SqlCommand(query, connection))
                using (var reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        databases.Add(reader.GetString(0));
                    }
                }
            }
            return databases;
        }

        /// <summary>
        /// Gets a list of all user tables in a specific database, formatted as SchemaName.TableName.
        /// </summary>
        public static async Task<List<string>> GetTablesAsync(string connectionString, string databaseName)
        {
            var tables = new List<string>();
            var dbConnString = BuildConnectionString(connectionString, databaseName);
            using (var connection = new SqlConnection(dbConnString))
            {
                await connection.OpenAsync();
                var query = @"
                    SELECT SCHEMA_NAME(schema_id) + '.' + name AS TableName 
                    FROM sys.tables 
                    ORDER BY SCHEMA_NAME(schema_id), name;";
                using (var command = new SqlCommand(query, connection))
                using (var reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        tables.Add(reader.GetString(0));
                    }
                }
            }
            return tables;
        }

        /// <summary>
        /// Gets columns for a given table, returning their names and data types.
        /// </summary>
        public static async Task<List<(string ColumnName, string DataType, bool IsPrimaryKey)>> GetColumnsAsync(string connectionString, string databaseName, string tableSchemaAndName)
        {
            var columns = new List<(string, string, bool)>();
            var dbConnString = BuildConnectionString(connectionString, databaseName);

            using (var connection = new SqlConnection(dbConnString))
            {
                await connection.OpenAsync();
                var query = @"
                    SELECT c.name, t.name + 
                        CASE 
                            WHEN t.name IN ('varchar', 'char', 'nvarchar', 'nchar') THEN '(' + 
                                CASE WHEN c.max_length = -1 THEN 'MAX' 
                                     ELSE CAST(CASE WHEN t.name LIKE 'n%' THEN c.max_length/2 ELSE c.max_length END AS VARCHAR) 
                                END + ')'
                            WHEN t.name IN ('decimal', 'numeric') THEN '(' + CAST(c.precision AS VARCHAR) + ',' + CAST(c.scale AS VARCHAR) + ')'
                            ELSE ''
                        END AS DataType,
                        CAST(ISNULL((SELECT 1 FROM sys.index_columns ic 
                                JOIN sys.indexes i ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                                WHERE ic.object_id = c.object_id AND ic.column_id = c.column_id AND i.is_primary_key = 1), 0) AS BIT) AS IsPrimaryKey
                    FROM sys.columns c
                    JOIN sys.types t ON c.user_type_id = t.user_type_id
                    WHERE c.object_id = OBJECT_ID(@TableFullName)
                    ORDER BY c.column_id;";

                using (var command = new SqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@TableFullName", tableSchemaAndName);
                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            columns.Add((reader.GetString(0), reader.GetString(1), reader.GetBoolean(2)));
                        }
                    }
                }
            }
            return columns;
        }

        public static async Task<List<(string IndexName, string IndexType, bool IsUnique, bool IsPrimaryKey)>> GetIndexesAsync(string connectionString, string databaseName, string tableSchemaAndName)
        {
            var indexes = new List<(string, string, bool, bool)>();
            var dbConnString = BuildConnectionString(connectionString, databaseName);

            using (var connection = new SqlConnection(dbConnString))
            {
                await connection.OpenAsync();
                var query = @"
                    SELECT i.name,
                           i.type_desc,
                           i.is_unique,
                           i.is_primary_key
                    FROM sys.indexes i
                    WHERE i.object_id = OBJECT_ID(@TableFullName)
                      AND i.name IS NOT NULL
                    ORDER BY i.is_primary_key DESC, i.is_unique DESC, i.name;";

                using (var command = new SqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@TableFullName", tableSchemaAndName);
                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            indexes.Add((reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), reader.GetBoolean(3)));
                        }
                    }
                }
            }
            return indexes;
        }

        public static async Task<List<(string TriggerName, bool IsDisabled)>> GetTriggersAsync(string connectionString, string databaseName, string tableSchemaAndName)
        {
            var triggers = new List<(string, bool)>();
            var dbConnString = BuildConnectionString(connectionString, databaseName);

            using (var connection = new SqlConnection(dbConnString))
            {
                await connection.OpenAsync();
                var query = @"
                    SELECT tr.name,
                           tr.is_disabled
                    FROM sys.triggers tr
                    WHERE tr.parent_id = OBJECT_ID(@TableFullName)
                    ORDER BY tr.name;";

                using (var command = new SqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@TableFullName", tableSchemaAndName);
                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            triggers.Add((reader.GetString(0), reader.GetBoolean(1)));
                        }
                    }
                }
            }
            return triggers;
        }

        /// <summary>
        /// Executes an SQL query or command against a database and returns the result, message, execution time.
        /// </summary>
        public static async Task<QueryResult> ExecuteQueryAsync(string connectionString, string databaseName, string sqlQuery)
        {
            var result = new QueryResult();
            var dbConnString = BuildConnectionString(connectionString, databaseName);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                using (var connection = new SqlConnection(dbConnString))
                {
                    await connection.OpenAsync();

                    var messages = new System.Text.StringBuilder();
                    connection.InfoMessage += (sender, e) =>
                    {
                        messages.AppendLine(e.Message);
                    };

                    using (var command = new SqlCommand(sqlQuery, connection))
                    {
                        command.CommandTimeout = AppSettings.Current.Query.CommandTimeoutSeconds;
                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            do
                            {
                                if (reader.FieldCount > 0)
                                {
                                    var dataTable = new DataTable();
                                    
                                    // SQL Server permits duplicate result column names (for example:
                                    // SELECT Units, *). DataTable requires every column name to be
                                    // unique, so only the display name of duplicates is suffixed.
                                    var usedColumnNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                    for (int i = 0; i < reader.FieldCount; i++)
                                    {
                                        string baseName = reader.GetName(i);
                                        if (string.IsNullOrWhiteSpace(baseName))
                                        {
                                            baseName = $"Column{i + 1}";
                                        }

                                        string uniqueName = baseName;
                                        int duplicateNumber = 2;
                                        while (!usedColumnNames.Add(uniqueName))
                                        {
                                            uniqueName = $"{baseName} ({duplicateNumber++})";
                                        }

                                        dataTable.Columns.Add(uniqueName, reader.GetFieldType(i));
                                    }

                                    // Manually load rows
                                    while (await reader.ReadAsync())
                                    {
                                        var row = dataTable.NewRow();
                                        for (int i = 0; i < reader.FieldCount; i++)
                                        {
                                            row[i] = reader.GetValue(i);
                                        }
                                        dataTable.Rows.Add(row);
                                    }

                                    result.DataTables.Add(dataTable);
                                }
                                else
                                {
                                    int rowsAffected = reader.RecordsAffected;
                                    if (rowsAffected >= 0)
                                    {
                                        messages.AppendLine($"({rowsAffected} row(s) affected)");
                                    }
                                }
                            } while (await reader.NextResultAsync());
                        }
                    }

                    stopwatch.Stop();
                    result.IsSuccess = true;
                    result.ExecutionTime = stopwatch.Elapsed;
                    result.Message = messages.Length > 0 ? messages.ToString() : "Command completed successfully.";
                }
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                result.IsSuccess = false;
                result.ExecutionTime = stopwatch.Elapsed;
                result.Message = $"Msg {ex.HResult}, Level, State\n{ex.Message}";
            }

            return result;
        }

        public static async Task<List<string>> GetViewsAsync(string connectionString, string databaseName)
        {
            var views = new List<string>();
            var dbConnString = BuildConnectionString(connectionString, databaseName);
            using (var connection = new SqlConnection(dbConnString))
            {
                await connection.OpenAsync();
                var query = @"
                    SELECT SCHEMA_NAME(schema_id) + '.' + name AS ViewName 
                    FROM sys.views 
                    ORDER BY SCHEMA_NAME(schema_id), name;";
                using (var command = new SqlCommand(query, connection))
                using (var reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        views.Add(reader.GetString(0));
                    }
                }
            }
            return views;
        }

        public static async Task<List<string>> GetStoredProceduresAsync(string connectionString, string databaseName)
        {
            var sps = new List<string>();
            var dbConnString = BuildConnectionString(connectionString, databaseName);
            using (var connection = new SqlConnection(dbConnString))
            {
                await connection.OpenAsync();
                var query = @"
                    SELECT SCHEMA_NAME(schema_id) + '.' + name AS ProcedureName 
                    FROM sys.procedures 
                    ORDER BY SCHEMA_NAME(schema_id), name;";
                using (var command = new SqlCommand(query, connection))
                using (var reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        sps.Add(reader.GetString(0));
                    }
                }
            }
            return sps;
        }

        public static async Task<List<string>> GetFunctionsAsync(string connectionString, string databaseName, bool tableValued)
        {
            var funcs = new List<string>();
            var dbConnString = BuildConnectionString(connectionString, databaseName);
            using (var connection = new SqlConnection(dbConnString))
            {
                await connection.OpenAsync();
                var query = @"
                    SELECT SCHEMA_NAME(schema_id) + '.' + name AS FunctionName, type
                    FROM sys.objects 
                    WHERE type IN ('FN', 'IF', 'TF', 'FS', 'FT')
                    ORDER BY SCHEMA_NAME(schema_id), name;";
                using (var command = new SqlCommand(query, connection))
                using (var reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        string objectType = reader.GetString(1);
                        bool isTableValued = objectType is "IF" or "TF" or "FT";
                        if (isTableValued == tableValued)
                        {
                            funcs.Add(reader.GetString(0));
                        }
                    }
                }
            }
            return funcs;
        }

        public static async Task<string> GetObjectDefinitionAsync(string connectionString, string databaseName, string objectName)
        {
            var dbConnString = BuildConnectionString(connectionString, databaseName);
            using (var connection = new SqlConnection(dbConnString))
            {
                await connection.OpenAsync();
                var query = "SELECT definition FROM sys.sql_modules WHERE object_id = OBJECT_ID(@ObjectName);";
                using (var command = new SqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@ObjectName", objectName);
                    var result = await command.ExecuteScalarAsync();
                    return result?.ToString() ?? string.Empty;
                }
            }
        }

        public static async Task<string> GenerateTableCreateScriptAsync(string connectionString, string databaseName, string tableName)
        {
            var columns = new List<(string Name, string Type, bool IsNullable, bool IsIdentity, bool IsPrimaryKey)>();
            var dbConnString = BuildConnectionString(connectionString, databaseName);
            using (var connection = new SqlConnection(dbConnString))
            {
                await connection.OpenAsync();
                var query = @"
                    SELECT c.name, t.name + 
                        CASE 
                            WHEN t.name IN ('varchar', 'char', 'nvarchar', 'nchar') THEN '(' + 
                                CASE WHEN c.max_length = -1 THEN 'MAX' 
                                     ELSE CAST(CASE WHEN t.name LIKE 'n%' THEN c.max_length/2 ELSE c.max_length END AS VARCHAR) 
                                END + ')'
                            WHEN t.name IN ('decimal', 'numeric') THEN '(' + CAST(c.precision AS VARCHAR) + ',' + CAST(c.scale AS VARCHAR) + ')'
                            ELSE ''
                        END AS DataType,
                        c.is_nullable,
                        c.is_identity,
                        CAST(ISNULL((SELECT 1 FROM sys.index_columns ic 
                                JOIN sys.indexes i ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                                WHERE ic.object_id = c.object_id AND ic.column_id = c.column_id AND i.is_primary_key = 1), 0) AS BIT) AS IsPrimaryKey
                    FROM sys.columns c
                    JOIN sys.types t ON c.user_type_id = t.user_type_id
                    WHERE c.object_id = OBJECT_ID(@TableName)
                    ORDER BY c.column_id;";
                using (var command = new SqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@TableName", tableName);
                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            columns.Add((
                                reader.GetString(0),
                                reader.GetString(1),
                                reader.GetBoolean(2),
                                reader.GetBoolean(3),
                                reader.GetBoolean(4)
                            ));
                        }
                    }
                }
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"-- Create Table Script for {tableName}");
            sb.AppendLine($"CREATE TABLE {tableName}");
            sb.AppendLine("(");
            for (int i = 0; i < columns.Count; i++)
            {
                var col = columns[i];
                string identityStr = col.IsIdentity ? " IDENTITY(1,1)" : "";
                string nullStr = col.IsNullable ? "NULL" : "NOT NULL";
                string pkStr = col.IsPrimaryKey ? " PRIMARY KEY" : "";
                
                string comma = (i < columns.Count - 1) ? "," : "";
                sb.AppendLine($"    [{col.Name}] {col.Type}{identityStr}{pkStr} {nullStr}{comma}");
            }
            sb.AppendLine(");");
            return sb.ToString();
        }
    }
}
