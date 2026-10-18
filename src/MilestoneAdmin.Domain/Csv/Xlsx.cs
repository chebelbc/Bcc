using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Text;
using System.Xml;

namespace MilestoneAdmin.Domain.Csv
{
    public sealed class XlsxSheetInfo
    {
        public string Name { get; set; }
        public string PartName { get; set; }
    }

    public sealed class XlsxWorkbookInfo
    {
        public List<XlsxSheetInfo> Sheets { get; } = new List<XlsxSheetInfo>();

        public bool HasSheet(string name) => Sheets.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// A workbook produced by MilestonePSTools' Export-VmsHardware (.xlsx). Import-VmsHardwareExcel requires a
        /// "Hardware" worksheet; the exporter also writes "Cameras" (MilestonePSTools/Private/ImportVmsHardwareExcelFunctions.ps1).
        /// Such files are imported by MilestonePSTools itself, not by the application's table reader.
        /// </summary>
        public bool IsMilestonePSToolsExport => HasSheet("Hardware") && HasSheet("Cameras");
    }

    /// <summary>
    /// Minimal, dependency-free reader for Office Open XML spreadsheets (.xlsx). Reads cell values only (shared strings,
    /// inline strings, numbers, booleans, formula results); ignores formatting. Hardened against malformed and hostile
    /// files: DTDs are prohibited, no external resources are resolved, and part sizes, rows and columns are limited.
    /// </summary>
    public static class XlsxReader
    {
        public const long MaxPartBytes = 64L * 1024 * 1024;
        public const int MaxRows = 100000;
        public const int MaxColumns = 500;

        private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private const string PkgRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";

        public static XlsxWorkbookInfo ReadWorkbookInfo(string path)
        {
            using (var fs = File.OpenRead(path)) return ReadWorkbookInfo(fs);
        }

        public static XlsxWorkbookInfo ReadWorkbookInfo(Stream stream)
        {
            using (var zip = Open(stream)) return WorkbookInfo(zip);
        }

        /// <summary>Reads a worksheet (by name, or the first worksheet) into a table: the first non-empty row is the header.</summary>
        public static CsvTable ReadTable(string path, string sheetName = null)
        {
            using (var fs = File.OpenRead(path)) return ReadTable(fs, sheetName);
        }

        public static CsvTable ReadTable(Stream stream, string sheetName = null)
        {
            using (var zip = Open(stream))
            {
                var info = WorkbookInfo(zip);
                if (info.Sheets.Count == 0) throw new CsvFormatException("The workbook contains no worksheets", 0);
                var sheet = sheetName == null
                    ? info.Sheets[0]
                    : info.Sheets.FirstOrDefault(s => string.Equals(s.Name, sheetName, StringComparison.OrdinalIgnoreCase))
                      ?? throw new CsvFormatException($"Worksheet '{sheetName}' was not found", 0);
                var shared = ReadSharedStrings(zip);
                var entry = Entry(zip, sheet.PartName) ?? throw new CsvFormatException($"Worksheet '{sheet.Name}' is missing from the file", 0);
                return ToTable(ReadCells(entry, shared), sheet.Name);
            }
        }

        private static ZipArchive Open(Stream stream)
        {
            try
            {
                return new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            }
            catch (InvalidDataException ex)
            {
                throw new CsvFormatException("The file is not a valid .xlsx workbook (" + ex.Message + ")", 0);
            }
        }

        private static ZipArchiveEntry Entry(ZipArchive zip, string partName)
        {
            var name = partName.TrimStart('/');
            return zip.Entries.FirstOrDefault(e => string.Equals(e.FullName.Replace('\\', '/'), name, StringComparison.OrdinalIgnoreCase));
        }

        private static XmlReader Xml(ZipArchiveEntry entry)
        {
            if (entry.Length > MaxPartBytes) throw new CsvFormatException($"Part '{entry.FullName}' is too large", 0);
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                IgnoreWhitespace = false,
                CloseInput = true
            };
            return XmlReader.Create(new LimitedStream(entry.Open(), MaxPartBytes), settings);
        }

