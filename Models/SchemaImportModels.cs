using System;
using System.Collections.Generic;

namespace SSMS
{
    public enum SchemaImportObjectType
    {
        Unknown,
        Database,
        Schema,
        Type,
        Sequence,
        Table,
        Constraint,
        ForeignKey,
        Index,
        View,
        Function,
        Procedure,
        Trigger,
        Data,
        Permission,
        Other
    }

    public enum SchemaImportStatus
    {
        Pending,
        Success,
        Retried,
        Failed,
        Skipped
    }

    public sealed class SchemaImportBatchInfo
    {
        public int Index { get; init; }
        public string Text { get; init; } = string.Empty;
        public int RepeatCount { get; init; } = 1;
        public int StartLineNumber { get; init; } = 1;
        public SchemaImportObjectType ObjectType { get; init; }
        public string ObjectName { get; init; } = string.Empty;
        public int Phase { get; init; }
        public bool IsObject => ObjectType != SchemaImportObjectType.Unknown;
        public List<string> Dependencies { get; } = new();
        public List<string> Warnings { get; } = new();

        public string DisplayName => string.IsNullOrWhiteSpace(ObjectName)
            ? $"Batch {Index + 1}"
            : ObjectName;

        public string TypeName => ObjectType.ToString();
    }

    public sealed class SchemaImportPlan
    {
        public string FilePath { get; init; } = string.Empty;
        public long FileLength { get; init; }
        public string ScriptDatabaseName { get; set; } = string.Empty;
        public List<SchemaImportBatchInfo> Batches { get; } = new();
        public List<string> Warnings { get; } = new();

        public int TotalRepeatUnits
        {
            get
            {
                int total = 0;
                foreach (SchemaImportBatchInfo batch in Batches)
                {
                    total += Math.Max(1, batch.RepeatCount);
                }
                return total;
            }
        }

        public int Count(SchemaImportObjectType type)
        {
            int count = 0;
            foreach (SchemaImportBatchInfo batch in Batches)
            {
                if (batch.ObjectType == type)
                {
                    count++;
                }
            }
            return count;
        }
    }

    public sealed class SchemaImportItemResult
    {
        public int BatchIndex { get; init; }
        public string ObjectName { get; init; } = string.Empty;
        public string ObjectType { get; init; } = string.Empty;
        public int SourceLine { get; init; }
        public int Phase { get; init; }
        public SchemaImportStatus Status { get; set; }
        public int Attempts { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public string DependencyText { get; init; } = string.Empty;
        public string SqlText { get; init; } = string.Empty;

        public string StatusText => Status.ToString();
        public string DisplayName => string.IsNullOrWhiteSpace(ObjectName)
            ? $"Batch {BatchIndex + 1}"
            : ObjectName;
    }

    public sealed class SchemaImportProgress
    {
        public int Completed { get; init; }
        public int Total { get; init; }
        public string Message { get; init; } = string.Empty;
        public SchemaImportItemResult? Result { get; init; }

        public int Percentage => Total <= 0
            ? 0
            : Math.Min(100, (int)Math.Round(Completed * 100d / Total));
    }

    public sealed class SchemaImportRetryRound
    {
        public int Round { get; init; }
        public int Requested { get; init; }
        public int Success { get; init; }
        public int Failed { get; init; }
        public int DependencyFailures { get; init; }
    }

    public sealed class SchemaImportRetryResult
    {
        public List<SchemaImportItemResult> Results { get; } = new();
        public List<SchemaImportRetryRound> Rounds { get; } = new();
        public bool ReachedRoundLimit { get; set; }
        public bool StoppedWithoutProgress { get; set; }
    }
}
