using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Data.SqlClient;

namespace SSMS
{
    public partial class MainWindow : Window
    {
        private ContextMenu CreateObjectContextMenu(string connectionString, string databaseName, string objectType, string objectName)
        {
            var menu = new ContextMenu();

            var newQueryItem = new MenuItem { Header = "New Query" };
            newQueryItem.Click += (s, e) => CreateNewQueryTab(connectionString, databaseName);
            menu.Items.Add(newQueryItem);

            if (objectType == "Table" || objectType == "View")
            {
                var selectTopItem = new MenuItem { Header = "Select Top 200" };
                selectTopItem.Click += (s, e) =>
                {
                    string safeName = objectName;
                    var parts = objectName.Split('.');
                    if (parts.Length == 2)
                    {
                        safeName = $"[{parts[0]}].[{parts[1]}]";
                    }
                    else
                    {
                        safeName = $"[{objectName}]";
                    }
                    string sql = $"SELECT TOP 200 * FROM {safeName};";
                    CreateNewQueryTab(connectionString, databaseName, sql, $"{objectName} (Top 200)", autoExecute: true);
                };
                menu.Items.Add(selectTopItem);
            }

            var scriptAsMenu = new MenuItem { Header = "Script Object as" };

            var createToMenu = new MenuItem { Header = "CREATE To" };
            var createNewQuery = new MenuItem { Header = "New Query Editor Window" };
            createNewQuery.Click += async (s, e) => await GenerateScriptObjectAsync(connectionString, databaseName, objectType, objectName, "CREATE");
            createToMenu.Items.Add(createNewQuery);
            scriptAsMenu.Items.Add(createToMenu);

            if (objectType == "Table")
            {
                var insertToMenu = new MenuItem { Header = "INSERT To" };
                var insertNewQuery = new MenuItem { Header = "New Query Editor Window (Standard)" };
                insertNewQuery.Click += async (s, e) => await GenerateScriptObjectAsync(connectionString, databaseName, objectType, objectName, "INSERT");
                insertToMenu.Items.Add(insertNewQuery);

                var insertVarsNewQuery = new MenuItem { Header = "New Query Editor Window (with Variables)" };
                insertVarsNewQuery.Click += async (s, e) => await GenerateScriptObjectAsync(connectionString, databaseName, objectType, objectName, "INSERT_VARS");
                insertToMenu.Items.Add(insertVarsNewQuery);

                var insertDataNewQuery = new MenuItem { Header = "New Query Editor Window (with Data)" };
                insertDataNewQuery.Click += (s, e) => ShowInsertWithDataDialog(connectionString, databaseName, objectName);
                insertToMenu.Items.Add(insertDataNewQuery);

                scriptAsMenu.Items.Add(insertToMenu);

                var updateToMenu = new MenuItem { Header = "UPDATE To" };
                var updateNewQuery = new MenuItem { Header = "New Query Editor Window" };
                updateNewQuery.Click += async (s, e) => await GenerateScriptObjectAsync(connectionString, databaseName, objectType, objectName, "UPDATE");
                updateToMenu.Items.Add(updateNewQuery);
                scriptAsMenu.Items.Add(updateToMenu);

                var deleteToMenu = new MenuItem { Header = "DELETE To" };
                var deleteNewQuery = new MenuItem { Header = "New Query Editor Window" };
                deleteNewQuery.Click += async (s, e) => await GenerateScriptObjectAsync(connectionString, databaseName, objectType, objectName, "DELETE");
                deleteToMenu.Items.Add(deleteNewQuery);
                scriptAsMenu.Items.Add(deleteToMenu);
            }

            var alterToMenu = new MenuItem { Header = "ALTER To" };
            var alterNewQuery = new MenuItem { Header = "New Query Editor Window" };
            alterNewQuery.Click += async (s, e) => await GenerateScriptObjectAsync(connectionString, databaseName, objectType, objectName, "ALTER");
            alterToMenu.Items.Add(alterNewQuery);
            scriptAsMenu.Items.Add(alterToMenu);

            var dropToMenu = new MenuItem { Header = "DROP To" };
            var dropNewQuery = new MenuItem { Header = "New Query Editor Window" };
            dropNewQuery.Click += async (s, e) => await GenerateScriptObjectAsync(connectionString, databaseName, objectType, objectName, "DROP");
            dropToMenu.Items.Add(dropNewQuery);
            scriptAsMenu.Items.Add(dropToMenu);

            menu.Items.Add(scriptAsMenu);

            return menu;
        }

