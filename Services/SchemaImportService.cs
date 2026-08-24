using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace SSMS
{
    public sealed class SchemaImportService
    {
        private const int MaxDependencyRetryPasses = 3;
        private static readonly Regex UseStatementPattern = new(
            @"^\s*USE\s+(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_][\w$#@]*)\s*;?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        public Task<SchemaImportPlan> AnalyzeAsync(
            string filePath,
            CancellationToken cancellationToken = default)
        {
            return SchemaScriptParser.ParseFileAsync(filePath, cancellationToken);
        }

        public async Task<List<SchemaImportItemResult>> ImportAsync(
            SchemaImportPlan plan,
            string connectionString,
            string databaseName,
            bool createDatabase = false,
            IProgress<SchemaImportProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (string.IsNullOrWhiteSpace(connectionString)) throw new ArgumentException("Connection string is required.", nameof(connectionString));
            if (string.IsNullOrWhiteSpace(databaseName)) throw new ArgumentException("Target database is required.", nameof(databaseName));

            var results = new Dictionary<int, SchemaImportItemResult>();
            var pending = new List<SchemaImportBatchInfo>(plan.Batches);
            int completed = 0;
            int total = Math.Max(1, plan.TotalRepeatUnits);

            if (createDatabase)
            {
                await CreateDatabaseAsync(connectionString, databaseName, cancellationToken);
            }

            using var connection = new SqlConnection(DatabaseHelper.BuildConnectionString(connectionString, databaseName));
            await connection.OpenAsync(cancellationToken);

            for (int pass = 1; pass <= MaxDependencyRetryPasses && pending.Count > 0; pass++)
            {
                var deferred = new List<SchemaImportBatchInfo>();

                foreach (SchemaImportBatchInfo batch in pending)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    SchemaImportItemResult item = GetOrCreateResult(results, batch);
                    item.Attempts++;

                    if (IsUseStatement(batch.Text) || batch.ObjectType == SchemaImportObjectType.Database)
                    {
                        item.Status = SchemaImportStatus.Skipped;
                        item.ErrorMessage = IsUseStatement(batch.Text)
                            ? "USE statement skipped; the selected target database is authoritative."
                            : "CREATE DATABASE statement skipped; database creation is controlled by the import target selection.";
                        completed += Math.Max(1, batch.RepeatCount);
                        Report(progress, completed, total, $"Skipped USE statement at line {batch.StartLineNumber}.", item);
                        continue;
                    }

                    try
                    {
                        await ExecuteBatchAsync(connection, batch, cancellationToken);
                        item.Status = item.Attempts > 1 ? SchemaImportStatus.Retried : SchemaImportStatus.Success;
                        completed += Math.Max(1, batch.RepeatCount);
                        Report(progress, completed, total, $"{item.Status}: {item.DisplayName}", item);
                    }
                    catch (SqlException ex)
                    {
                        item.ErrorMessage = FormatSqlException(ex, batch.StartLineNumber);
                        if (IsDependencyError(ex) && pass < MaxDependencyRetryPasses)
                        {
                            deferred.Add(batch);
                            item.Status = SchemaImportStatus.Retried;
                            Report(progress, completed, total, $"Retry scheduled: {item.DisplayName}", item);
                        }
                        else
                        {
                            item.Status = SchemaImportStatus.Failed;
                            completed += Math.Max(1, batch.RepeatCount);
                            Report(progress, completed, total, $"Failed: {item.DisplayName}", item);
                        }
                    }
                    catch (Exception ex)
                    {
                        item.Status = SchemaImportStatus.Failed;
                        item.ErrorMessage = $"Line {batch.StartLineNumber}: {ex.Message}";
                        completed += Math.Max(1, batch.RepeatCount);
                        Report(progress, completed, total, $"Failed: {item.DisplayName}", item);
                    }
                }

                if (deferred.Count == pending.Count && pass == MaxDependencyRetryPasses)
                {
                    foreach (SchemaImportBatchInfo batch in deferred)
                    {
                        SchemaImportItemResult item = results[batch.Index];
                        item.Status = SchemaImportStatus.Failed;
                        completed += Math.Max(1, batch.RepeatCount);
                        Report(progress, completed, total, $"Failed after {MaxDependencyRetryPasses} attempts: {item.DisplayName}", item);
                    }
                    pending.Clear();
                }
                else
                {
                    pending = deferred;
                }
            }

            progress?.Report(new SchemaImportProgress
            {
                Completed = total,
                Total = total,
                Message = "Schema import completed."
            });

            return plan.Batches
                .Where(batch => results.ContainsKey(batch.Index))
                .Select(batch => results[batch.Index])
                .ToList();
        }

        public static string BuildReportText(
            SchemaImportPlan plan,
            IEnumerable<SchemaImportItemResult> results,
            string databaseName)
        {
            var rows = results.ToList();
            var sb = new StringBuilder();
            sb.AppendLine("MiniSSMS Schema Import Report");
            sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"File: {plan.FilePath}");
            sb.AppendLine($"Target database: {databaseName}");
            sb.AppendLine();
            sb.AppendLine($"Success: {rows.Count(row => row.Status is SchemaImportStatus.Success or SchemaImportStatus.Retried)}");
            sb.AppendLine($"Skipped: {rows.Count(row => row.Status == SchemaImportStatus.Skipped)}");
            sb.AppendLine($"Failed: {rows.Count(row => row.Status == SchemaImportStatus.Failed)}");
            sb.AppendLine();

            foreach (SchemaImportItemResult row in rows.Where(row => row.Status == SchemaImportStatus.Failed))
            {
                sb.AppendLine($"[{row.Status}] {row.DisplayName} ({row.ObjectType})");
                sb.AppendLine($"  Source line: {row.SourceLine}");
                sb.AppendLine($"  Attempts: {row.Attempts}");
                if (!string.IsNullOrWhiteSpace(row.DependencyText)) sb.AppendLine($"  Dependencies: {row.DependencyText}");
                sb.AppendLine($"  Error: {row.ErrorMessage}");
                sb.AppendLine("  SQL:");
                sb.AppendLine(row.SqlText.Trim());
                sb.AppendLine();
            }

            return sb.ToString();
        }

        private static async Task ExecuteBatchAsync(
            SqlConnection connection,
            SchemaImportBatchInfo batch,
            CancellationToken cancellationToken)
        {
            using var command = new SqlCommand(batch.Text, connection)
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
                    // The command may already have completed or been disposed.
                }
            });

            for (int repeat = 0; repeat < Math.Max(1, batch.RepeatCount); repeat++)
            {
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        private static SchemaImportItemResult GetOrCreateResult(
            IDictionary<int, SchemaImportItemResult> results,
            SchemaImportBatchInfo batch)
        {
            if (results.TryGetValue(batch.Index, out SchemaImportItemResult? existing))
            {
                return existing;
            }

            var item = new SchemaImportItemResult
            {
                BatchIndex = batch.Index,
                ObjectName = batch.DisplayName,
                ObjectType = batch.TypeName,
                SourceLine = batch.StartLineNumber,
                Phase = batch.Phase,
                Status = SchemaImportStatus.Pending,
                DependencyText = string.Join(", ", batch.Dependencies),
                SqlText = batch.Text
            };
            results[batch.Index] = item;
            return item;
        }

        private static void Report(
            IProgress<SchemaImportProgress>? progress,
            int completed,
            int total,
            string message,
            SchemaImportItemResult item)
        {
            progress?.Report(new SchemaImportProgress
            {
                Completed = completed,
                Total = total,
                Message = message,
                Result = item
            });
        }

        private static bool IsUseStatement(string sql)
        {
            string withoutComments = Regex.Replace(sql, @"(?s)/\*.*?\*/|--[^\r\n]*", string.Empty);
            return UseStatementPattern.IsMatch(withoutComments);
        }

        private static async Task CreateDatabaseAsync(
            string connectionString,
            string databaseName,
            CancellationToken cancellationToken)
        {
            if (databaseName.Length == 0 || databaseName.Length > 128 || databaseName.Contains(';'))
            {
                throw new InvalidOperationException("Database name must contain 1 to 128 characters and cannot contain ';'.");
            }

            string masterConnectionString = DatabaseHelper.BuildConnectionString(connectionString, "master");
            await using var connection = new SqlConnection(masterConnectionString);
            await connection.OpenAsync(cancellationToken);

            await using (var checkCommand = new SqlCommand("SELECT DB_ID(@DatabaseName);", connection))
            {
                checkCommand.Parameters.AddWithValue("@DatabaseName", databaseName);
                object? existingId = await checkCommand.ExecuteScalarAsync(cancellationToken);
                if (existingId != null && existingId != DBNull.Value)
                {
                    throw new InvalidOperationException($"Database '{databaseName}' already exists. Select it as an existing database instead.");
                }
            }

            string escapedName = databaseName.Replace("]", "]]", StringComparison.Ordinal);
            await using var createCommand = new SqlCommand($"CREATE DATABASE [{escapedName}];", connection)
            {
                CommandTimeout = AppSettings.Current.Query.CommandTimeoutSeconds
            };
            await createCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        private static bool IsDependencyError(SqlException exception)
        {
            foreach (SqlError error in exception.Errors)
            {
                if (error.Number is 207 or 208 or 4121 or 4512 or 15151 or 2760 or 15135)
                {
                    return true;
                }

                if (error.Message.Contains("could not find object", StringComparison.OrdinalIgnoreCase) ||
                    error.Message.Contains("invalid object name", StringComparison.OrdinalIgnoreCase) ||
                    error.Message.Contains("invalid column name", StringComparison.OrdinalIgnoreCase) ||
                    error.Message.Contains("could not find the function", StringComparison.OrdinalIgnoreCase) ||
                    error.Message.Contains("type", StringComparison.OrdinalIgnoreCase) && error.Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static string FormatSqlException(SqlException exception, int batchStartLine)
        {
            var sb = new StringBuilder();
            foreach (SqlError error in exception.Errors)
            {
                int absoluteLine = Math.Max(1, batchStartLine + error.LineNumber - 1);
                sb.AppendLine($"Msg {error.Number}, Level {error.Class}, State {error.State}, Line {absoluteLine}");
                sb.AppendLine(error.Message);
            }
            return sb.Length > 0 ? sb.ToString().TrimEnd() : exception.Message;
        }
    }
}
