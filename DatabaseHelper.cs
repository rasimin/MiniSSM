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
                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            do
                            {
                                if (reader.FieldCount > 0)
                                {
                                    var dataTable = new DataTable();
                                    
                                    // Manually build columns to avoid closing/advancing the reader
                                    for (int i = 0; i < reader.FieldCount; i++)
                                    {
                                        dataTable.Columns.Add(reader.GetName(i), reader.GetFieldType(i));
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
    }
}
