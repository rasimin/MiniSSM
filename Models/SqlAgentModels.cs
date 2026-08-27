using System;

namespace SSMS
{
    public sealed class SqlAgentServiceStatus
    {
        public string ServiceName { get; init; } = string.Empty;
        public int StatusCode { get; init; }
        public string StatusDescription { get; init; } = string.Empty;
        public DateTime? LastStartupTime { get; init; }
        public bool IsAvailable { get; init; }

        public bool IsRunning => StatusCode == 4;

        public string StatusDisplay => IsAvailable
            ? StatusDescription
            : "Unavailable";
    }

    public sealed class SqlAgentJob
    {
        public Guid JobId { get; init; }
        public string Name { get; init; } = string.Empty;
        public bool Enabled { get; init; }
        public bool IsRunning { get; init; }
        public string Description { get; init; } = string.Empty;
        public string OwnerName { get; init; } = string.Empty;
        public DateTime? LastRunTime { get; init; }
        public int LastRunStatusCode { get; init; }
        public string LastRunMessage { get; init; } = string.Empty;
        public TimeSpan? LastRunDuration { get; init; }
        public string ScheduleName { get; init; } = string.Empty;
        public DateTime? NextRunTime { get; init; }

        public string EnabledDisplay => Enabled ? "Yes" : "No";
        public string RunningDisplay => IsRunning ? "Running" : "Idle";
        public string LastRunStatusDisplay => SqlAgentValueFormatter.GetRunStatusDisplay(LastRunStatusCode);
        public string LastRunDisplay => LastRunTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "Never";
        public string LastRunDurationDisplay => LastRunDuration.HasValue
            ? SqlAgentValueFormatter.FormatDuration(LastRunDuration.Value)
            : "-";
        public string NextRunDisplay => NextRunTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-";
        public string ScheduleDisplay => string.IsNullOrWhiteSpace(ScheduleName) ? "-" : ScheduleName;
    }

    public sealed class SqlAgentJobStep
    {
        public int StepId { get; init; }
        public string StepName { get; init; } = string.Empty;
        public string Subsystem { get; init; } = string.Empty;
        public string DatabaseName { get; init; } = string.Empty;
        public string Command { get; init; } = string.Empty;
        public int RetryAttempts { get; init; }
        public int RetryInterval { get; init; }
    }

    public sealed class SqlAgentSchedule
    {
        public int ScheduleId { get; init; }
        public string Name { get; init; } = string.Empty;
        public bool Enabled { get; init; }
        public string FrequencyDescription { get; init; } = string.Empty;
        public DateTime? ActiveStartTime { get; init; }
        public DateTime? ActiveEndTime { get; init; }
        public DateTime? NextRunTime { get; init; }

        public string EnabledDisplay => Enabled ? "Yes" : "No";
        public string ActiveWindowDisplay => ActiveStartTime.HasValue
            ? $"{ActiveStartTime.Value:yyyy-MM-dd HH:mm:ss} - {(ActiveEndTime.HasValue ? ActiveEndTime.Value.ToString("yyyy-MM-dd HH:mm:ss") : "No end")}" 
            : "-";
        public string NextRunDisplay => NextRunTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-";
    }

    public sealed class SqlAgentJobHistory
    {
        public int InstanceId { get; init; }
        public int StepId { get; init; }
        public string StepName { get; init; } = string.Empty;
        public int RunStatusCode { get; init; }
        public DateTime? RunTime { get; init; }
        public TimeSpan? Duration { get; init; }
        public int Retries { get; init; }
        public string Message { get; init; } = string.Empty;

        public string RunStatusDisplay => SqlAgentValueFormatter.GetRunStatusDisplay(RunStatusCode);
        public string RunTimeDisplay => RunTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-";
        public string DurationDisplay => Duration.HasValue
            ? SqlAgentValueFormatter.FormatDuration(Duration.Value)
            : "-";
        public string StepDisplay => StepId == 0 ? "Job outcome" : $"{StepId}: {StepName}";
    }

    internal static class SqlAgentValueFormatter
    {
        public static string GetRunStatusDisplay(int statusCode) => statusCode switch
        {
            0 => "Failed",
            1 => "Succeeded",
            2 => "Retry",
            3 => "Canceled",
            4 => "In Progress",
            _ => "Never run"
        };

        public static string FormatDuration(TimeSpan duration)
        {
            return duration.TotalHours >= 1
                ? duration.ToString(@"h\:mm\:ss")
                : duration.ToString(@"m\:ss");
        }

        public static DateTime? CombineDateAndTime(int dateValue, int timeValue)
        {
            if (dateValue <= 0)
            {
                return null;
            }

            if (!DateTime.TryParseExact(
                    dateValue.ToString("D8"),
                    "yyyyMMdd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out DateTime date))
            {
                return null;
            }

            int hours = Math.Clamp(timeValue / 10000, 0, 23);
            int minutes = Math.Clamp((timeValue % 10000) / 100, 0, 59);
            int seconds = Math.Clamp(timeValue % 100, 0, 59);
            return date.Add(new TimeSpan(hours, minutes, seconds));
        }

        public static string GetFrequencyDescription(
            int frequencyType,
            int frequencyInterval,
            int subdayType,
            int subdayInterval,
            int relativeInterval,
            int recurrenceFactor)
        {
            string subday = subdayType switch
            {
                2 => $" every {subdayInterval} second(s)",
                4 => $" every {subdayInterval} minute(s)",
                8 => $" every {subdayInterval} hour(s)",
                _ => string.Empty
            };

            return frequencyType switch
            {
                1 => "Once",
                4 => $"Daily (every {Math.Max(1, recurrenceFactor)} day(s)){subday}",
                8 => $"Weekly ({GetWeeklyDays(frequencyInterval)}, every {Math.Max(1, recurrenceFactor)} week(s)){subday}",
                16 => $"Monthly (day {frequencyInterval}, every {Math.Max(1, recurrenceFactor)} month(s)){subday}",
                32 => $"Monthly relative ({GetRelativeInterval(relativeInterval)}, every {Math.Max(1, recurrenceFactor)} month(s)){subday}",
                64 => "When SQL Server Agent starts",
                128 => "When computer is idle",
                _ => "Custom / unknown schedule"
            };
        }

        private static string GetWeeklyDays(int interval)
        {
            string[] days = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
            var selected = new System.Collections.Generic.List<string>();
            for (int index = 0; index < days.Length; index++)
            {
                if ((interval & (1 << index)) != 0)
                {
                    selected.Add(days[index]);
                }
            }

            return selected.Count == 0 ? "unspecified days" : string.Join(", ", selected);
        }

        private static string GetRelativeInterval(int interval) => interval switch
        {
            1 => "Sunday",
            2 => "Monday",
            3 => "Tuesday",
            4 => "Wednesday",
            5 => "Thursday",
            6 => "Friday",
            7 => "Saturday",
            8 => "Day",
            9 => "Weekday",
            10 => "Weekend day",
            _ => "relative day"
        };
    }
}