        public Task OpenObjectDefinitionFromEditorAsync(string connectionString, string databaseName, string objectType, string objectName)
        {
            return GenerateScriptObjectAsync(connectionString, databaseName, objectType, objectName, "CREATE");
        }

        private async Task GenerateScriptObjectAsync(string connectionString, string databaseName, string objectType, string objectName, string scriptType)
        {
            string sql = "";
            try
            {
                if (scriptType == "DROP")
                {
                    string dropKeyword = objectType;
                    if (objectType == "StoredProcedure") dropKeyword = "PROCEDURE";
                    sql = $"DROP {dropKeyword.ToUpper()} {objectName};";
                }
                else if (objectType == "Table")
                {
                    if (scriptType == "CREATE")
                    {
                        sql = await DatabaseHelper.GenerateTableCreateScriptAsync(connectionString, databaseName, objectName);
                    }
                    else if (scriptType == "INSERT" || scriptType == "INSERT_VARS")
                    {
                        var columns = await DatabaseHelper.GetColumnsAsync(connectionString, databaseName, objectName);
                        var sb = new System.Text.StringBuilder();
                        string safeName = objectName;
                        var parts = objectName.Split('.');
                        if (parts.Length == 2)
                        {
                            safeName = $"[{parts[0]}].[{parts[1]}]";
                        }
                        else
                        {
                            safeName = $"[{objectName}]";
                        }

                        if (scriptType == "INSERT_VARS")
                        {
                            foreach (var col in columns)
                            {
                                string varName = col.ColumnName.Replace(" ", "_");
                                varName = new string(varName.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
                                sb.AppendLine($"DECLARE @{varName} {col.DataType} = NULL;");
                            }
                            sb.AppendLine();
                        }

                        sb.AppendLine($"INSERT INTO {safeName}");
                        sb.AppendLine("(");
                        for (int i = 0; i < columns.Count; i++)
                        {
                            sb.Append($"    [{columns[i].ColumnName}]");
                            if (i < columns.Count - 1)
                            {
                                sb.AppendLine(",");
                            }
                            else
                            {
                                sb.AppendLine();
                            }
                        }
                        sb.AppendLine(")");
                        sb.AppendLine("VALUES");
                        sb.AppendLine("(");
                        for (int i = 0; i < columns.Count; i++)
                        {
                            if (scriptType == "INSERT_VARS")
                            {
                                string varName = columns[i].ColumnName.Replace(" ", "_");
                                varName = new string(varName.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
                                sb.Append($"    @{varName}");
                            }
                            else
                            {
                                sb.Append($"    NULL /* {columns[i].ColumnName} ({columns[i].DataType}) */");
                            }

                            if (i < columns.Count - 1)
                            {
                                sb.AppendLine(",");
                            }
                            else
                            {
                                sb.AppendLine();
                            }
                        }
                        sb.AppendLine(");");
                        sql = sb.ToString();
                    }
                    else if (scriptType == "UPDATE" || scriptType == "DELETE")
                    {
                        var columns = await DatabaseHelper.GetColumnsAsync(connectionString, databaseName, objectName);
                        var keyColumns = columns.Where(column => column.IsPrimaryKey).ToList();
                        if (keyColumns.Count == 0 && columns.Count > 0)
                        {
                            keyColumns.Add(columns[0]);
                        }

                        var sb = new System.Text.StringBuilder();
                        string safeName = QuoteMultipartIdentifier(objectName);
                        if (!columns.Any(column => column.IsPrimaryKey))
                        {
                            sb.AppendLine("-- WARNING: No primary key was found. Review the generated WHERE clause before execution.");
                        }
                        sb.AppendLine("SET XACT_ABORT ON;");
                        sb.AppendLine();

                        IEnumerable<(string ColumnName, string DataType, bool IsPrimaryKey)> variables =
                            scriptType == "UPDATE" ? columns : keyColumns;
                        foreach (var column in variables)
                        {
                            sb.AppendLine($"DECLARE @{SanitizeSqlVariableName(column.ColumnName)} {column.DataType} = NULL;");
                        }
                        sb.AppendLine();

                        if (scriptType == "UPDATE")
                        {
                            var updateColumns = columns.Where(column => !column.IsPrimaryKey).ToList();
                            sb.AppendLine($"UPDATE {safeName}");
                            sb.AppendLine("SET");
                            if (updateColumns.Count == 0 && keyColumns.Count > 0)
                            {
                                sb.AppendLine($"    {QuoteSqlIdentifier(keyColumns[0].ColumnName)} = {QuoteSqlIdentifier(keyColumns[0].ColumnName)}");
                            }
                            for (int i = 0; i < updateColumns.Count; i++)
                            {
                                string suffix = i < updateColumns.Count - 1 ? "," : string.Empty;
                                sb.AppendLine($"    {QuoteSqlIdentifier(updateColumns[i].ColumnName)} = @{SanitizeSqlVariableName(updateColumns[i].ColumnName)}{suffix}");
                            }
                        }
                        else
                        {
                            sb.AppendLine($"DELETE FROM {safeName}");
                        }

                        if (keyColumns.Count > 0)
                        {
                            sb.AppendLine("WHERE");
                            for (int i = 0; i < keyColumns.Count; i++)
                            {
                                string prefix = i == 0 ? "    " : "    AND ";
                                sb.AppendLine($"{prefix}{QuoteSqlIdentifier(keyColumns[i].ColumnName)} = @{SanitizeSqlVariableName(keyColumns[i].ColumnName)}");
                            }
                        }
                        sb.AppendLine(";");
                        sql = sb.ToString();
                    }
                    else // ALTER Table
                    {
                        sql = $"-- Alter Table Script for {objectName}\n-- ALTER TABLE {objectName} ADD [NewColumnName] DataType;";
                    }
                }
                else // View, StoredProcedure, Function
                {
                    string def = await DatabaseHelper.GetObjectDefinitionAsync(connectionString, databaseName, objectName);
                    if (string.IsNullOrEmpty(def))
                    {
                        sql = $"-- Could not retrieve definition for {objectName}.";
                    }
                    else
                    {
                        if (scriptType == "CREATE")
                        {
                            sql = def;
                        }
                        else // ALTER
                        {
                            int idx = def.IndexOf("CREATE", StringComparison.OrdinalIgnoreCase);
                            if (idx >= 0)
                            {
                                sql = def.Substring(0, idx) + "ALTER" + def.Substring(idx + 6);
                            }
                            else
                            {
                                sql = def;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                sql = $"-- Error generating script: {ex.Message}";
            }

            CreateNewQueryTab(connectionString, databaseName, sql, $"{objectName}_{scriptType}.sql");
        }

        private static string QuoteMultipartIdentifier(string objectName)
        {
            return string.Join(".", objectName.Split('.').Select(QuoteSqlIdentifier));
        }

        private static string QuoteSqlIdentifier(string identifier)
        {
            return "[" + identifier.Replace("]", "]]") + "]";
        }

        private static string SanitizeSqlVariableName(string columnName)
        {
            string name = new(columnName.Select(character => char.IsLetterOrDigit(character) || character == '_' ? character : '_').ToArray());
            if (string.IsNullOrWhiteSpace(name)) return "Value";
            return char.IsDigit(name[0]) ? $"Value_{name}" : name;
        }

        private void ShowInsertWithDataDialog(string connectionString, string databaseName, string tableName)
        {
            var dialog = new InsertWithDataWindow(connectionString, databaseName, tableName);
            dialog.Owner = this;
            if (dialog.ShowDialog() == true && dialog.IsGenerated)
            {
                CreateNewQueryTab(connectionString, databaseName, dialog.GeneratedSql, $"{tableName}_InsertData.sql");
            }
        }

    }
}