        private static XlsxWorkbookInfo WorkbookInfo(ZipArchive zip)
        {
            var info = new XlsxWorkbookInfo();
            var wb = Entry(zip, "xl/workbook.xml") ?? throw new CsvFormatException("The file is not an Excel workbook (xl/workbook.xml is missing)", 0);
            var rels = new Dictionary<string, string>(StringComparer.Ordinal);
            var relsEntry = Entry(zip, "xl/_rels/workbook.xml.rels");
            if (relsEntry != null)
            {
                using (var r = Xml(relsEntry))
                {
                    while (r.Read())
                    {
                        if (r.NodeType == XmlNodeType.Element && r.LocalName == "Relationship" && r.NamespaceURI == PkgRelNs)
                        {
                            var id = r.GetAttribute("Id");
                            var target = r.GetAttribute("Target");
                            if (id != null && target != null) rels[id] = target.StartsWith("/", StringComparison.Ordinal) ? target.TrimStart('/') : "xl/" + target;
                        }
                    }
                }
            }

            using (var r = Xml(wb))
            {
                var index = 0;
                while (r.Read())
                {
                    if (r.NodeType != XmlNodeType.Element || r.LocalName != "sheet" || r.NamespaceURI != MainNs) continue;
                    index++;
                    var name = r.GetAttribute("name") ?? "Sheet" + index.ToString(CultureInfo.InvariantCulture);
                    var rid = r.GetAttribute("id", RelNs);
                    var part = rid != null && rels.TryGetValue(rid, out var t) ? t : "xl/worksheets/sheet" + index.ToString(CultureInfo.InvariantCulture) + ".xml";
                    info.Sheets.Add(new XlsxSheetInfo { Name = name, PartName = NormalizePart(part) });
                }
            }

            return info;
        }

        private static string NormalizePart(string part)
        {
            var segments = new List<string>();
            foreach (var s in part.Replace('\\', '/').Split('/'))
            {
                if (s == "..") { if (segments.Count > 0) segments.RemoveAt(segments.Count - 1); }
                else if (s.Length > 0 && s != ".") segments.Add(s);
            }

            return string.Join("/", segments);
        }

        private static List<string> ReadSharedStrings(ZipArchive zip)
        {
            var list = new List<string>();
            var e = Entry(zip, "xl/sharedStrings.xml");
            if (e == null) return list;
            using (var r = Xml(e))
            {
                // Advance manually: ReadElementContentAsString and Skip already move the reader to the next node.
                StringBuilder current = null;
                r.Read();
                while (!r.EOF)
                {
                    if (r.NodeType == XmlNodeType.Element && r.NamespaceURI == MainNs)
                    {
                        if (r.LocalName == "si")
                        {
                            if (r.IsEmptyElement) list.Add(string.Empty);
                            else current = new StringBuilder();
                        }
                        else if (r.LocalName == "rPh")
                        {
                            r.Skip(); // phonetic guide text is not part of the value
                            continue;
                        }
                        else if (r.LocalName == "t" && current != null)
                        {
                            current.Append(r.ReadElementContentAsString());
                            continue;
                        }
                    }
                    else if (r.NodeType == XmlNodeType.EndElement && r.NamespaceURI == MainNs && r.LocalName == "si" && current != null)
                    {
                        list.Add(current.ToString());
                        current = null;
                    }

                    r.Read();
                }
            }

            return list;
        }

        private sealed class Cell
        {
            public int Row;
            public int Column;
            public string Value;
        }

