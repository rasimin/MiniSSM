using System;

namespace SSMS
{
    public sealed class QueryHistoryEntry
    {
        public long Id { get; init; }
        public DateTimeOffset ExecutedAtUtc { get; init; }
        public string ServerName { get; init; } = string.Empty;
        public string StartedDatabaseName { get; init; } = string.Empty;
        public string EffectiveDatabaseName { get; init; } = string.Empty;
        public string QueryText { get; init; } = string.Empty;
        public long DurationMilliseconds { get; init; }
        public string ExecutionStatus { get; init; } = string.Empty;
        public string ResultMessage { get; init; } = string.Empty;
        public int? RowsAffected { get; init; }
        public int ResultRowCount { get; init; }
        public string ErrorMessage { get; init; } = string.Empty;

        public string ExecutedAtDisplay => ExecutedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        public string DurationDisplay => $"{DurationMilliseconds:N0} ms";
        public string DatabaseDisplay => string.IsNullOrWhiteSpace(EffectiveDatabaseName)
            ? StartedDatabaseName
            : EffectiveDatabaseName;
        public string RowsDisplay => RowsAffected.HasValue
            ? $"{RowsAffected:N0} affected"
            : ResultRowCount > 0 ? $"{ResultRowCount:N0} returned" : "-";
        public string QueryPreview
        {
            get
            {
                string singleLine = QueryText.Replace('\r', ' ').Replace('\n', ' ').Trim();
                return singleLine.Length <= 140 ? singleLine : singleLine[..140] + "…";
            }
        }
    }
}
