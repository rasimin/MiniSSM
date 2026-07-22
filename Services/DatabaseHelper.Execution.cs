using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace SSMS
{
    public static partial class DatabaseHelper
    {
        public static async Task<QueryResult> ExecuteQueryAsync(
            string connectionString,
            string databaseName,
            string sqlQuery,
            IProgress<string>? messageProgress = null,
            CancellationToken cancellationToken = default,
            QueryExecutionMode mode = QueryExecutionMode.Execute)
        {
            var result = new QueryResult();
            var dbConnString = BuildConnectionString(connectionString, databaseName);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                using (var connection = new SqlConnection(dbConnString))
                {
                    await connection.OpenAsync(cancellationToken);

                    var messages = new System.Text.StringBuilder();
                    connection.InfoMessage += (sender, e) =>
                    {
                        messages.AppendLine(e.Message);
                        messageProgress?.Report(e.Message);
                    };

                    List<SqlBatch> batches = SqlBatchSplitter.Split(sqlQuery);
                    if (batches.Count == 0)
                    {
                        throw new InvalidOperationException("No executable SQL batch was found.");
                    }

                    string? sessionOption = mode switch
                    {
                        QueryExecutionMode.Parse => "SET PARSEONLY ON",
                        QueryExecutionMode.EstimatedPlan => "SET SHOWPLAN_XML ON",
                        QueryExecutionMode.ActualPlan => "SET STATISTICS XML ON",
                        _ => null
                    };
                    string? resetOption = mode switch
                    {
                        QueryExecutionMode.Parse => "SET PARSEONLY OFF",
                        QueryExecutionMode.EstimatedPlan => "SET SHOWPLAN_XML OFF",
                        QueryExecutionMode.ActualPlan => "SET STATISTICS XML OFF",
                        _ => null
                    };

                    try
                    {
                        if (sessionOption != null)
                        {
                            await ExecuteSessionCommandAsync(connection, sessionOption, cancellationToken);
                        }

                        int batchNumber = 0;
                        foreach (SqlBatch batch in batches)
                        {
                            batchNumber++;
                            for (int repeat = 0; repeat < batch.RepeatCount; repeat++)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                await ExecuteBatchAsync(
                                    connection,
                                    batch.Text,
                                    result,
                                    messages,
                                    messageProgress,
                                    cancellationToken,
                                    includeDataResults: mode is QueryExecutionMode.Execute or QueryExecutionMode.ActualPlan);

                                if (batch.RepeatCount > 1)
                                {
                                    string repeatMessage = $"Batch {batchNumber} completed ({repeat + 1}/{batch.RepeatCount}).";
                                    messages.AppendLine(repeatMessage);
                                    messageProgress?.Report(repeatMessage);
                                }
                            }
                        }
                    }
                    finally
                    {
                        if (resetOption != null && connection.State == ConnectionState.Open)
                        {
                            try
                            {
                                await ExecuteSessionCommandAsync(connection, resetOption, CancellationToken.None);
                            }
                            catch (Exception resetException)
                            {
                                AppLogger.Error(resetException, $"Failed to reset SQL session option: {resetOption}");
                            }
                        }
                    }

                    stopwatch.Stop();
                    result.IsSuccess = true;
                    result.EffectiveDatabaseName = connection.Database;
                    result.ExecutionTime = stopwatch.Elapsed;
                    result.Message = messages.Length > 0
                        ? messages.ToString()
                        : mode switch
                        {
                            QueryExecutionMode.Parse => "Syntax check completed successfully.",
                            QueryExecutionMode.EstimatedPlan => "Estimated execution plan generated successfully.",
                            QueryExecutionMode.ActualPlan => "Query completed and actual execution plan generated successfully.",
                            _ => "Command completed successfully."
                        };
                }
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                result.IsCancelled = true;
                result.IsSuccess = false;
                result.ExecutionTime = stopwatch.Elapsed;
                result.Message = "Query cancelled by user.";
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                result.IsSuccess = false;
                result.ExecutionTime = stopwatch.Elapsed;
                result.Message = $"Msg {ex.HResult}, Level, State\n{ex.Message}";
            }

            return result;
        }

        private static async Task ExecuteSessionCommandAsync(
            SqlConnection connection,
            string commandText,
            CancellationToken cancellationToken)
        {
            using var command = new SqlCommand(commandText, connection)
            {
                CommandTimeout = AppSettings.Current.Query.CommandTimeoutSeconds
            };
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static async Task ExecuteBatchAsync(
            SqlConnection connection,
            string sql,
            QueryResult result,
            System.Text.StringBuilder messages,
            IProgress<string>? messageProgress,
            CancellationToken cancellationToken,
            bool includeDataResults)
        {
            using var command = new SqlCommand(sql, connection)
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
                    // Command may already be completed or disposed.
                }
            });

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            do
            {
                if (reader.FieldCount > 0)
                {
                    if (IsExecutionPlanResult(reader))
                    {
                        while (await reader.ReadAsync(cancellationToken))
                        {
                            if (!reader.IsDBNull(0))
                            {
                                result.ExecutionPlans.Add(reader.GetString(0));
                            }
                        }
                    }
                    else if (includeDataResults)
                    {
                        var dataTable = new DataTable();
                        var usedColumnNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            string baseName = reader.GetName(i);
                            if (string.IsNullOrWhiteSpace(baseName))
                            {
                                baseName = $"Column{i + 1}";
                            }

                            string uniqueName = baseName;
                            int duplicateNumber = 2;
                            while (!usedColumnNames.Add(uniqueName))
                            {
                                uniqueName = $"{baseName} ({duplicateNumber++})";
                            }
                            dataTable.Columns.Add(uniqueName, reader.GetFieldType(i));
                        }

                        while (await reader.ReadAsync(cancellationToken))
                        {
                            DataRow row = dataTable.NewRow();
                            for (int i = 0; i < reader.FieldCount; i++)
                            {
                                row[i] = reader.GetValue(i);
                            }
                            dataTable.Rows.Add(row);
                        }
                        result.DataTables.Add(dataTable);
                    }
                    else
                    {
                        while (await reader.ReadAsync(cancellationToken))
                        {
                            // Consume parse/showplan rows that are not surfaced as grid data.
                        }
                    }
                }
                else if (reader.RecordsAffected >= 0)
                {
                    int rowsAffected = reader.RecordsAffected;
                    result.RowsAffected = (result.RowsAffected ?? 0) + rowsAffected;
                    string affectedMessage = $"({rowsAffected} row(s) affected)";
                    messages.AppendLine(affectedMessage);
                    messageProgress?.Report(affectedMessage);
                }
            } while (await reader.NextResultAsync(cancellationToken));
        }

        private static bool IsExecutionPlanResult(SqlDataReader reader)
        {
            return reader.FieldCount == 1 &&
                   reader.GetName(0).Contains("Showplan", StringComparison.OrdinalIgnoreCase);
        }

    }
}