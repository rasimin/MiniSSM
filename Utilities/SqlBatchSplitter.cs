using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace SSMS
{
    public sealed record SqlBatch(string Text, int RepeatCount);

    public static class SqlBatchSplitter
    {
        private static readonly Regex GoLinePattern = new(
            @"^\s*GO(?:\s+(\d+))?\s*(?:--.*)?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static List<SqlBatch> Split(string sql)
        {
            var batches = new List<SqlBatch>();
            var current = new StringBuilder();
            bool inBlockComment = false;
            bool inString = false;

            using var reader = new System.IO.StringReader(sql ?? string.Empty);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (!inBlockComment && !inString)
                {
                    Match match = GoLinePattern.Match(line);
                    if (match.Success)
                    {
                        AddBatch(batches, current, ParseRepeatCount(match.Groups[1].Value));
                        continue;
                    }
                }

                current.AppendLine(line);
                UpdateLexicalState(line, ref inBlockComment, ref inString);
            }

            AddBatch(batches, current, 1);
            return batches;
        }

        private static int ParseRepeatCount(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return 1;
            }

            if (!int.TryParse(value, out int count) || count < 1 || count > 1000)
            {
                throw new InvalidOperationException("GO repeat count must be between 1 and 1000.");
            }

            return count;
        }

        private static void AddBatch(List<SqlBatch> batches, StringBuilder current, int repeatCount)
        {
            string text = current.ToString().Trim();
            current.Clear();
            if (text.Length > 0)
            {
                batches.Add(new SqlBatch(text, repeatCount));
            }
        }

        private static void UpdateLexicalState(string line, ref bool inBlockComment, ref bool inString)
        {
            for (int i = 0; i < line.Length; i++)
            {
                char current = line[i];
                char next = i + 1 < line.Length ? line[i + 1] : '\0';

                if (inBlockComment)
                {
                    if (current == '*' && next == '/')
                    {
                        inBlockComment = false;
                        i++;
                    }
                    continue;
                }

                if (inString)
                {
                    if (current == '\'' && next == '\'')
                    {
                        i++;
                    }
                    else if (current == '\'')
                    {
                        inString = false;
                    }
                    continue;
                }

                if (current == '-' && next == '-')
                {
                    break;
                }
                if (current == '/' && next == '*')
                {
                    inBlockComment = true;
                    i++;
                }
                else if (current == '\'')
                {
                    inString = true;
                }
            }
        }
    }
}
