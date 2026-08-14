using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace SSMS
{
    public sealed class SqlTraceSession : IAsyncDisposable
    {
        private const int TraceMaxFileSizeMb = 20;
        private const int TraceFileCount = 2;
        private readonly string _connectionString;
        private readonly string _traceFileBasePath;
        private readonly string _traceFilePath;
        private int _traceId;
        private bool _stopped;

        private SqlTraceSession(string connectionString, string traceFileBasePath)
        {
            _connectionString = DatabaseHelper.BuildConnectionString(connectionString, "master");
            _traceFileBasePath = traceFileBasePath;
            _traceFilePath = traceFileBasePath.EndsWith(".trc", StringComparison.OrdinalIgnoreCase)
                ? traceFileBasePath
                : $"{traceFileBasePath}.trc";
        }

        public string TraceFilePath => _traceFilePath;

        public static async Task<SqlTraceSession> StartAsync(
            string connectionString,
            string? databaseName,
            CancellationToken cancellationToken = default)
        {
            string masterConnectionString = DatabaseHelper.BuildConnectionString(connectionString, "master");
            string traceFilePath;

            await using (var connection = new SqlConnection(masterConnectionString))
            {
                await connection.OpenAsync(cancellationToken);
                traceFilePath = await ResolveTraceFilePathAsync(connection, cancellationToken);
            }

            var session = new SqlTraceSession(connectionString, traceFilePath);
            try
            {
                await session.StartInternalAsync(databaseName, cancellationToken);
                return session;
            }
            catch
            {
                await session.StopAsync();
                throw;
            }
        }

        public async Task<IReadOnlyList<SqlTraceEvent>> ReadEventsAsync(
            long afterSequence,
            CancellationToken cancellationToken = default)
        {
            if (_stopped)
            {
                return Array.Empty<SqlTraceEvent>();
            }

            var events = new List<SqlTraceEvent>();
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            const string query = @"
SELECT TOP (500)
    EventSequence,
    EventClass,
    StartTime,
    DatabaseName,
    LoginName,
    HostName,
    ApplicationName,
    SPID,
    Duration,
    CPU,
    Reads,
    Writes,
    TextData
FROM sys.fn_trace_gettable(@TraceFilePath, 2)
WHERE EventSequence > @AfterSequence
ORDER BY EventSequence ASC;";

            await using var command = new SqlCommand(query, connection)
            {
                CommandTimeout = 15
            };
            command.Parameters.Add("@TraceFilePath", SqlDbType.NVarChar, 256).Value = _traceFilePath;
            command.Parameters.Add("@AfterSequence", SqlDbType.BigInt).Value = afterSequence;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                events.Add(new SqlTraceEvent
                {
                    EventSequence = GetInt64(reader, "EventSequence"),
                    EventName = GetEventName(GetInt32(reader, "EventClass")),
                    StartTime = GetDateTime(reader, "StartTime"),
                    DatabaseName = GetString(reader, "DatabaseName"),
                    LoginName = GetString(reader, "LoginName"),
                    HostName = GetString(reader, "HostName"),
                    ApplicationName = GetString(reader, "ApplicationName"),
                    Spid = GetInt32(reader, "SPID"),
                    DurationMicroseconds = GetInt64(reader, "Duration"),
                    CpuMicroseconds = GetInt64(reader, "CPU"),
                    Reads = GetInt64(reader, "Reads"),
                    Writes = GetInt64(reader, "Writes"),
                    TextData = GetString(reader, "TextData")
                });
            }

            return events;
        }

        public async Task StopAsync()
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            if (_traceId <= 0)
            {
                return;
            }

            try
            {
                await using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync();

                await ExecuteTraceProcedureAsync(connection, "sys.sp_trace_setstatus", _traceId, 0, null, null);
                await ExecuteTraceProcedureAsync(connection, "sys.sp_trace_setstatus", _traceId, 2, null, null);
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"Failed to stop SQL trace {_traceId}");
            }
            finally
            {
                _traceId = 0;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
        }

        private async Task StartInternalAsync(string? databaseName, CancellationToken cancellationToken)
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using (var createCommand = new SqlCommand("sys.sp_trace_create", connection)
            {
                CommandType = CommandType.StoredProcedure,
                CommandTimeout = 15
            })
            {
                var traceIdParameter = new SqlParameter("@traceid", SqlDbType.Int)
                {
                    Direction = ParameterDirection.Output
                };
                createCommand.Parameters.Add(traceIdParameter);
                createCommand.Parameters.Add("@options", SqlDbType.Int).Value = 2;
                createCommand.Parameters.Add("@tracefile", SqlDbType.NVarChar, 256).Value = _traceFileBasePath;
                createCommand.Parameters.Add("@maxfilesize", SqlDbType.BigInt).Value = TraceMaxFileSizeMb;
                createCommand.Parameters.Add("@stoptime", SqlDbType.DateTime).Value = DBNull.Value;
                createCommand.Parameters.Add("@filecount", SqlDbType.Int).Value = TraceFileCount;
                await createCommand.ExecuteNonQueryAsync(cancellationToken);
                _traceId = Convert.ToInt32(traceIdParameter.Value);
            }

            int[] eventClasses = { 10, 12 }; // RPC:Completed and SQL:BatchCompleted
            int[] eventColumns =
            {
                1, 3, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 26, 27, 35, 40, 51
            };

            foreach (int eventClass in eventClasses)
            {
                foreach (int columnId in eventColumns)
                {
                    await ExecuteTraceProcedureAsync(
                        connection,
                        "sys.sp_trace_setevent",
                        _traceId,
                        eventClass,
                        columnId,
                        true,
                        cancellationToken);
                }
            }

            if (!string.IsNullOrWhiteSpace(databaseName))
            {
                int? databaseId = await GetDatabaseIdAsync(connection, databaseName, cancellationToken);
                if (!databaseId.HasValue)
                {
                    throw new InvalidOperationException($"Database '{databaseName}' was not found or is not accessible.");
                }

                await using var filterCommand = new SqlCommand("sys.sp_trace_setfilter", connection)
                {
                    CommandType = CommandType.StoredProcedure,
                    CommandTimeout = 15
                };
                filterCommand.Parameters.Add("@traceid", SqlDbType.Int).Value = _traceId;
                filterCommand.Parameters.Add("@columnid", SqlDbType.Int).Value = 3;
                filterCommand.Parameters.Add("@logical_operator", SqlDbType.Int).Value = 0;
                filterCommand.Parameters.Add("@comparison_operator", SqlDbType.Int).Value = 0;
                filterCommand.Parameters.Add("@value", SqlDbType.Int).Value = databaseId.Value;
                await filterCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await ExecuteTraceProcedureAsync(
                connection,
                "sys.sp_trace_setstatus",
                _traceId,
                1,
                null,
                null,
                cancellationToken);
        }

        private static async Task<string> ResolveTraceFilePathAsync(
            SqlConnection connection,
            CancellationToken cancellationToken)
        {
            const string query = @"
SELECT
    CONVERT(nvarchar(260), SERVERPROPERTY('InstanceDefaultLogPath')) AS DefaultLogPath,
    CONVERT(nvarchar(260), SERVERPROPERTY('ErrorLogFileName')) AS ErrorLogFileName;";

            await using var command = new SqlCommand(query, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return $"C:\\Windows\\Temp\\MiniSSMS_{Guid.NewGuid():N}";
            }

            string defaultLogPath = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            string errorLogFileName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            string directory = defaultLogPath;

            if (string.IsNullOrWhiteSpace(directory) && !string.IsNullOrWhiteSpace(errorLogFileName))
            {
                directory = Path.GetDirectoryName(errorLogFileName) ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = @"C:\Windows\Temp";
            }

            return Path.Combine(directory, $"MiniSSMS_{Guid.NewGuid():N}");
        }

        private static async Task<int?> GetDatabaseIdAsync(
            SqlConnection connection,
            string databaseName,
            CancellationToken cancellationToken)
        {
            await using var command = new SqlCommand("SELECT DB_ID(@DatabaseName);", connection);
            command.Parameters.Add("@DatabaseName", SqlDbType.NVarChar, 128).Value = databaseName;
            object? value = await command.ExecuteScalarAsync(cancellationToken);
            return value == null || value == DBNull.Value ? null : Convert.ToInt32(value);
        }

        private static async Task ExecuteTraceProcedureAsync(
            SqlConnection connection,
            string procedureName,
            int traceId,
            int eventOrStatus,
            int? columnId,
            object? onValue,
            CancellationToken cancellationToken = default)
        {
            await using var command = new SqlCommand(procedureName, connection)
            {
                CommandType = CommandType.StoredProcedure,
                CommandTimeout = 15
            };
            command.Parameters.Add("@traceid", SqlDbType.Int).Value = traceId;

            if (procedureName.EndsWith("sp_trace_setevent", StringComparison.OrdinalIgnoreCase))
            {
                command.Parameters.Add("@eventid", SqlDbType.Int).Value = eventOrStatus;
                command.Parameters.Add("@columnid", SqlDbType.Int).Value = columnId!.Value;
                command.Parameters.Add("@on", SqlDbType.Int).Value = Convert.ToInt32(onValue);
            }
            else
            {
                command.Parameters.Add("@status", SqlDbType.Int).Value = eventOrStatus;
            }

            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static string GetEventName(int eventClass) => eventClass switch
        {
            10 => "RPC:Completed",
            12 => "SQL:BatchCompleted",
            _ => $"Event {eventClass}"
        };

        private static string GetString(SqlDataReader reader, string columnName)
        {
            int ordinal = reader.GetOrdinal(columnName);
            return reader.IsDBNull(ordinal) ? string.Empty : Convert.ToString(reader.GetValue(ordinal)) ?? string.Empty;
        }

        private static int GetInt32(SqlDataReader reader, string columnName)
        {
            int ordinal = reader.GetOrdinal(columnName);
            return reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal));
        }

        private static long GetInt64(SqlDataReader reader, string columnName)
        {
            int ordinal = reader.GetOrdinal(columnName);
            return reader.IsDBNull(ordinal) ? 0L : Convert.ToInt64(reader.GetValue(ordinal));
        }

        private static DateTime GetDateTime(SqlDataReader reader, string columnName)
        {
            int ordinal = reader.GetOrdinal(columnName);
            return reader.IsDBNull(ordinal) ? DateTime.MinValue : Convert.ToDateTime(reader.GetValue(ordinal));
        }
    }
}