        private static List<Cell> ReadCells(ZipArchiveEntry sheet, List<string> shared)
        {
            var cells = new List<Cell>();
            using (var r = Xml(sheet))
            {
                var row = 0;
                var nextCol = 0;
                while (r.Read())
                {
                    if (r.NodeType != XmlNodeType.Element || r.NamespaceURI != MainNs) continue;
                    if (r.LocalName == "row")
                    {
                        row = int.TryParse(r.GetAttribute("r"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rn) ? rn : row + 1;
                        if (row > MaxRows + 1) throw new CsvFormatException($"The worksheet has more than {MaxRows} rows", row);
                        nextCol = 0;
                    }
                    else if (r.LocalName == "c")
                    {
                        var reference = r.GetAttribute("r");
                        var col = reference != null ? ColumnIndex(reference) : nextCol;
                        if (col < 0) throw new CsvFormatException($"Invalid cell reference '{reference}'", row);
                        if (col >= MaxColumns) throw new CsvFormatException($"The worksheet has more than {MaxColumns} columns", row);
                        nextCol = col + 1;
                        var type = r.GetAttribute("t");
                        var value = r.IsEmptyElement ? string.Empty : ReadCellValue(r, type, shared, row);
                        if (value.Length > 0) cells.Add(new Cell { Row = row, Column = col, Value = value });
                    }
                }
            }

            return cells;
        }

        private static string ReadCellValue(XmlReader r, string type, List<string> shared, int row)
        {
            string raw = null;
            var inline = new StringBuilder();
            using (var sub = r.ReadSubtree())
            {
                sub.Read(); // the <c> element itself
                sub.Read();
                while (!sub.EOF)
                {
                    if (sub.NodeType == XmlNodeType.Element && sub.NamespaceURI == MainNs)
                    {
                        if (sub.LocalName == "v")
                        {
                            raw = sub.ReadElementContentAsString();
                            continue;
                        }

                        if (sub.LocalName == "t" && type == "inlineStr")
                        {
                            inline.Append(sub.ReadElementContentAsString());
                            continue;
                        }

                        if (sub.LocalName == "rPh")
                        {
                            sub.Skip();
                            continue;
                        }
                    }

                    sub.Read();
                }
            }

            switch (type)
            {
                case "s":
                    if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) || i < 0 || i >= shared.Count)
                        throw new CsvFormatException("Invalid shared string reference", row);
                    return shared[i];
                case "inlineStr":
                    return inline.ToString();
                case "b":
                    return raw == "1" ? "True" : raw == "0" ? "False" : raw ?? string.Empty;
                case "str":
                case "e":
                case "d":
                    return raw ?? string.Empty;
                default:
                    return NormalizeNumber(raw);
            }
        }

        /// <summary>Numbers as an administrator typed them: "806" stays "806"; binary artifacts like 45.417000000000002 become 45.417.</summary>
        public static string NormalizeNumber(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return raw;
            if (Math.Abs(d) < 1e15 && d == Math.Floor(d)) return ((long)d).ToString(CultureInfo.InvariantCulture);
            return d.ToString("G15", CultureInfo.InvariantCulture);
        }

        /// <summary>"A1" -> 0, "AB12" -> 27. Returns -1 for an invalid reference.</summary>
        public static int ColumnIndex(string reference)
        {
            var col = 0;
            var letters = 0;
            foreach (var ch in reference)
            {
                var u = char.ToUpperInvariant(ch);
                if (u >= 'A' && u <= 'Z')
                {
                    if (++letters > 3) return -1;
                    col = col * 26 + (u - 'A' + 1);
                }
                else if (char.IsDigit(ch)) break;
                else return -1;
            }

            return letters == 0 ? -1 : col - 1;
        }

        private static CsvTable ToTable(List<Cell> cells, string sheetName)
        {
            var table = new CsvTable();
            if (cells.Count == 0) return table;
            var byRow = cells.GroupBy(c => c.Row).OrderBy(g => g.Key).ToList();
            var header = byRow[0];
            var width = header.Max(c => c.Column) + 1;
            var headers = new string[width];
            for (var i = 0; i < width; i++) headers[i] = string.Empty;
            foreach (var c in header) headers[c.Column] = c.Value.Trim();
            table.Headers.AddRange(headers);
            var dup = table.Headers.GroupBy(h => h, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Key.Length > 0 && g.Count() > 1);
            if (dup != null) throw new CsvFormatException($"Duplicate column '{dup.Key}' in worksheet '{sheetName}'", header.Key);
            foreach (var g in byRow.Skip(1))
            {
                var extra = g.FirstOrDefault(c => c.Column >= width || headers[c.Column].Length == 0);
                if (extra != null)
                    throw new CsvFormatException($"Cell {ColumnName(extra.Column)}{g.Key} has a value but no column header", g.Key);
                var row = new string[width];
                for (var i = 0; i < width; i++) row[i] = string.Empty;
                foreach (var c in g) row[c.Column] = c.Value;
                table.Rows.Add(row);
                table.RowLineNumbers.Add(g.Key);
            }

            return table;
        }

        public static string ColumnName(int index)
        {
            var sb = new StringBuilder();
            index++;
            while (index > 0)
            {
                var m = (index - 1) % 26;
                sb.Insert(0, (char)('A' + m));
                index = (index - 1) / 26;
            }

            return sb.ToString();
        }

        /// <summary>Stops reading after a byte budget, so a part that lies about its size cannot exhaust memory.</summary>
        private sealed class LimitedStream : Stream
        {
            private readonly Stream _inner;
            private long _remaining;

            public LimitedStream(Stream inner, long limit)
            {
                _inner = inner;
                _remaining = limit;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var n = _inner.Read(buffer, offset, count);
                _remaining -= n;
                if (_remaining < 0) throw new CsvFormatException("A part of the workbook is too large", 0);
                return n;
            }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing) _inner.Dispose();
                base.Dispose(disposing);
            }
        }
    }

    /// <summary>
    /// Writes a single-sheet .xlsx with text cells (inline strings) and a bold header row. Values are stored as text,
    /// never as formulas, so spreadsheet formula injection is not possible.
    /// </summary>
    public static class XlsxWriter
    {
        public static void Write(Stream output, string sheetName, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
        {
            sheetName = SafeSheetName(sheetName);
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                Part(zip, "[Content_Types].xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                    "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                    "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                    "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                    "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                    "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
                    "</Types>");
                Part(zip, "_rels/.rels",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                    "</Relationships>");
                Part(zip, "xl/workbook.xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                    "<sheets><sheet name=\"" + Escape(sheetName) + "\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
                Part(zip, "xl/_rels/workbook.xml.rels",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                    "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
                    "</Relationships>");
                Part(zip, "xl/styles.xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                    "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
                    "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
                    "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
                    "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                    "<cellXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/></cellXfs>" +
                    "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
                    "</styleSheet>");

                var sheet = new StringBuilder();
                sheet.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
                sheet.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
                sheet.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
                sheet.Append("<sheetData>");
                AppendRow(sheet, 1, headers, bold: true);
                var n = 1;
                foreach (var r in rows) AppendRow(sheet, ++n, r, bold: false);
                sheet.Append("</sheetData></worksheet>");
                Part(zip, "xl/worksheets/sheet1.xml", sheet.ToString());
            }
        }

        public static void WriteFile(string path, string sheetName, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
        {
            using (var fs = File.Create(path)) Write(fs, sheetName, headers, rows);
        }

        private static void AppendRow(StringBuilder sb, int rowNumber, IReadOnlyList<string> values, bool bold)
        {
            sb.Append("<row r=\"").Append(rowNumber.ToString(CultureInfo.InvariantCulture)).Append("\">");
            for (var i = 0; i < values.Count; i++)
            {
                var v = values[i] ?? string.Empty;
                if (v.Length == 0) continue;
                sb.Append("<c r=\"").Append(XlsxReader.ColumnName(i)).Append(rowNumber.ToString(CultureInfo.InvariantCulture)).Append("\" t=\"inlineStr\"");
                if (bold) sb.Append(" s=\"1\"");
                sb.Append("><is><t xml:space=\"preserve\">").Append(Escape(v)).Append("</t></is></c>");
            }

            sb.Append("</row>");
        }

        private static void Part(ZipArchive zip, string name, string content)
        {
            var e = zip.CreateEntry(name, CompressionLevel.Optimal);
            using (var s = e.Open())
            {
                var bytes = new UTF8Encoding(false).GetBytes(content);
                s.Write(bytes, 0, bytes.Length);
            }
        }

        /// <summary>XML-escapes text and drops characters XML 1.0 cannot represent.</summary>
        private static string Escape(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                if (c < 0x20 && c != '\t' && c != '\n' && c != '\r') continue;
                if (c == 0xFFFE || c == 0xFFFF) continue;
                switch (c)
                {
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '&': sb.Append("&amp;"); break;
                    case '"': sb.Append("&quot;"); break;
                    default: sb.Append(c); break;
                }
            }

            return sb.ToString();
        }

        private static string SafeSheetName(string name)
        {
            var cleaned = new string((name ?? "Sheet1").Where(c => "[]:*?/\\".IndexOf(c) < 0).ToArray()).Trim();
            if (cleaned.Length == 0) cleaned = "Sheet1";
            return cleaned.Length > 31 ? cleaned.Substring(0, 31) : cleaned;
        }
    }

    /// <summary>Templates for hardware onboarding files in the documented Import-VmsHardware column format.</summary>
    public static class HardwareImportTemplate
    {
        public static readonly string[] Headers =
            { "DeviceType", "Name", "Address", "Channel", "UserName", "Password", "DriverNumber", "DriverGroup", "RecordingServer", "Enabled", "HardwareName", "StorageName", "Coordinates", "DeviceGroups" };

        /// <summary>Example rows (fictitious addresses from the documentation range; no real password).</summary>
        public static IReadOnlyList<IReadOnlyList<string>> ExampleRows(string recordingServer) => new List<IReadOnlyList<string>>
        {
            new[] { "Camera", "Entrance camera", "192.168.1.101", "0", "root", "", "", "", recordingServer ?? "", "True", "Entrance encoder", "", "", "/Imported cameras" },
            new[] { "Camera", "Lobby camera 1", "192.168.1.102", "0", "root", "", "", "AXIS", recordingServer ?? "", "True", "Lobby encoder", "", "", "/Imported cameras;/Lobby" },
            new[] { "Camera", "Lobby camera 2", "192.168.1.102", "1", "root", "", "", "AXIS", recordingServer ?? "", "False", "Lobby encoder", "", "", "/Imported cameras;/Lobby" }
        };
    }

    /// <summary>Reads a hardware onboarding file in any supported format.</summary>
    public static class HardwareImportFile
    {
        public static bool IsExcel(string path) => string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase);

        public static CsvTable Read(string path) => IsExcel(path) ? XlsxReader.ReadTable(path, FindSheet(path)) : CsvParser.ParseFile(path);

        /// <summary>The first worksheet with an "Address" header, else the first worksheet.</summary>
        private static string FindSheet(string path)
        {
            var info = XlsxReader.ReadWorkbookInfo(path);
            foreach (var s in info.Sheets)
            {
                try
                {
                    if (XlsxReader.ReadTable(path, s.Name).IndexOf("Address") >= 0) return s.Name;
                }
                catch (CsvFormatException)
                {
                    // A worksheet that cannot be read as a table is not the onboarding sheet.
                }
            }

            return null;
        }
    }
}
