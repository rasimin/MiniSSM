using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace SSMS
{
    public class QueryResult
    {
        public List<DataTable> DataTables { get; set; } = new List<DataTable>();
        public List<string> ExecutionPlans { get; set; } = new List<string>();
        public string Message { get; set; } = string.Empty;
        public bool IsSuccess { get; set; }
        public bool IsCancelled { get; set; }
        public string EffectiveDatabaseName { get; set; } = string.Empty;
        public int? RowsAffected { get; set; }
        public TimeSpan ExecutionTime { get; set; }
    }

    public enum QueryExecutionMode
    {
        Execute,
        Parse,
        EstimatedPlan,
        ActualPlan
    }

    public sealed class DatabaseObjectSearchResult
    {
        public string DatabaseName { get; init; } = string.Empty;
        public string SchemaName { get; init; } = string.Empty;
        public string ObjectName { get; init; } = string.Empty;
        public string ObjectType { get; init; } = string.Empty;
        public int ObjectId { get; init; }
        public DateTime CreateDate { get; init; }
        public DateTime ModifyDate { get; init; }
        public string MatchLocation { get; init; } = string.Empty;
        public string MatchDetail { get; init; } = string.Empty;
        public string FullName => $"{SchemaName}.{ObjectName}";
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
        public static async Task<List<string>> GetDatabasesAsync(
            string connectionString,
            CancellationToken cancellationToken = default)
        {
            var databases = new List<string>();
            using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync(cancellationToken);
                var query = "SELECT name FROM sys.databases ORDER BY name;";
                using (var command = new SqlCommand(query, connection))
                using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        databases.Add(reader.GetString(0));
                    }
                }
            }
            return databases;
        }

        public static async Task<List<DatabaseObjectSearchResult>> SearchObjectsAcrossDatabasesAsync(
            string connectionString,
            string searchText,
            string? databaseFilter = null,
            CancellationToken cancellationToken = default)
        {
            var results = new List<DatabaseObjectSearchResult>();
            string escapedSearch = searchText
                .Replace("\\", "\\\\")
                .Replace("%", "\\%")
                .Replace("_", "\\_");
            string pattern = $"%{escapedSearch}%";

            IReadOnlyList<string> databases = string.IsNullOrWhiteSpace(databaseFilter)
                ? await GetDatabasesAsync(connectionString, cancellationToken)
                : new[] { databaseFilter };

            foreach (string databaseName in databases)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var connection = new SqlConnection(BuildConnectionString(connectionString, databaseName));
                    await connection.OpenAsync(cancellationToken);
                    const string query = @"
WITH SearchableObjects AS
(
    SELECT o.object_id,
           s.name AS SchemaName,
           o.name AS ObjectName,
           o.create_date AS CreateDate,
           o.modify_date AS ModifyDate,
           m.definition AS DefinitionText,
           CASE o.type
           WHEN 'U' THEN 'Table'
           WHEN 'V' THEN 'View'
           WHEN 'P' THEN 'StoredProcedure'
           WHEN 'PC' THEN 'StoredProcedure'
           WHEN 'FN' THEN 'Function'
           WHEN 'IF' THEN 'Function'
           WHEN 'TF' THEN 'Function'
           WHEN 'FS' THEN 'Function'
           WHEN 'FT' THEN 'Function'
           WHEN 'TR' THEN 'Trigger'
           ELSE o.type_desc
           END AS ObjectType
    FROM sys.objects o
    JOIN sys.schemas s ON s.schema_id = o.schema_id
    LEFT JOIN sys.sql_modules m ON m.object_id = o.object_id
    WHERE o.is_ms_shipped = 0
      AND o.type IN ('U', 'V', 'P', 'PC', 'FN', 'IF', 'TF', 'FS', 'FT', 'TR')
)
SELECT TOP (250)
       DB_NAME() AS DatabaseName,
       o.SchemaName,
       o.ObjectName,
       o.ObjectType,
       o.object_id,
       o.CreateDate,
       o.ModifyDate,
       STUFF(
           CASE WHEN o.ObjectName LIKE @Pattern ESCAPE '\' THEN ', Object name' ELSE '' END +
           CASE WHEN o.SchemaName LIKE @Pattern ESCAPE '\' THEN ', Schema' ELSE '' END +
           CASE WHEN matchedColumn.ColumnName IS NOT NULL THEN ', Column' ELSE '' END +
           CASE WHEN o.DefinitionText LIKE @Pattern ESCAPE '\' THEN ', Definition' ELSE '' END,
           1, 2, '') AS MatchLocation,
       STUFF(
           CASE WHEN o.ObjectName LIKE @Pattern ESCAPE '\' THEN ' | Object: ' + o.ObjectName ELSE '' END +
           CASE WHEN o.SchemaName LIKE @Pattern ESCAPE '\' THEN ' | Schema: ' + o.SchemaName ELSE '' END +
           CASE WHEN matchedColumn.ColumnName IS NOT NULL THEN ' | Column: ' + matchedColumn.ColumnName ELSE '' END +
           CASE WHEN definitionMatch.MatchPosition > 0 THEN ' | Definition: ' +
               LTRIM(RTRIM(SUBSTRING(
                   normalized.DefinitionText,
                   CASE WHEN definitionMatch.MatchPosition > 60 THEN definitionMatch.MatchPosition - 60 ELSE 1 END,
                   220))) ELSE '' END,
           1, 3, '') AS MatchDetail
FROM SearchableObjects o
OUTER APPLY
(
    SELECT TOP (1) c.name AS ColumnName
    FROM sys.columns c
    WHERE c.object_id = o.object_id
      AND c.name LIKE @Pattern ESCAPE '\'
    ORDER BY c.column_id
) matchedColumn
CROSS APPLY
(
    SELECT REPLACE(REPLACE(o.DefinitionText, CHAR(13), ' '), CHAR(10), ' ') AS DefinitionText
) normalized
CROSS APPLY
(
    SELECT CHARINDEX(@SearchText, normalized.DefinitionText) AS MatchPosition
) definitionMatch
WHERE o.ObjectName LIKE @Pattern ESCAPE '\'
   OR o.SchemaName LIKE @Pattern ESCAPE '\'
   OR matchedColumn.ColumnName IS NOT NULL
   OR o.DefinitionText LIKE @Pattern ESCAPE '\'
ORDER BY o.SchemaName, o.ObjectName;";
                    using var command = new SqlCommand(query, connection);
                    command.Parameters.AddWithValue("@Pattern", pattern);
                    command.Parameters.Add("@SearchText", SqlDbType.NVarChar, 4000).Value = searchText;
                    using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        results.Add(new DatabaseObjectSearchResult
                        {
                            DatabaseName = reader.GetString(0),
                            SchemaName = reader.GetString(1),
                            ObjectName = reader.GetString(2),
                            ObjectType = reader.GetString(3),
                            ObjectId = reader.GetInt32(4),
                            CreateDate = reader.GetDateTime(5),
                            ModifyDate = reader.GetDateTime(6),
                            MatchLocation = reader.GetString(7),
                            MatchDetail = reader.GetString(8)
                        });
                        if (results.Count >= 1000)
                        {
                            return results;
                        }
                    }
                }
                catch (Exception ex) when (ex is SqlException or InvalidOperationException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AppLogger.Error(ex, $"Object search skipped database '{databaseName}'");
                }
            }

            return results;
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
                    SELECT trigger_schema.name + '.' + tr.name,
                           tr.is_disabled
                    FROM sys.triggers tr
                    INNER JOIN sys.objects trigger_object
                        ON trigger_object.object_id = tr.object_id
                    INNER JOIN sys.schemas trigger_schema
                        ON trigger_schema.schema_id = trigger_object.schema_id
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
        public static async Task<QueryResult> ExecuteQueryAsync(
            string connectionString,
            string databaseName,
            string sqlQuery,
            IProgress<string>? messageProgress = null,
            CancellationToken cancellationToken = default,
            QueryExecutionMode mode = QueryExecutionMode.Execute)
        {
            var result = new QueryResult();
            var dbConnString = BuildConnectionString(connectionString, databaseName);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                using (var connection = new SqlConnection(dbConnString))
                {
                    await connection.OpenAsync(cancellationToken);

                    var messages = new System.Text.StringBuilder();
                    connection.InfoMessage += (sender, e) =>
                    {
                        messages.AppendLine(e.Message);
                        messageProgress?.Report(e.Message);
                    };

                    List<SqlBatch> batches = SqlBatchSplitter.Split(sqlQuery);
                    if (batches.Count == 0)
                    {
                        throw new InvalidOperationException("No executable SQL batch was found.");
                    }

                    string? sessionOption = mode switch
                    {
                        QueryExecutionMode.Parse => "SET PARSEONLY ON",
                        QueryExecutionMode.EstimatedPlan => "SET SHOWPLAN_XML ON",
                        QueryExecutionMode.ActualPlan => "SET STATISTICS XML ON",
                        _ => null
                    };
                    string? resetOption = mode switch
                    {
                        QueryExecutionMode.Parse => "SET PARSEONLY OFF",
                        QueryExecutionMode.EstimatedPlan => "SET SHOWPLAN_XML OFF",
                        QueryExecutionMode.ActualPlan => "SET STATISTICS XML OFF",
                        _ => null
                    };

                    try
                    {
                        if (sessionOption != null)
                        {
                            await ExecuteSessionCommandAsync(connection, sessionOption, cancellationToken);
                        }

                        int batchNumber = 0;
                        foreach (SqlBatch batch in batches)
                        {
                            batchNumber++;
                            for (int repeat = 0; repeat < batch.RepeatCount; repeat++)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                await ExecuteBatchAsync(
                                    connection,
                                    batch.Text,
                                    result,
                                    messages,
                                    messageProgress,
                                    cancellationToken,
                                    includeDataResults: mode is QueryExecutionMode.Execute or QueryExecutionMode.ActualPlan);

                                if (batch.RepeatCount > 1)
                                {
                                    string repeatMessage = $"Batch {batchNumber} completed ({repeat + 1}/{batch.RepeatCount}).";
                                    messages.AppendLine(repeatMessage);
                                    messageProgress?.Report(repeatMessage);
                                }
                            }
                        }
                    }
                    finally
                    {
                        if (resetOption != null && connection.State == ConnectionState.Open)
                        {
                            try
                            {
                                await ExecuteSessionCommandAsync(connection, resetOption, CancellationToken.None);
                            }
                            catch (Exception resetException)
                            {
                                AppLogger.Error(resetException, $"Failed to reset SQL session option: {resetOption}");
                            }
                        }
                    }

                    stopwatch.Stop();
                    result.IsSuccess = true;
                    result.EffectiveDatabaseName = connection.Database;
                    result.ExecutionTime = stopwatch.Elapsed;
                    result.Message = messages.Length > 0
                        ? messages.ToString()
                        : mode switch
                        {
                            QueryExecutionMode.Parse => "Syntax check completed successfully.",
                            QueryExecutionMode.EstimatedPlan => "Estimated execution plan generated successfully.",
                            QueryExecutionMode.ActualPlan => "Query completed and actual execution plan generated successfully.",
                            _ => "Command completed successfully."
                        };
                }
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                result.IsCancelled = true;
                result.IsSuccess = false;
                result.ExecutionTime = stopwatch.Elapsed;
                result.Message = "Query cancelled by user.";
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

        private static async Task ExecuteSessionCommandAsync(
            SqlConnection connection,
            string commandText,
            CancellationToken cancellationToken)
        {
            using var command = new SqlCommand(commandText, connection)
            {
                CommandTimeout = AppSettings.Current.Query.CommandTimeoutSeconds
            };
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static async Task ExecuteBatchAsync(
            SqlConnection connection,
            string sql,
            QueryResult result,
            System.Text.StringBuilder messages,
            IProgress<string>? messageProgress,
            CancellationToken cancellationToken,
            bool includeDataResults)
        {
            using var command = new SqlCommand(sql, connection)
            {
                CommandTimeout = AppSettings.Current.Query.CommandTimeoutSeconds
            };
            using var cancellationRegistration = cancellationToken.Register(() =>
            {
                try
                {
                    command.Cancel();
                }
                catch
                {
                    // Command may already be completed or disposed.
                }
            });

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            do
            {
                if (reader.FieldCount > 0)
                {
                    if (IsExecutionPlanResult(reader))
                    {
                        while (await reader.ReadAsync(cancellationToken))
                        {
                            if (!reader.IsDBNull(0))
                            {
                                result.ExecutionPlans.Add(reader.GetString(0));
                            }
                        }
                    }
                    else if (includeDataResults)
                    {
                        var dataTable = new DataTable();
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

                        while (await reader.ReadAsync(cancellationToken))
                        {
                            DataRow row = dataTable.NewRow();
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
                        while (await reader.ReadAsync(cancellationToken))
                        {
                            // Consume parse/showplan rows that are not surfaced as grid data.
                        }
                    }
                }
                else if (reader.RecordsAffected >= 0)
                {
                    int rowsAffected = reader.RecordsAffected;
                    result.RowsAffected = (result.RowsAffected ?? 0) + rowsAffected;
                    string affectedMessage = $"({rowsAffected} row(s) affected)";
                    messages.AppendLine(affectedMessage);
                    messageProgress?.Report(affectedMessage);
                }
            } while (await reader.NextResultAsync(cancellationToken));
        }

        private static bool IsExecutionPlanResult(SqlDataReader reader)
        {
            return reader.FieldCount == 1 &&
                   reader.GetName(0).Contains("Showplan", StringComparison.OrdinalIgnoreCase);
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
            var columns = new List<TableColumnScriptInfo>();
            var keyConstraints = new Dictionary<int, KeyConstraintScriptInfo>();
            var checks = new List<CheckConstraintScriptInfo>();
            var foreignKeys = new Dictionary<int, ForeignKeyScriptInfo>();
            var indexes = new Dictionary<int, IndexScriptInfo>();
            var triggers = new List<TriggerScriptInfo>();
            string schemaName;
            string resolvedTableName;
            var dbConnString = BuildConnectionString(connectionString, databaseName);

            using (var connection = new SqlConnection(dbConnString))
            {
                await connection.OpenAsync();
                const string query = @"
DECLARE @ObjectId INT = OBJECT_ID(@TableName, N'U');
IF @ObjectId IS NULL
    THROW 50000, 'Table was not found in the selected database.', 1;

SELECT s.name AS SchemaName, tb.name AS TableName
FROM sys.tables tb
JOIN sys.schemas s ON s.schema_id = tb.schema_id
WHERE tb.object_id = @ObjectId;

SELECT c.column_id, c.name, ts.name AS TypeSchema, ty.name AS TypeName,
       ty.is_user_defined, c.max_length, c.precision, c.scale, c.is_nullable,
       c.collation_name,
       CONVERT(NVARCHAR(100), ic.seed_value) AS IdentitySeed,
       CONVERT(NVARCHAR(100), ic.increment_value) AS IdentityIncrement,
       cc.definition AS ComputedDefinition, ISNULL(cc.is_persisted, 0) AS IsPersisted,
       dc.name AS DefaultName, dc.definition AS DefaultDefinition,
       c.is_rowguidcol, c.is_sparse, c.is_column_set
FROM sys.columns c
JOIN sys.types ty ON ty.user_type_id = c.user_type_id
JOIN sys.schemas ts ON ts.schema_id = ty.schema_id
LEFT JOIN sys.identity_columns ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
WHERE c.object_id = @ObjectId
ORDER BY c.column_id;

SELECT kc.object_id AS ConstraintId, kc.name, kc.type, i.type_desc, ds.name AS DataSpaceName,
       ds.type_desc AS DataSpaceType, ic.key_ordinal, c.name AS ColumnName,
       ic.is_descending_key, ic.partition_ordinal
FROM sys.key_constraints kc
JOIN sys.indexes i ON i.object_id = kc.parent_object_id AND i.index_id = kc.unique_index_id
JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0
JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
LEFT JOIN sys.data_spaces ds ON ds.data_space_id = i.data_space_id
WHERE kc.parent_object_id = @ObjectId
ORDER BY kc.object_id, ic.key_ordinal;

SELECT object_id, name, definition, is_not_for_replication, is_disabled, is_not_trusted
FROM sys.check_constraints
WHERE parent_object_id = @ObjectId
ORDER BY name;

SELECT fk.object_id, fk.name, rs.name AS ReferencedSchema, rt.name AS ReferencedTable,
       fk.delete_referential_action_desc, fk.update_referential_action_desc,
       fk.is_not_for_replication, fk.is_disabled, fk.is_not_trusted,
       fkc.constraint_column_id, pc.name AS ParentColumn, rc.name AS ReferencedColumn
FROM sys.foreign_keys fk
JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id
JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
JOIN sys.columns pc ON pc.object_id = fk.parent_object_id AND pc.column_id = fkc.parent_column_id
JOIN sys.columns rc ON rc.object_id = fk.referenced_object_id AND rc.column_id = fkc.referenced_column_id
WHERE fk.parent_object_id = @ObjectId
ORDER BY fk.object_id, fkc.constraint_column_id;

SELECT i.index_id, i.name, i.type, i.type_desc, i.is_unique, i.has_filter, i.filter_definition,
       ds.name AS DataSpaceName, ds.type_desc AS DataSpaceType, ic.index_column_id,
       ic.key_ordinal, ic.is_included_column, ic.is_descending_key,
       ic.partition_ordinal, c.name AS ColumnName, i.is_disabled
FROM sys.indexes i
LEFT JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
LEFT JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
LEFT JOIN sys.data_spaces ds ON ds.data_space_id = i.data_space_id
WHERE i.object_id = @ObjectId
  AND i.index_id > 0
  AND i.name IS NOT NULL
  AND i.is_primary_key = 0
  AND i.is_unique_constraint = 0
  AND i.is_hypothetical = 0
ORDER BY i.index_id, ic.is_included_column, ic.key_ordinal, ic.index_column_id;

SELECT tr.name, tr.is_disabled, sm.definition, sm.uses_ansi_nulls, sm.uses_quoted_identifier
FROM sys.triggers tr
LEFT JOIN sys.sql_modules sm ON sm.object_id = tr.object_id
WHERE tr.parent_id = @ObjectId
ORDER BY tr.name;";

                using (var command = new SqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@TableName", tableName);
                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        if (!await reader.ReadAsync())
                        {
                            throw new InvalidOperationException($"Table '{tableName}' was not found.");
                        }

                        schemaName = reader.GetString(0);
                        resolvedTableName = reader.GetString(1);

                        await reader.NextResultAsync();
                        while (await reader.ReadAsync())
                        {
                            columns.Add(new TableColumnScriptInfo
                            {
                                Name = reader.GetString(1),
                                TypeSchema = reader.GetString(2),
                                TypeName = reader.GetString(3),
                                IsUserDefined = reader.GetBoolean(4),
                                MaxLength = reader.GetInt16(5),
                                Precision = reader.GetByte(6),
                                Scale = reader.GetByte(7),
                                IsNullable = reader.GetBoolean(8),
                                CollationName = reader.IsDBNull(9) ? null : reader.GetString(9),
                                IdentitySeed = reader.IsDBNull(10) ? null : reader.GetString(10),
                                IdentityIncrement = reader.IsDBNull(11) ? null : reader.GetString(11),
                                ComputedDefinition = reader.IsDBNull(12) ? null : reader.GetString(12),
                                IsPersisted = reader.GetBoolean(13),
                                DefaultName = reader.IsDBNull(14) ? null : reader.GetString(14),
                                DefaultDefinition = reader.IsDBNull(15) ? null : reader.GetString(15),
                                IsRowGuid = reader.GetBoolean(16),
                                IsSparse = reader.GetBoolean(17),
                                IsColumnSet = reader.GetBoolean(18)
                            });
                        }

                        await reader.NextResultAsync();
                        while (await reader.ReadAsync())
                        {
                            int id = reader.GetInt32(0);
                            if (!keyConstraints.TryGetValue(id, out var constraint))
                            {
                                constraint = new KeyConstraintScriptInfo
                                {
                                    Name = reader.GetString(1),
                                    IsPrimaryKey = reader.GetString(2) == "PK",
                                    IndexType = reader.GetString(3),
                                    DataSpaceName = reader.IsDBNull(4) ? null : reader.GetString(4),
                                    DataSpaceType = reader.IsDBNull(5) ? null : reader.GetString(5)
                                };
                                keyConstraints.Add(id, constraint);
                            }

                            constraint.Columns.Add(new IndexColumnScriptInfo
                            {
                                Name = reader.GetString(7),
                                IsDescending = reader.GetBoolean(8)
                            });
                            if (reader.GetByte(9) > 0)
                            {
                                constraint.PartitionColumnName = reader.GetString(7);
                            }
                        }

                        await reader.NextResultAsync();
                        while (await reader.ReadAsync())
                        {
                            checks.Add(new CheckConstraintScriptInfo
                            {
                                Name = reader.GetString(1),
                                Definition = reader.GetString(2),
                                IsNotForReplication = reader.GetBoolean(3),
                                IsDisabled = reader.GetBoolean(4),
                                IsNotTrusted = reader.GetBoolean(5)
                            });
                        }

                        await reader.NextResultAsync();
                        while (await reader.ReadAsync())
                        {
                            int id = reader.GetInt32(0);
                            if (!foreignKeys.TryGetValue(id, out var foreignKey))
                            {
                                foreignKey = new ForeignKeyScriptInfo
                                {
                                    Name = reader.GetString(1),
                                    ReferencedSchema = reader.GetString(2),
                                    ReferencedTable = reader.GetString(3),
                                    DeleteAction = reader.GetString(4),
                                    UpdateAction = reader.GetString(5),
                                    IsNotForReplication = reader.GetBoolean(6),
                                    IsDisabled = reader.GetBoolean(7),
                                    IsNotTrusted = reader.GetBoolean(8)
                                };
                                foreignKeys.Add(id, foreignKey);
                            }

                            foreignKey.ParentColumns.Add(reader.GetString(10));
                            foreignKey.ReferencedColumns.Add(reader.GetString(11));
                        }

                        await reader.NextResultAsync();
                        while (await reader.ReadAsync())
                        {
                            int id = reader.GetInt32(0);
                            if (!indexes.TryGetValue(id, out var index))
                            {
                                index = new IndexScriptInfo
                                {
                                    Name = reader.GetString(1),
                                    Type = reader.GetByte(2),
                                    TypeDescription = reader.GetString(3),
                                    IsUnique = reader.GetBoolean(4),
                                    FilterDefinition = reader.IsDBNull(6) ? null : reader.GetString(6),
                                    DataSpaceName = reader.IsDBNull(7) ? null : reader.GetString(7),
                                    DataSpaceType = reader.IsDBNull(8) ? null : reader.GetString(8),
                                    IsDisabled = reader.GetBoolean(15)
                                };
                                indexes.Add(id, index);
                            }

                            if (!reader.IsDBNull(14))
                            {
                                var indexColumn = new IndexColumnScriptInfo
                                {
                                    Name = reader.GetString(14),
                                    IsDescending = reader.GetBoolean(12)
                                };
                                if (reader.GetBoolean(11))
                                {
                                    index.IncludedColumns.Add(indexColumn);
                                }
                                else
                                {
                                    index.KeyColumns.Add(indexColumn);
                                }
                                if (reader.GetByte(13) > 0)
                                {
                                    index.PartitionColumnName = reader.GetString(14);
                                }
                            }
                        }

                        await reader.NextResultAsync();
                        while (await reader.ReadAsync())
                        {
                            triggers.Add(new TriggerScriptInfo
                            {
                                Name = reader.GetString(0),
                                IsDisabled = reader.GetBoolean(1),
                                Definition = reader.IsDBNull(2) ? null : reader.GetString(2),
                                UsesAnsiNulls = reader.IsDBNull(3) || reader.GetBoolean(3),
                                UsesQuotedIdentifier = reader.IsDBNull(4) || reader.GetBoolean(4)
                            });
                        }
                    }
                }
            }

            var sb = new System.Text.StringBuilder();
            string qualifiedTableName = $"{QuoteIdentifier(schemaName)}.{QuoteIdentifier(resolvedTableName)}";
            sb.AppendLine($"-- Complete CREATE script for {qualifiedTableName}");
            sb.AppendLine("SET ANSI_NULLS ON;");
            sb.AppendLine("SET QUOTED_IDENTIFIER ON;");
            sb.AppendLine("GO");
            sb.AppendLine();
            sb.AppendLine($"CREATE TABLE {qualifiedTableName}");
            sb.AppendLine("(");
            for (int i = 0; i < columns.Count; i++)
            {
                sb.Append("    ");
                sb.Append(BuildColumnDefinition(columns[i]));
                sb.AppendLine(i < columns.Count - 1 ? "," : string.Empty);
            }
            sb.AppendLine(");");
            sb.AppendLine("GO");

            foreach (var constraint in keyConstraints.Values)
            {
                sb.AppendLine();
                string constraintType = constraint.IsPrimaryKey ? "PRIMARY KEY" : "UNIQUE";
                sb.AppendLine($"ALTER TABLE {qualifiedTableName} ADD CONSTRAINT {QuoteIdentifier(constraint.Name)} {constraintType} {NormalizeIndexType(constraint.IndexType)}");
                AppendIndexColumnList(sb, constraint.Columns, includeSortDirection: true);
                AppendDataSpaceClause(sb, constraint.DataSpaceName, constraint.DataSpaceType, constraint.PartitionColumnName);
                sb.AppendLine(";");
                sb.AppendLine("GO");
            }

            foreach (var check in checks)
            {
                sb.AppendLine();
                string trustMode = check.IsNotTrusted ? "NOCHECK" : "CHECK";
                string notForReplication = check.IsNotForReplication ? " NOT FOR REPLICATION" : string.Empty;
                sb.AppendLine($"ALTER TABLE {qualifiedTableName} WITH {trustMode} ADD CONSTRAINT {QuoteIdentifier(check.Name)} CHECK{notForReplication} {check.Definition};");
                sb.AppendLine($"ALTER TABLE {qualifiedTableName} {(check.IsDisabled ? "NOCHECK" : "CHECK")} CONSTRAINT {QuoteIdentifier(check.Name)};");
                sb.AppendLine("GO");
            }

            foreach (var index in indexes.Values)
            {
                sb.AppendLine();
                AppendIndexScript(sb, qualifiedTableName, index);
                sb.AppendLine("GO");
            }

            foreach (var foreignKey in foreignKeys.Values)
            {
                sb.AppendLine();
                string trustMode = foreignKey.IsNotTrusted ? "NOCHECK" : "CHECK";
                sb.AppendLine($"ALTER TABLE {qualifiedTableName} WITH {trustMode} ADD CONSTRAINT {QuoteIdentifier(foreignKey.Name)} FOREIGN KEY");
                AppendSimpleColumnList(sb, foreignKey.ParentColumns);
                sb.AppendLine($"REFERENCES {QuoteIdentifier(foreignKey.ReferencedSchema)}.{QuoteIdentifier(foreignKey.ReferencedTable)}");
                AppendSimpleColumnList(sb, foreignKey.ReferencedColumns);
                if (foreignKey.DeleteAction != "NO_ACTION")
                {
                    sb.AppendLine($"ON DELETE {foreignKey.DeleteAction.Replace('_', ' ')}");
                }
                if (foreignKey.UpdateAction != "NO_ACTION")
                {
                    sb.AppendLine($"ON UPDATE {foreignKey.UpdateAction.Replace('_', ' ')}");
                }
                if (foreignKey.IsNotForReplication)
                {
                    sb.AppendLine("NOT FOR REPLICATION");
                }
                sb.AppendLine(";");
                sb.AppendLine($"ALTER TABLE {qualifiedTableName} {(foreignKey.IsDisabled ? "NOCHECK" : "CHECK")} CONSTRAINT {QuoteIdentifier(foreignKey.Name)};");
                sb.AppendLine("GO");
            }

            foreach (var trigger in triggers)
            {
                sb.AppendLine();
                if (string.IsNullOrWhiteSpace(trigger.Definition))
                {
                    sb.AppendLine($"-- Trigger {QuoteIdentifier(trigger.Name)} is encrypted and could not be scripted.");
                    continue;
                }

                sb.AppendLine($"SET ANSI_NULLS {(trigger.UsesAnsiNulls ? "ON" : "OFF")};");
                sb.AppendLine("GO");
                sb.AppendLine($"SET QUOTED_IDENTIFIER {(trigger.UsesQuotedIdentifier ? "ON" : "OFF")};");
                sb.AppendLine("GO");
                sb.AppendLine(trigger.Definition.Trim());
                sb.AppendLine("GO");
                if (trigger.IsDisabled)
                {
                    sb.AppendLine($"DISABLE TRIGGER {QuoteIdentifier(trigger.Name)} ON {qualifiedTableName};");
                    sb.AppendLine("GO");
                }
            }

            return sb.ToString();
        }

        private static string BuildColumnDefinition(TableColumnScriptInfo column)
        {
            string quotedName = QuoteIdentifier(column.Name);
            if (!string.IsNullOrWhiteSpace(column.ComputedDefinition))
            {
                return $"{quotedName} AS {column.ComputedDefinition}{(column.IsPersisted ? " PERSISTED" : string.Empty)}";
            }

            var parts = new List<string> { quotedName, BuildTypeDeclaration(column) };
            if (!string.IsNullOrWhiteSpace(column.CollationName) && TypeSupportsCollation(column.TypeName))
            {
                parts.Add($"COLLATE {column.CollationName}");
            }
            if (column.IdentitySeed != null && column.IdentityIncrement != null)
            {
                parts.Add($"IDENTITY({column.IdentitySeed},{column.IdentityIncrement})");
            }
            if (column.IsRowGuid)
            {
                parts.Add("ROWGUIDCOL");
            }
            if (column.IsSparse)
            {
                parts.Add("SPARSE");
            }
            if (column.IsColumnSet)
            {
                parts.Add("COLUMN_SET FOR ALL_SPARSE_COLUMNS");
            }

            parts.Add(column.IsNullable ? "NULL" : "NOT NULL");
            if (!string.IsNullOrWhiteSpace(column.DefaultDefinition))
            {
                if (!string.IsNullOrWhiteSpace(column.DefaultName))
                {
                    parts.Add($"CONSTRAINT {QuoteIdentifier(column.DefaultName)}");
                }
                parts.Add($"DEFAULT {column.DefaultDefinition}");
            }
            return string.Join(" ", parts);
        }

        private static string BuildTypeDeclaration(TableColumnScriptInfo column)
        {
            if (column.IsUserDefined)
            {
                return $"{QuoteIdentifier(column.TypeSchema)}.{QuoteIdentifier(column.TypeName)}";
            }

            string typeName = column.TypeName.ToLowerInvariant();
            return typeName switch
            {
                "varchar" or "char" or "varbinary" or "binary" =>
                    $"{typeName}({(column.MaxLength == -1 ? "MAX" : column.MaxLength.ToString())})",
                "nvarchar" or "nchar" =>
                    $"{typeName}({(column.MaxLength == -1 ? "MAX" : (column.MaxLength / 2).ToString())})",
                "decimal" or "numeric" => $"{typeName}({column.Precision},{column.Scale})",
                "datetime2" or "datetimeoffset" or "time" => $"{typeName}({column.Scale})",
                "float" => $"float({column.Precision})",
                _ => typeName
            };
        }

        private static bool TypeSupportsCollation(string typeName)
        {
            return typeName.ToLowerInvariant() is "char" or "varchar" or "text" or "nchar" or "nvarchar" or "ntext";
        }

        private static void AppendIndexScript(System.Text.StringBuilder sb, string tableName, IndexScriptInfo index)
        {
            string unique = index.IsUnique ? "UNIQUE " : string.Empty;
            if (index.Type is 1 or 2)
            {
                sb.AppendLine($"CREATE {unique}{NormalizeIndexType(index.TypeDescription)} INDEX {QuoteIdentifier(index.Name)} ON {tableName}");
                AppendIndexColumnList(sb, index.KeyColumns, includeSortDirection: true);
                if (index.IncludedColumns.Count > 0)
                {
                    sb.AppendLine("INCLUDE");
                    AppendIndexColumnList(sb, index.IncludedColumns, includeSortDirection: false);
                }
                if (!string.IsNullOrWhiteSpace(index.FilterDefinition))
                {
                    sb.AppendLine($"WHERE {index.FilterDefinition}");
                }
                AppendDataSpaceClause(sb, index.DataSpaceName, index.DataSpaceType, index.PartitionColumnName);
                sb.AppendLine(";");
                AppendDisabledIndexStatement(sb, tableName, index);
                return;
            }

            if (index.Type is 5 or 6)
            {
                string kind = index.Type == 5 ? "CLUSTERED COLUMNSTORE" : "NONCLUSTERED COLUMNSTORE";
                sb.Append($"CREATE {kind} INDEX {QuoteIdentifier(index.Name)} ON {tableName}");
                if (index.Type == 6 && index.KeyColumns.Count > 0)
                {
                    sb.AppendLine();
                    AppendIndexColumnList(sb, index.KeyColumns, includeSortDirection: false);
                }
                else
                {
                    sb.AppendLine();
                }
                AppendDataSpaceClause(sb, index.DataSpaceName, index.DataSpaceType, index.PartitionColumnName);
                sb.AppendLine(";");
                AppendDisabledIndexStatement(sb, tableName, index);
                return;
            }

            sb.AppendLine($"-- Index {QuoteIdentifier(index.Name)} uses {index.TypeDescription} and requires specialized scripting.");
        }

        private static void AppendDisabledIndexStatement(
            System.Text.StringBuilder sb,
            string tableName,
            IndexScriptInfo index)
        {
            if (index.IsDisabled)
            {
                sb.AppendLine($"ALTER INDEX {QuoteIdentifier(index.Name)} ON {tableName} DISABLE;");
            }
        }

        private static void AppendIndexColumnList(System.Text.StringBuilder sb, List<IndexColumnScriptInfo> columns, bool includeSortDirection)
        {
            sb.AppendLine("(");
            for (int i = 0; i < columns.Count; i++)
            {
                string direction = includeSortDirection ? (columns[i].IsDescending ? " DESC" : " ASC") : string.Empty;
                sb.AppendLine($"    {QuoteIdentifier(columns[i].Name)}{direction}{(i < columns.Count - 1 ? "," : string.Empty)}");
            }
            sb.AppendLine(")");
        }

        private static void AppendSimpleColumnList(System.Text.StringBuilder sb, List<string> columns)
        {
            sb.AppendLine("(");
            for (int i = 0; i < columns.Count; i++)
            {
                sb.AppendLine($"    {QuoteIdentifier(columns[i])}{(i < columns.Count - 1 ? "," : string.Empty)}");
            }
            sb.AppendLine(")");
        }

        private static void AppendDataSpaceClause(
            System.Text.StringBuilder sb,
            string? dataSpaceName,
            string? dataSpaceType,
            string? partitionColumnName)
        {
            if (string.IsNullOrWhiteSpace(dataSpaceName))
            {
                return;
            }

            if (dataSpaceType == "PARTITION_SCHEME" && !string.IsNullOrWhiteSpace(partitionColumnName))
            {
                sb.AppendLine($"ON {QuoteIdentifier(dataSpaceName)}({QuoteIdentifier(partitionColumnName)})");
                return;
            }

            if (dataSpaceType != "PARTITION_SCHEME")
            {
                sb.AppendLine($"ON {QuoteIdentifier(dataSpaceName)}");
            }
        }

        private static string NormalizeIndexType(string typeDescription)
        {
            return typeDescription.Replace('_', ' ');
        }

        private static string QuoteIdentifier(string identifier)
        {
            return $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
        }

        private sealed class TableColumnScriptInfo
        {
            public string Name { get; init; } = string.Empty;
            public string TypeSchema { get; init; } = string.Empty;
            public string TypeName { get; init; } = string.Empty;
            public bool IsUserDefined { get; init; }
            public short MaxLength { get; init; }
            public byte Precision { get; init; }
            public byte Scale { get; init; }
            public bool IsNullable { get; init; }
            public string? CollationName { get; init; }
            public string? IdentitySeed { get; init; }
            public string? IdentityIncrement { get; init; }
            public string? ComputedDefinition { get; init; }
            public bool IsPersisted { get; init; }
            public string? DefaultName { get; init; }
            public string? DefaultDefinition { get; init; }
            public bool IsRowGuid { get; init; }
            public bool IsSparse { get; init; }
            public bool IsColumnSet { get; init; }
        }

        private sealed class IndexColumnScriptInfo
        {
            public string Name { get; init; } = string.Empty;
            public bool IsDescending { get; init; }
        }

        private sealed class KeyConstraintScriptInfo
        {
            public string Name { get; init; } = string.Empty;
            public bool IsPrimaryKey { get; init; }
            public string IndexType { get; init; } = string.Empty;
            public string? DataSpaceName { get; init; }
            public string? DataSpaceType { get; init; }
            public string? PartitionColumnName { get; set; }
            public List<IndexColumnScriptInfo> Columns { get; } = new();
        }

        private sealed class CheckConstraintScriptInfo
        {
            public string Name { get; init; } = string.Empty;
            public string Definition { get; init; } = string.Empty;
            public bool IsNotForReplication { get; init; }
            public bool IsDisabled { get; init; }
            public bool IsNotTrusted { get; init; }
        }

        private sealed class ForeignKeyScriptInfo
        {
            public string Name { get; init; } = string.Empty;
            public string ReferencedSchema { get; init; } = string.Empty;
            public string ReferencedTable { get; init; } = string.Empty;
            public string DeleteAction { get; init; } = string.Empty;
            public string UpdateAction { get; init; } = string.Empty;
            public bool IsNotForReplication { get; init; }
            public bool IsDisabled { get; init; }
            public bool IsNotTrusted { get; init; }
            public List<string> ParentColumns { get; } = new();
            public List<string> ReferencedColumns { get; } = new();
        }

        private sealed class IndexScriptInfo
        {
            public string Name { get; init; } = string.Empty;
            public byte Type { get; init; }
            public string TypeDescription { get; init; } = string.Empty;
            public bool IsUnique { get; init; }
            public bool IsDisabled { get; init; }
            public string? FilterDefinition { get; init; }
            public string? DataSpaceName { get; init; }
            public string? DataSpaceType { get; init; }
            public string? PartitionColumnName { get; set; }
            public List<IndexColumnScriptInfo> KeyColumns { get; } = new();
            public List<IndexColumnScriptInfo> IncludedColumns { get; } = new();
        }

        private sealed class TriggerScriptInfo
        {
            public string Name { get; init; } = string.Empty;
            public bool IsDisabled { get; init; }
            public string? Definition { get; init; }
            public bool UsesAnsiNulls { get; init; }
            public bool UsesQuotedIdentifier { get; init; }
        }
    }
}
