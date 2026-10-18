using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace MilestoneAdmin.Domain.Csv
{
    public sealed class CsvTable
    {
        public List<string> Headers { get; } = new List<string>();

        /// <summary>Data rows; each row has one value per header (missing trailing cells are empty strings).</summary>
        public List<string[]> Rows { get; } = new List<string[]>();

        /// <summary>1-based physical line number of each data row (for error messages).</summary>
        public List<int> RowLineNumbers { get; } = new List<int>();

        public int IndexOf(string header) => Headers.FindIndex(h => string.Equals(h, header, StringComparison.OrdinalIgnoreCase));
    }

    public sealed class CsvFormatException : FormatException
    {
        public CsvFormatException(string message, int line)
            : base($"{message} (line {line})")
        {
            Line = line;
        }

        public int Line { get; }
    }

    /// <summary>RFC 4180 CSV reader/writer (quoted fields, embedded delimiters, quotes and line breaks).</summary>
    public static class CsvParser
    {
        public static CsvTable Parse(string text, char delimiter = ',')
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            if (text.Length > 0 && text[0] == (char)0xFEFF) text = text.Substring(1);
            var records = ReadRecords(text, delimiter);
            var table = new CsvTable();
            if (records.Count == 0) return table;
            table.Headers.AddRange(records[0].Fields.Select(h => h.Trim()));
            var dup = table.Headers.GroupBy(h => h, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Key.Length > 0 && g.Count() > 1);
            if (dup != null) throw new CsvFormatException($"Duplicate column '{dup.Key}'", records[0].Line);
            for (var i = 1; i < records.Count; i++)
            {
                var r = records[i];
                if (r.Fields.Count == 1 && r.Fields[0].Length == 0) continue; // blank line
                if (r.Fields.Count > table.Headers.Count)
                    throw new CsvFormatException($"Row has {r.Fields.Count} values but the header has {table.Headers.Count} columns", r.Line);
                var row = new string[table.Headers.Count];
                for (var c = 0; c < row.Length; c++) row[c] = c < r.Fields.Count ? r.Fields[c] : string.Empty;
                table.Rows.Add(row);
                table.RowLineNumbers.Add(r.Line);
            }

            return table;
        }

        public static char DetectDelimiter(string firstLine)
        {
            var candidates = new[] { ',', ';', '\t' };
            return candidates.OrderByDescending(c => firstLine?.Count(x => x == c) ?? 0).First();
        }

        private sealed class Record
        {
            public int Line;
            public List<string> Fields = new List<string>();
        }

        private static List<Record> ReadRecords(string text, char delimiter)
        {
            var records = new List<Record>();
            var line = 1;
            var i = 0;
            var current = new Record { Line = line };
            var field = new StringBuilder();
            var inQuotes = false;
            var fieldStarted = false;
            while (i < text.Length)
            {
                var c = text[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            field.Append('"');
                            i += 2;
                            continue;
                        }

                        inQuotes = false;
                        i++;
                        continue;
                    }

                    if (c == '\n') line++;
                    field.Append(c);
                    i++;
                    continue;
                }

                if (c == '"' && !fieldStarted)
                {
                    inQuotes = true;
                    fieldStarted = true;
                    i++;
                    continue;
                }

                if (c == delimiter)
                {
                    current.Fields.Add(field.ToString());
                    field.Clear();
                    fieldStarted = false;
                    i++;
                    continue;
                }

                if (c == '\r' || c == '\n')
                {
                    current.Fields.Add(field.ToString());
                    field.Clear();
                    fieldStarted = false;
                    records.Add(current);
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    i++;
                    line++;
                    current = new Record { Line = line };
                    continue;
                }

                field.Append(c);
                fieldStarted = true;
                i++;
            }

            if (inQuotes) throw new CsvFormatException("Unterminated quoted field", current.Line);
            if (field.Length > 0 || current.Fields.Count > 0)
            {
                current.Fields.Add(field.ToString());
                records.Add(current);
            }

            return records;
        }

        public static string Write(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows, char delimiter = ',')
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(delimiter.ToString(), headers.Select(h => Escape(h, delimiter))));
            foreach (var r in rows) sb.AppendLine(string.Join(delimiter.ToString(), r.Select(v => Escape(v, delimiter))));
            return sb.ToString();
        }

        /// <summary>
        /// Escapes a value. Values starting with = + - @ (or tab / CR) are prefixed with an apostrophe so spreadsheet
        /// applications do not evaluate them as formulas (CSV injection).
        /// </summary>
        public static string Escape(string value, char delimiter = ',')
        {
            value ??= string.Empty;
            if (value.Length > 0 && "=+-@\t\r".IndexOf(value[0]) >= 0 && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                value = "'" + value;
            if (value.IndexOf(delimiter) >= 0 || value.IndexOf('"') >= 0 || value.IndexOf('\n') >= 0 || value.IndexOf('\r') >= 0)
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }

        public static CsvTable ParseFile(string path)
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            var firstLine = text.Split('\n').FirstOrDefault() ?? string.Empty;
            return Parse(text, DetectDelimiter(firstLine));
        }
    }
}
