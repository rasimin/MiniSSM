using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SSMS
{
    public static class SchemaScriptParser
    {
        private static readonly Regex ObjectDeclarationPattern = new(
            @"\b(?<verb>CREATE\s+OR\s+ALTER|CREATE|ALTER|DROP)\s+(?<kind>SCHEMA|TABLE|VIEW|PROCEDURE|PROC|FUNCTION|TRIGGER|TYPE|SEQUENCE)\s+(?:(?:IF\s+NOT\s+EXISTS)\s+)?(?<name>(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_][\w$#@]*)(?:\s*\.\s*(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_][\w$#@]*)){0,2})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex DatabaseDeclarationPattern = new(
            @"\bCREATE\s+DATABASE\s+(?:(?:IF\s+NOT\s+EXISTS)\s+)?(?<name>(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_][\w$#@]*))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex IndexDeclarationPattern = new(
            @"\bCREATE\s+(?:(?:UNIQUE|CLUSTERED|NONCLUSTERED|XML|SPATIAL)\s+)*INDEX\s+(?<name>(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_][\w$#@]*))\s+ON\s+(?<target>(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_][\w$#@]*)(?:\s*\.\s*(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_][\w$#@]*)){0,2})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex DataPattern = new(
            @"\b(?:INSERT\s+INTO|MERGE\s+INTO|UPDATE|DELETE\s+FROM)\s+(?<name>(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_][\w$#@]*)(?:\s*\.\s*(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_][\w$#@]*)){0,2})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ReferencePattern = new(
            @"\b(?:FROM|JOIN|UPDATE|INTO|REFERENCES|EXEC(?:UTE)?|DELETE\s+FROM|MERGE\s+INTO)\s+(?<name>(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_][\w$#@]*)(?:\s*\.\s*(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_][\w$#@]*)){0,2})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly HashSet<string> IgnoredReferenceNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "SELECT", "VALUES", "OPENQUERY", "OPENROWSET", "OPENJSON", "STRING_SPLIT",
            "UNNEST", "TABLE", "VIEW", "FUNCTION", "PROCEDURE", "PROC", "NULL"
        };

        public static async Task<SchemaImportPlan> ParseFileAsync(
            string filePath,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("SQL file path is required.", nameof(filePath));
            }

            string sql = await File.ReadAllTextAsync(filePath, Encoding.UTF8, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var plan = new SchemaImportPlan
            {
                FilePath = filePath,
                FileLength = new FileInfo(filePath).Length
            };

            List<SqlBatch> batches = SqlBatchSplitter.Split(sql);
            for (int i = 0; i < batches.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SqlBatch batch = batches[i];
                string masked = MaskCommentsAndStrings(batch.Text);
                SchemaImportObjectType objectType = DetectObjectType(masked);
                string objectName = DetectObjectName(masked, objectType);

                Match databaseMatch = DatabaseDeclarationPattern.Match(masked);
                if (databaseMatch.Success)
                {
                    objectType = SchemaImportObjectType.Database;
                    objectName = NormalizeDatabaseName(databaseMatch.Groups["name"].Value);
                    if (string.IsNullOrWhiteSpace(plan.ScriptDatabaseName))
                    {
                        plan.ScriptDatabaseName = objectName;
                    }
                }

                if (objectType == SchemaImportObjectType.Unknown && DataPattern.IsMatch(masked))
                {
                    objectType = SchemaImportObjectType.Data;
                    objectName = NormalizeObjectName(DataPattern.Match(masked).Groups["name"].Value);
                }

                var item = new SchemaImportBatchInfo
                {
                    Index = i,
                    Text = batch.Text,
                    RepeatCount = batch.RepeatCount,
                    StartLineNumber = batch.StartLineNumber,
                    ObjectType = objectType,
                    ObjectName = objectName,
                    Phase = GetPhase(objectType)
                };

                foreach (Match referenceMatch in ReferencePattern.Matches(masked))
                {
                    string reference = NormalizeObjectName(referenceMatch.Groups["name"].Value);
                    if (!string.IsNullOrWhiteSpace(reference) &&
                        !IgnoredReferenceNames.Contains(reference) &&
                        !string.Equals(reference, objectName, StringComparison.OrdinalIgnoreCase) &&
                        !item.Dependencies.Contains(reference, StringComparer.OrdinalIgnoreCase))
                    {
                        item.Dependencies.Add(reference);
                    }
                }

                if (objectType == SchemaImportObjectType.Index)
                {
                    Match indexMatch = IndexDeclarationPattern.Match(masked);
                    if (indexMatch.Success)
                    {
                        string target = NormalizeObjectName(indexMatch.Groups["target"].Value);
                        if (!string.IsNullOrWhiteSpace(target) &&
                            !item.Dependencies.Contains(target, StringComparer.OrdinalIgnoreCase))
                        {
                            item.Dependencies.Add(target);
                        }
                    }
                }

                if (Regex.IsMatch(masked, @"\bUSE\s+", RegexOptions.IgnoreCase))
                {
                    item.Warnings.Add("USE statement will be skipped; the selected target database is authoritative.");
                }

                plan.Batches.Add(item);
            }

            ResolveDependenciesAndOrder(plan);
            return plan;
        }

        private static SchemaImportObjectType DetectObjectType(string masked)
        {
            Match indexMatch = IndexDeclarationPattern.Match(masked);
            if (indexMatch.Success)
            {
                return SchemaImportObjectType.Index;
            }

            Match declaration = ObjectDeclarationPattern.Match(masked);
            if (!declaration.Success)
            {
                if (Regex.IsMatch(masked, @"\bALTER\s+TABLE\b.*\bFOREIGN\s+KEY\b", RegexOptions.IgnoreCase | RegexOptions.Singleline))
                {
                    return SchemaImportObjectType.ForeignKey;
                }

                if (Regex.IsMatch(masked, @"\bALTER\s+TABLE\b.*\b(?:CONSTRAINT|PRIMARY\s+KEY|UNIQUE|CHECK)\b", RegexOptions.IgnoreCase | RegexOptions.Singleline))
                {
                    return SchemaImportObjectType.Constraint;
                }

                if (Regex.IsMatch(masked, @"\b(?:GRANT|DENY|REVOKE|EXEC\s+sys\.sp_addextendedproperty)\b", RegexOptions.IgnoreCase))
                {
                    return SchemaImportObjectType.Permission;
                }

                return SchemaImportObjectType.Unknown;
            }

            string kind = declaration.Groups["kind"].Value.ToUpperInvariant();
            if (kind == "SCHEMA") return SchemaImportObjectType.Schema;
            if (kind == "TYPE") return SchemaImportObjectType.Type;
            if (kind == "SEQUENCE") return SchemaImportObjectType.Sequence;
            if (kind == "TABLE")
            {
                string verb = declaration.Groups["verb"].Value;
                if (verb.StartsWith("CREATE", StringComparison.OrdinalIgnoreCase))
                {
                    return SchemaImportObjectType.Table;
                }

                return Regex.IsMatch(masked, @"\bFOREIGN\s+KEY\b", RegexOptions.IgnoreCase)
                    ? SchemaImportObjectType.ForeignKey
                    : SchemaImportObjectType.Constraint;
            }
            if (kind == "VIEW") return SchemaImportObjectType.View;
            if (kind == "FUNCTION") return SchemaImportObjectType.Function;
            if (kind is "PROCEDURE" or "PROC") return SchemaImportObjectType.Procedure;
            if (kind == "TRIGGER") return SchemaImportObjectType.Trigger;
            return SchemaImportObjectType.Unknown;
        }

        private static string DetectObjectName(string masked, SchemaImportObjectType objectType)
        {
            if (objectType == SchemaImportObjectType.Index)
            {
                Match indexMatch = IndexDeclarationPattern.Match(masked);
                return indexMatch.Success
                    ? NormalizeObjectName(indexMatch.Groups["name"].Value)
                    : string.Empty;
            }

            if (objectType == SchemaImportObjectType.Database)
            {
                Match databaseMatch = DatabaseDeclarationPattern.Match(masked);
                return databaseMatch.Success
                    ? NormalizeDatabaseName(databaseMatch.Groups["name"].Value)
                    : string.Empty;
            }

            Match declaration = ObjectDeclarationPattern.Match(masked);
            if (declaration.Success)
            {
                string name = NormalizeObjectName(declaration.Groups["name"].Value);
                if (objectType is SchemaImportObjectType.Constraint or SchemaImportObjectType.ForeignKey)
                {
                    Match constraintMatch = Regex.Match(
                        masked,
                        @"\bCONSTRAINT\s+(?<name>(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_][\w$#@]*))",
                        RegexOptions.IgnoreCase);
                    if (constraintMatch.Success)
                    {
                        name = NormalizeObjectName(constraintMatch.Groups["name"].Value);
                    }
                }
                return name;
            }

            Match tableAlterMatch = Regex.Match(
                masked,
                @"\bALTER\s+TABLE\s+(?<name>(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_][\w$#@]*)(?:\s*\.\s*(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_][\w$#@]*)){0,2})",
                RegexOptions.IgnoreCase);
            return tableAlterMatch.Success
                ? NormalizeObjectName(tableAlterMatch.Groups["name"].Value)
                : string.Empty;
        }

        private static void ResolveDependenciesAndOrder(SchemaImportPlan plan)
        {
            var producers = new Dictionary<string, SchemaImportBatchInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (SchemaImportBatchInfo batch in plan.Batches)
            {
                if (batch.IsObject && !string.IsNullOrWhiteSpace(batch.ObjectName) && !producers.ContainsKey(batch.ObjectName))
                {
                    producers[batch.ObjectName] = batch;
                }
            }

            var dependencyMap = new Dictionary<SchemaImportBatchInfo, List<SchemaImportBatchInfo>>();
            foreach (SchemaImportBatchInfo batch in plan.Batches)
            {
                var dependencies = new List<SchemaImportBatchInfo>();
                foreach (string dependencyName in batch.Dependencies)
                {
                    if (producers.TryGetValue(dependencyName, out SchemaImportBatchInfo? producer) &&
                        !ReferenceEquals(producer, batch) &&
                        !dependencies.Contains(producer))
                    {
                        dependencies.Add(producer);
                    }
                }
                dependencyMap[batch] = dependencies;
            }

            var indegree = plan.Batches.ToDictionary(batch => batch, _ => 0);
            var dependents = plan.Batches.ToDictionary(batch => batch, _ => new List<SchemaImportBatchInfo>());
            foreach ((SchemaImportBatchInfo batch, List<SchemaImportBatchInfo> dependencies) in dependencyMap)
            {
                foreach (SchemaImportBatchInfo dependency in dependencies)
                {
                    indegree[batch]++;
                    dependents[dependency].Add(batch);
                }
            }

            var remaining = new HashSet<SchemaImportBatchInfo>(plan.Batches);
            var ordered = new List<SchemaImportBatchInfo>(plan.Batches.Count);
            while (remaining.Count > 0)
            {
                List<SchemaImportBatchInfo> available = remaining
                    .Where(batch => indegree[batch] == 0)
                    .OrderBy(batch => batch.Phase)
                    .ThenBy(batch => batch.Index)
                    .ToList();

                if (available.Count == 0)
                {
                    SchemaImportBatchInfo cycleItem = remaining
                        .OrderBy(batch => batch.Phase)
                        .ThenBy(batch => batch.Index)
                        .First();
                    cycleItem.Warnings.Add("Circular or unresolved dependency detected; execution will continue with the planned order.");
                    plan.Warnings.Add($"Circular or unresolved dependency near {cycleItem.DisplayName} (line {cycleItem.StartLineNumber}).");
                    available.Add(cycleItem);
                }

                foreach (SchemaImportBatchInfo batch in available)
                {
                    if (!remaining.Remove(batch))
                    {
                        continue;
                    }

                    ordered.Add(batch);
                    foreach (SchemaImportBatchInfo dependent in dependents[batch])
                    {
                        indegree[dependent] = Math.Max(0, indegree[dependent] - 1);
                    }
                }
            }

            plan.Batches.Clear();
            plan.Batches.AddRange(ordered);
        }

        private static int GetPhase(SchemaImportObjectType objectType)
        {
            return objectType switch
            {
                SchemaImportObjectType.Unknown => 0,
                SchemaImportObjectType.Database => 5,
                SchemaImportObjectType.Schema => 10,
                SchemaImportObjectType.Type or SchemaImportObjectType.Sequence => 20,
                SchemaImportObjectType.Table => 30,
                SchemaImportObjectType.Constraint => 40,
                SchemaImportObjectType.Index => 50,
                SchemaImportObjectType.ForeignKey => 60,
                SchemaImportObjectType.View or SchemaImportObjectType.Function => 70,
                SchemaImportObjectType.Procedure => 80,
                SchemaImportObjectType.Trigger => 90,
                SchemaImportObjectType.Permission => 100,
                SchemaImportObjectType.Data => 110,
                _ => 120
            };
        }

        private static string NormalizeObjectName(string value)
        {
            string[] parts = value
                .Split('.', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim().Trim('[', ']', '"'))
                .Where(part => part.Length > 0)
                .ToArray();

            if (parts.Length == 0) return string.Empty;
            if (parts.Length == 1) return $"dbo.{parts[0]}";
            if (parts.Length == 2) return $"{parts[0]}.{parts[1]}";
            return $"{parts[^2]}.{parts[^1]}";
        }

        private static string NormalizeDatabaseName(string value)
        {
            return value.Trim().Trim('[', ']', '"');
        }

        private static string MaskCommentsAndStrings(string sql)
        {
            var output = new StringBuilder(sql.Length);
            bool inBlockComment = false;
            bool inString = false;

            for (int i = 0; i < sql.Length; i++)
            {
                char current = sql[i];
                char next = i + 1 < sql.Length ? sql[i + 1] : '\0';

                if (inBlockComment)
                {
                    if (current == '*' && next == '/')
                    {
                        output.Append("  ");
                        i++;
                        inBlockComment = false;
                    }
                    else
                    {
                        output.Append(current is '\r' or '\n' ? current : ' ');
                    }
                    continue;
                }

                if (inString)
                {
                    if (current == '\'' && next == '\'')
                    {
                        output.Append("  ");
                        i++;
                    }
                    else
                    {
                        output.Append(current is '\r' or '\n' ? current : ' ');
                        if (current == '\'') inString = false;
                    }
                    continue;
                }

                if (current == '-' && next == '-')
                {
                    output.Append("  ");
                    i++;
                    while (i + 1 < sql.Length && sql[i + 1] is not '\r' and not '\n')
                    {
                        output.Append(' ');
                        i++;
                    }
                    continue;
                }

                if (current == '/' && next == '*')
                {
                    output.Append("  ");
                    i++;
                    inBlockComment = true;
                    continue;
                }

                if (current == '\'')
                {
                    output.Append(' ');
                    inString = true;
                    continue;
                }

                output.Append(current);
            }

            return output.ToString();
        }
    }
}
