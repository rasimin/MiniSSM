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

    public static partial class DatabaseHelper
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
