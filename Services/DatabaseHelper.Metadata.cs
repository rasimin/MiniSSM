using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace SSMS
{
    public static partial class DatabaseHelper
    {
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

    }
}