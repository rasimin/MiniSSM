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
        public const int MaxAutomaticDependencyRounds = 3;
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
            CancellationToken cancellationToken = default,
            bool autoRetryDependencies = false)
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

            int maxPasses = autoRetryDependencies ? MaxDependencyRetryPasses : 1;
            for (int pass = 1; pass <= maxPasses && pending.Count > 0; pass++)
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
                        if (IsDependencyError(ex) && autoRetryDependencies && pass < maxPasses)
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

                if (deferred.Count == pending.Count && pass == maxPasses)
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

        public async Task<SchemaImportRetryResult> RerunFailedAsync(
            SchemaImportPlan plan,
            IEnumerable<SchemaImportItemResult> failedResults,
            string connectionString,
            string databaseName,
            IProgress<SchemaImportProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (failedResults == null) throw new ArgumentNullException(nameof(failedResults));

            List<SchemaImportItemResult> currentResults = failedResults
                .Where(result => result.Status == SchemaImportStatus.Failed)
                .ToList();
            var aggregateResults = currentResults.ToDictionary(result => result.BatchIndex, CloneResult);
            var retryResult = new SchemaImportRetryResult();

            if (currentResults.Count == 0)
            {
                return retryResult;
            }

            // The first rerun is intentionally broad: retry every failed batch once,
            // regardless of whether its previous error looked dependency-related.
            SchemaImportPlan initialRetryPlan = BuildRetryPlan(plan, currentResults);
            List<SchemaImportItemResult> initialRoundResults = await ImportAsync(
                initialRetryPlan,
                connectionString,
                databaseName,
                false,
                progress,
                cancellationToken,
                autoRetryDependencies: false);
            AddRoundResult(
                retryResult,
                round: 1,
                initialFailedRerun: true,
                requested: currentResults.Count,
                roundResults: initialRoundResults);
            MergeRoundResults(aggregateResults, initialRoundResults);

            currentResults = initialRoundResults
                .Where(result => result.Status == SchemaImportStatus.Failed && IsDependencyErrorMessage(result.ErrorMessage))
                .ToList();

            int automaticRound = 0;
            while (currentResults.Count > 0 && automaticRound < MaxAutomaticDependencyRounds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                automaticRound++;
                SchemaImportPlan retryPlan = BuildRetryPlan(plan, currentResults);
                List<SchemaImportItemResult> roundResults = await ImportAsync(
                    retryPlan,
                    connectionString,
                    databaseName,
                    false,
                    progress,
                    cancellationToken,
                    autoRetryDependencies: false);

                int dependencyFailures = roundResults.Count(result =>
                    result.Status == SchemaImportStatus.Failed && IsDependencyErrorMessage(result.ErrorMessage));
                AddRoundResult(
                    retryResult,
                    round: automaticRound + 1,
                    initialFailedRerun: false,
                    requested: currentResults.Count,
                    roundResults: roundResults);
                MergeRoundResults(aggregateResults, roundResults);

                if (dependencyFailures == 0 || dependencyFailures >= currentResults.Count)
                {
                    retryResult.StoppedWithoutProgress = dependencyFailures >= currentResults.Count && dependencyFailures > 0;
                    break;
                }

                currentResults = roundResults
                    .Where(result => result.Status == SchemaImportStatus.Failed && IsDependencyErrorMessage(result.ErrorMessage))
                    .ToList();
            }

            retryResult.Results.AddRange(aggregateResults.Values.OrderBy(result => result.BatchIndex));
            retryResult.ReachedRoundLimit = automaticRound >= MaxAutomaticDependencyRounds &&
                                            retryResult.Rounds.LastOrDefault()?.DependencyFailures > 0;
            return retryResult;
        }

        private static void AddRoundResult(
            SchemaImportRetryResult retryResult,
            int round,
            bool initialFailedRerun,
            int requested,
            IReadOnlyCollection<SchemaImportItemResult> roundResults)
        {
            retryResult.Rounds.Add(new SchemaImportRetryRound
            {
                Round = round,
                IsInitialFailedRerun = initialFailedRerun,
                Requested = requested,
                Success = roundResults.Count(result => result.Status is SchemaImportStatus.Success or SchemaImportStatus.Retried),
                Failed = roundResults.Count(result => result.Status == SchemaImportStatus.Failed),
                DependencyFailures = roundResults.Count(result => result.Status == SchemaImportStatus.Failed && IsDependencyErrorMessage(result.ErrorMessage))
            });
        }

        private static void MergeRoundResults(
            IDictionary<int, SchemaImportItemResult> aggregateResults,
            IEnumerable<SchemaImportItemResult> roundResults)
        {
            foreach (SchemaImportItemResult roundResult in roundResults)
            {
                if (!aggregateResults.TryGetValue(roundResult.BatchIndex, out SchemaImportItemResult? aggregate))
                {
                    aggregate = CloneResult(roundResult);
                    aggregateResults[roundResult.BatchIndex] = aggregate;
                }
                else
                {
                    aggregate.Attempts += roundResult.Attempts;
                    aggregate.Status = roundResult.Status;
                    aggregate.ErrorMessage = roundResult.ErrorMessage;
                }
            }
        }

        public static bool IsDependencyErrorMessage(string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            int[] dependencyErrorNumbers = { 207, 208, 4121, 4512, 15151, 2760, 15135 };
            if (dependencyErrorNumbers.Any(number => Regex.IsMatch(message, $@"\b(?:Msg\s+)?{number}\b", RegexOptions.IgnoreCase)))
            {
                return true;
            }

            return message.Contains("could not find object", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("invalid object name", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("invalid column name", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("could not find the function", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("type", StringComparison.OrdinalIgnoreCase) &&
                   message.Contains("does not exist", StringComparison.OrdinalIgnoreCase);
        }

        private static SchemaImportPlan BuildRetryPlan(
            SchemaImportPlan plan,
            IEnumerable<SchemaImportItemResult> results)
        {
            var failedIndexes = results.Select(result => result.BatchIndex).ToHashSet();
            var retryPlan = new SchemaImportPlan
            {
                FilePath = plan.FilePath,
                FileLength = plan.FileLength,
                ScriptDatabaseName = plan.ScriptDatabaseName
            };
            retryPlan.Batches.AddRange(plan.Batches.Where(batch => failedIndexes.Contains(batch.Index)));
            return retryPlan;
        }

        private static SchemaImportItemResult CloneResult(SchemaImportItemResult result)
        {
            return new SchemaImportItemResult
            {
                BatchIndex = result.BatchIndex,
                ObjectName = result.ObjectName,
                ObjectType = result.ObjectType,
                SourceLine = result.SourceLine,
                Phase = result.Phase,
                Status = result.Status,
                Attempts = result.Attempts,
                ErrorMessage = result.ErrorMessage,
                DependencyText = result.DependencyText,
                SqlText = result.SqlText
            };
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
            sb.AppendLine("Summary:");
            sb.AppendLine($"Planned: {rows.Count}");
            sb.AppendLine($"Success: {rows.Count(row => row.Status is SchemaImportStatus.Success or SchemaImportStatus.Retried)}");
            sb.AppendLine($"Skipped: {rows.Count(row => row.Status == SchemaImportStatus.Skipped)}");
            sb.AppendLine($"Failed: {rows.Count(row => row.Status == SchemaImportStatus.Failed)}");

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
                if (IsDependencyErrorMessage($"Msg {error.Number}: {error.Message}"))
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
