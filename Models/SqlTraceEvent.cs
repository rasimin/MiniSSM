using System;

namespace SSMS
{
    public sealed class SqlTraceEvent
    {
        public long EventSequence { get; init; }
        public string EventName { get; init; } = string.Empty;
        public DateTime StartTime { get; init; }
        public string DatabaseName { get; init; } = string.Empty;
        public string LoginName { get; init; } = string.Empty;
        public string HostName { get; init; } = string.Empty;
        public string ApplicationName { get; init; } = string.Empty;
        public int Spid { get; init; }
        public long DurationMicroseconds { get; init; }
        public long CpuMicroseconds { get; init; }
        public long Reads { get; init; }
        public long Writes { get; init; }
        public string TextData { get; init; } = string.Empty;

        public double DurationMilliseconds => DurationMicroseconds / 1000d;
        public double CpuMilliseconds => CpuMicroseconds / 1000d;
    }
}
