using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace SSMS
{
    public static class QueryHistoryService
    {
        private const int MaximumStoredEntries = 10000;
        private static readonly SemaphoreSlim DatabaseLock = new(1, 1);
        private static bool _isInitialized;

        public static string DatabasePath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MiniSSMS",
            "Data",
            "query-history.db");

        private static string ConnectionString => new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        public static async Task TryAddAsync(QueryHistoryEntry entry)
        {
            try
            {
                await DatabaseLock.WaitAsync();
                try
                {
                    await EnsureInitializedAsync();
                    await using var connection = new SqliteConnection(ConnectionString);
                    await connection.OpenAsync();
                    await using var transaction = await connection.BeginTransactionAsync();

                    await using (var command = connection.CreateCommand())
                    {
                        command.Transaction = (SqliteTransaction)transaction;
                        command.CommandText = @"
INSERT INTO QueryExecutionLog
(
    ExecutedAtUtc, ServerName, StartedDatabaseName, EffectiveDatabaseName,
    QueryText, DurationMilliseconds, ExecutionStatus, ResultMessage,
    RowsAffected, ResultRowCount, ErrorMessage
)
VALUES
(
    @ExecutedAtUtc, @ServerName, @StartedDatabaseName, @EffectiveDatabaseName,
    @QueryText, @DurationMilliseconds, @ExecutionStatus, @ResultMessage,
    @RowsAffected, @ResultRowCount, @ErrorMessage
);";
                        command.Parameters.AddWithValue("@ExecutedAtUtc", entry.ExecutedAtUtc.ToString("O"));
                        command.Parameters.AddWithValue("@ServerName", entry.ServerName);
                        command.Parameters.AddWithValue("@StartedDatabaseName", entry.StartedDatabaseName);
                        command.Parameters.AddWithValue("@EffectiveDatabaseName", entry.EffectiveDatabaseName);
                        command.Parameters.AddWithValue("@QueryText", entry.QueryText);
                        command.Parameters.AddWithValue("@DurationMilliseconds", entry.DurationMilliseconds);
                        command.Parameters.AddWithValue("@ExecutionStatus", entry.ExecutionStatus);
                        command.Parameters.AddWithValue("@ResultMessage", entry.ResultMessage);
                        command.Parameters.AddWithValue("@RowsAffected", (object?)entry.RowsAffected ?? DBNull.Value);
                        command.Parameters.AddWithValue("@ResultRowCount", entry.ResultRowCount);
                        command.Parameters.AddWithValue("@ErrorMessage", entry.ErrorMessage);
                        await command.ExecuteNonQueryAsync();
                    }

                    await using (var cleanupCommand = connection.CreateCommand())
                    {
                        cleanupCommand.Transaction = (SqliteTransaction)transaction;
                        cleanupCommand.CommandText = @"
DELETE FROM QueryExecutionLog
WHERE Id IN
(
    SELECT Id
    FROM QueryExecutionLog
    ORDER BY Id DESC
    LIMIT -1 OFFSET @MaximumStoredEntries
);";
                        cleanupCommand.Parameters.AddWithValue("@MaximumStoredEntries", MaximumStoredEntries);
                        await cleanupCommand.ExecuteNonQueryAsync();
                    }

                    await transaction.CommitAsync();
                }
                finally
                {
                    DatabaseLock.Release();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "Failed to save query execution history.");
            }
        }

        public static async Task<List<QueryHistoryEntry>> GetLatestAsync(
            int maximumRows = 300,
            DateTimeOffset? executedFromUtc = null,
            DateTimeOffset? executedBeforeUtc = null,
            string? databaseFilter = null,
            string? sqlFilter = null)
        {
            await DatabaseLock.WaitAsync();
            try
            {
                await EnsureInitializedAsync();
                await using var connection = new SqliteConnection(ConnectionString);
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = @"
SELECT Id, ExecutedAtUtc, ServerName, StartedDatabaseName, EffectiveDatabaseName,
       QueryText, DurationMilliseconds, ExecutionStatus, ResultMessage,
       RowsAffected, ResultRowCount, ErrorMessage
FROM QueryExecutionLog
WHERE (@ExecutedFromUtc IS NULL OR ExecutedAtUtc >= @ExecutedFromUtc)
  AND (@ExecutedBeforeUtc IS NULL OR ExecutedAtUtc < @ExecutedBeforeUtc)
  AND
  (
      @DatabaseFilter IS NULL
      OR StartedDatabaseName LIKE @DatabaseFilter ESCAPE '\'
      OR EffectiveDatabaseName LIKE @DatabaseFilter ESCAPE '\'
  )
  AND (@SqlFilter IS NULL OR QueryText LIKE @SqlFilter ESCAPE '\')
ORDER BY Id DESC
LIMIT @MaximumRows;";
                command.Parameters.AddWithValue("@MaximumRows", Math.Clamp(maximumRows, 1, 300));
                command.Parameters.AddWithValue(
                    "@ExecutedFromUtc",
                    executedFromUtc.HasValue ? executedFromUtc.Value.ToString("O") : DBNull.Value);
                command.Parameters.AddWithValue(
                    "@ExecutedBeforeUtc",
                    executedBeforeUtc.HasValue ? executedBeforeUtc.Value.ToString("O") : DBNull.Value);
                command.Parameters.AddWithValue(
                    "@DatabaseFilter",
                    string.IsNullOrWhiteSpace(databaseFilter)
                        ? DBNull.Value
                        : $"%{EscapeLikePattern(databaseFilter.Trim())}%");
                command.Parameters.AddWithValue(
                    "@SqlFilter",
                    string.IsNullOrWhiteSpace(sqlFilter)
                        ? DBNull.Value
                        : $"%{EscapeLikePattern(sqlFilter.Trim())}%");

                var entries = new List<QueryHistoryEntry>();
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    entries.Add(new QueryHistoryEntry
                    {
                        Id = reader.GetInt64(0),
                        ExecutedAtUtc = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                        ServerName = reader.GetString(2),
                        StartedDatabaseName = reader.GetString(3),
                        EffectiveDatabaseName = reader.GetString(4),
                        QueryText = reader.GetString(5),
                        DurationMilliseconds = reader.GetInt64(6),
                        ExecutionStatus = reader.GetString(7),
                        ResultMessage = reader.GetString(8),
                        RowsAffected = reader.IsDBNull(9) ? null : reader.GetInt32(9),
                        ResultRowCount = reader.GetInt32(10),
                        ErrorMessage = reader.GetString(11)
                    });
                }
                return entries;
            }
            finally
            {
                DatabaseLock.Release();
            }
        }

        private static string EscapeLikePattern(string value)
        {
            return value
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("%", "\\%", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal);
        }

        private static async Task EnsureInitializedAsync()
        {
            if (_isInitialized)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
PRAGMA journal_mode = WAL;
PRAGMA busy_timeout = 5000;

CREATE TABLE IF NOT EXISTS QueryExecutionLog
(
    Id                    INTEGER PRIMARY KEY AUTOINCREMENT,
    ExecutedAtUtc         TEXT    NOT NULL,
    ServerName            TEXT    NOT NULL,
    StartedDatabaseName   TEXT    NOT NULL,
    EffectiveDatabaseName TEXT    NOT NULL,
    QueryText             TEXT    NOT NULL,
    DurationMilliseconds  INTEGER NOT NULL,
    ExecutionStatus       TEXT    NOT NULL,
    ResultMessage         TEXT    NOT NULL,
    RowsAffected          INTEGER NULL,
    ResultRowCount        INTEGER NOT NULL,
    ErrorMessage          TEXT    NOT NULL
);

CREATE INDEX IF NOT EXISTS IX_QueryExecutionLog_ExecutedAtUtc
ON QueryExecutionLog (ExecutedAtUtc DESC);";
            await command.ExecuteNonQueryAsync();
            _isInitialized = true;
        }
    }
}
