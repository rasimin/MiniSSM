using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace SSMS
{
    public sealed class SqlAgentService
    {
        private readonly string _connectionString;

        public SqlAgentService(string connectionString)
        {
            _connectionString = connectionString;
        }

        private string MsdbConnectionString => DatabaseHelper.BuildConnectionString(_connectionString, "msdb");

        public async Task<SqlAgentServiceStatus> GetServiceStatusAsync(CancellationToken cancellationToken = default)
        {
            const string query = @"
SELECT TOP (1)
       servicename,
       status,
       status_desc,
       last_startup_time
FROM sys.dm_server_services
WHERE servicename LIKE N'SQL Server Agent%'
ORDER BY servicename;";

            await using var connection = new SqlConnection(MsdbConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(query, connection)
            {
                CommandTimeout = 30
            };
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return new SqlAgentServiceStatus
                {
                    IsAvailable = false,
                    StatusDescription = "SQL Server Agent was not found on this instance."
                };
            }

            return new SqlAgentServiceStatus
            {
                IsAvailable = true,
                ServiceName = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                StatusCode = reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                StatusDescription = reader.IsDBNull(2) ? "Unknown" : reader.GetString(2),
                LastStartupTime = reader.IsDBNull(3) ? null : reader.GetDateTime(3)
            };
        }

        public async Task<List<SqlAgentJob>> GetJobsAsync(
            string? searchText = null,
            CancellationToken cancellationToken = default)
        {
            const string query = @"
WITH LatestOutcome AS
(
    SELECT h.job_id,
           h.run_status,
           h.run_date,
           h.run_time,
           h.run_duration,
           h.message,
           ROW_NUMBER() OVER (PARTITION BY h.job_id ORDER BY h.instance_id DESC) AS row_num
    FROM msdb.dbo.sysjobhistory h
    WHERE h.step_id = 0
),
LatestActivity AS
(
    SELECT ja.job_id,
           ja.start_execution_date,
           ja.stop_execution_date,
           ROW_NUMBER() OVER (PARTITION BY ja.job_id ORDER BY ja.session_id DESC) AS row_num
    FROM msdb.dbo.sysjobactivity ja
)
SELECT j.job_id,
       j.name,
       j.enabled,
       ISNULL(j.description, N''),
       ISNULL(SUSER_SNAME(j.owner_sid), N''),
       CAST(CASE WHEN activity.start_execution_date IS NOT NULL
                      AND activity.stop_execution_date IS NULL
                 THEN 1 ELSE 0 END AS bit) AS is_running,
       activity.start_execution_date,
       ISNULL(outcome.run_status, -1),
       ISNULL(outcome.run_date, 0),
       ISNULL(outcome.run_time, 0),
       ISNULL(outcome.run_duration, 0),
       ISNULL(outcome.message, N''),
       ISNULL(next_schedule.schedule_name, N''),
       ISNULL(next_schedule.next_run_date, 0),
       ISNULL(next_schedule.next_run_time, 0)
FROM msdb.dbo.sysjobs j
LEFT JOIN LatestOutcome outcome
       ON outcome.job_id = j.job_id
      AND outcome.row_num = 1
LEFT JOIN LatestActivity activity
       ON activity.job_id = j.job_id
      AND activity.row_num = 1
OUTER APPLY
(
    SELECT TOP (1)
           s.name AS schedule_name,
           js.next_run_date,
           js.next_run_time
    FROM msdb.dbo.sysjobschedules js
    INNER JOIN msdb.dbo.sysschedules s ON s.schedule_id = js.schedule_id
    WHERE js.job_id = j.job_id
    ORDER BY CASE WHEN js.next_run_date = 0 THEN 1 ELSE 0 END,
             js.next_run_date,
             js.next_run_time,
             s.name
) next_schedule
WHERE @SearchPattern = N'%%'
   OR j.name LIKE @SearchPattern
ORDER BY j.name;";

            var jobs = new List<SqlAgentJob>();
            string search = searchText?.Trim() ?? string.Empty;
            string searchPattern = string.IsNullOrWhiteSpace(search) ? "%%" : $"%{EscapeLikePattern(search)}%";

            await using var connection = new SqlConnection(MsdbConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(query, connection)
            {
                CommandTimeout = 30
            };
            command.Parameters.Add("@SearchPattern", SqlDbType.NVarChar, 512).Value = searchPattern;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                jobs.Add(new SqlAgentJob
                {
                    JobId = reader.GetGuid(0),
                    Name = reader.GetString(1),
                    Enabled = Convert.ToBoolean(reader.GetValue(2)),
                    Description = reader.GetString(3),
                    OwnerName = reader.GetString(4),
                    IsRunning = Convert.ToBoolean(reader.GetValue(5)),
                    LastRunTime = CombineDateAndTime(
                        reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
                        reader.IsDBNull(9) ? 0 : reader.GetInt32(9)),
                    LastRunStatusCode = reader.GetInt32(7),
                    LastRunMessage = reader.GetString(11),
                    LastRunDuration = ParseAgentDuration(reader.GetInt32(10)),
                    ScheduleName = reader.GetString(12),
                    NextRunTime = CombineDateAndTime(
                        reader.GetInt32(13),
                        reader.GetInt32(14))
                });
            }

            return jobs;
        }

        public async Task<List<SqlAgentJobStep>> GetJobStepsAsync(
            Guid jobId,
            CancellationToken cancellationToken = default)
        {
            const string query = @"
SELECT step_id,
       step_name,
       subsystem,
       ISNULL(database_name, N''),
       ISNULL(command, N''),
       retry_attempts,
       retry_interval
FROM msdb.dbo.sysjobsteps
WHERE job_id = @JobId
ORDER BY step_id;";

            var steps = new List<SqlAgentJobStep>();
            await using var connection = new SqlConnection(MsdbConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(query, connection)
            {
                CommandTimeout = 30
            };
            command.Parameters.Add("@JobId", SqlDbType.UniqueIdentifier).Value = jobId;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                steps.Add(new SqlAgentJobStep
                {
                    StepId = reader.GetInt32(0),
                    StepName = reader.GetString(1),
                    Subsystem = reader.GetString(2),
                    DatabaseName = reader.GetString(3),
                    Command = reader.GetString(4),
                    RetryAttempts = reader.GetInt32(5),
                    RetryInterval = reader.GetInt32(6)
                });
            }

            return steps;
        }

        public async Task<List<SqlAgentSchedule>> GetJobSchedulesAsync(
            Guid jobId,
            CancellationToken cancellationToken = default)
        {
            const string query = @"
SELECT s.schedule_id,
       s.name,
       s.enabled,
       s.freq_type,
       s.freq_interval,
       s.freq_subday_type,
       s.freq_subday_interval,
       s.freq_relative_interval,
       s.freq_recurrence_factor,
       s.active_start_date,
       s.active_start_time,
       s.active_end_date,
       s.active_end_time,
       js.next_run_date,
       js.next_run_time
FROM msdb.dbo.sysjobschedules js
INNER JOIN msdb.dbo.sysschedules s ON s.schedule_id = js.schedule_id
WHERE js.job_id = @JobId
ORDER BY s.name;";

            var schedules = new List<SqlAgentSchedule>();
            await using var connection = new SqlConnection(MsdbConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(query, connection)
            {
                CommandTimeout = 30
            };
            command.Parameters.Add("@JobId", SqlDbType.UniqueIdentifier).Value = jobId;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                schedules.Add(new SqlAgentSchedule
                {
                    ScheduleId = reader.GetInt32(0),
                    Name = reader.GetString(1),
                    Enabled = Convert.ToBoolean(reader.GetValue(2)),
                    FrequencyDescription = SqlAgentValueFormatter.GetFrequencyDescription(
                        reader.GetInt32(3),
                        reader.GetInt32(4),
                        reader.GetInt32(5),
                        reader.GetInt32(6),
                        reader.GetInt32(7),
                        reader.GetInt32(8)),
                    ActiveStartTime = SqlAgentValueFormatter.CombineDateAndTime(
                        reader.GetInt32(9),
                        reader.GetInt32(10)),
                    ActiveEndTime = SqlAgentValueFormatter.CombineDateAndTime(
                        reader.GetInt32(11),
                        reader.GetInt32(12)),
                    NextRunTime = SqlAgentValueFormatter.CombineDateAndTime(
                        reader.GetInt32(13),
                        reader.GetInt32(14))
                });
            }

            return schedules;
        }

        public async Task<List<SqlAgentJobHistory>> GetJobHistoryAsync(
            Guid jobId,
            int maximumRows = 100,
            CancellationToken cancellationToken = default)
        {
            const string query = @"
SELECT TOP (@MaximumRows)
       h.instance_id,
       h.step_id,
       CASE WHEN h.step_id = 0 THEN N'(Job outcome)' ELSE ISNULL(s.step_name, N'(Unknown step)') END,
       h.run_status,
       h.run_date,
       h.run_time,
       h.run_duration,
       h.retries_attempted,
       ISNULL(h.message, N'')
FROM msdb.dbo.sysjobhistory h
LEFT JOIN msdb.dbo.sysjobsteps s
       ON s.job_id = h.job_id
      AND s.step_id = h.step_id
WHERE h.job_id = @JobId
ORDER BY h.instance_id DESC;";

            var history = new List<SqlAgentJobHistory>();
            await using var connection = new SqlConnection(MsdbConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(query, connection)
            {
                CommandTimeout = 30
            };
            command.Parameters.Add("@MaximumRows", SqlDbType.Int).Value = Math.Clamp(maximumRows, 1, 1000);
            command.Parameters.Add("@JobId", SqlDbType.UniqueIdentifier).Value = jobId;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                history.Add(new SqlAgentJobHistory
                {
                    InstanceId = reader.GetInt32(0),
                    StepId = reader.GetInt32(1),
                    StepName = reader.GetString(2),
                    RunStatusCode = reader.GetInt32(3),
                    RunTime = CombineDateAndTime(reader.GetInt32(4), reader.GetInt32(5)),
                    Duration = ParseAgentDuration(reader.GetInt32(6)),
                    Retries = reader.GetInt32(7),
                    Message = reader.GetString(8)
                });
            }

            return history;
        }

        public Task StartJobAsync(Guid jobId, CancellationToken cancellationToken = default)
        {
            return ExecuteJobProcedureAsync("msdb.dbo.sp_start_job", jobId, cancellationToken);
        }

        public Task StopJobAsync(Guid jobId, CancellationToken cancellationToken = default)
        {
            return ExecuteJobProcedureAsync("msdb.dbo.sp_stop_job", jobId, cancellationToken);
        }

        public async Task SetJobEnabledAsync(
            Guid jobId,
            bool enabled,
            CancellationToken cancellationToken = default)
        {
            await using var connection = new SqlConnection(MsdbConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand("msdb.dbo.sp_update_job", connection)
            {
                CommandType = CommandType.StoredProcedure,
                CommandTimeout = 30
            };
            command.Parameters.Add("@job_id", SqlDbType.UniqueIdentifier).Value = jobId;
            command.Parameters.Add("@enabled", SqlDbType.Int).Value = enabled ? 1 : 0;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private async Task ExecuteJobProcedureAsync(
            string procedureName,
            Guid jobId,
            CancellationToken cancellationToken)
        {
            await using var connection = new SqlConnection(MsdbConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(procedureName, connection)
            {
                CommandType = CommandType.StoredProcedure,
                CommandTimeout = 30
            };
            command.Parameters.Add("@job_id", SqlDbType.UniqueIdentifier).Value = jobId;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static string EscapeLikePattern(string value)
        {
            return value
                .Replace("[", "[[]", StringComparison.Ordinal)
                .Replace("%", "[%]", StringComparison.Ordinal)
                .Replace("_", "[_]", StringComparison.Ordinal);
        }

        private static DateTime? CombineDateAndTime(int dateValue, int timeValue)
        {
            if (dateValue <= 0)
            {
                return null;
            }

            if (!DateTime.TryParseExact(
                    dateValue.ToString("D8", CultureInfo.InvariantCulture),
                    "yyyyMMdd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTime date))
            {
                return null;
            }

            int hours = Math.Clamp(timeValue / 10000, 0, 23);
            int minutes = Math.Clamp((timeValue % 10000) / 100, 0, 59);
            int seconds = Math.Clamp(timeValue % 100, 0, 59);
            return date.Add(new TimeSpan(hours, minutes, seconds));
        }

        private static TimeSpan? ParseAgentDuration(int durationValue)
        {
            if (durationValue < 0)
            {
                return null;
            }

            int hours = durationValue / 10000;
            int minutes = Math.Clamp((durationValue % 10000) / 100, 0, 59);
            int seconds = Math.Clamp(durationValue % 100, 0, 59);
            return new TimeSpan(hours, minutes, seconds);
        }
    }
}
