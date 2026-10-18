using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using MilestoneAdmin.Domain.Csv;
using Xunit;

namespace MilestoneAdmin.Tests.Domain
{
    /// <summary>
    /// The fixtures in tests/Fixtures/xlsx were produced by three independent tools (openpyxl, XlsxWriter and
    /// LibreOffice) from the same data, so the reader is tested against real-world files, not only its own writer.
    /// The passwords in them are fake test values.
    /// </summary>
    public class XlsxTests
    {
        private static string Fixture(string name) => Path.Combine(TestPaths.RepoRoot, "tests", "Fixtures", "xlsx", name);

        [Theory]
        [InlineData("openpyxl-simple.xlsx")]
        [InlineData("xlsxwriter-simple.xlsx")]
        [InlineData("libreoffice-simple.xlsx")]
        public void Reads_hardware_sheets_written_by_other_tools(string file)
        {
            var table = HardwareImportFile.Read(Fixture(file));
            Assert.Equal(HardwareImportTemplate.Headers, table.Headers);
            Assert.Equal(3, table.Rows.Count);
            string Cell(int row, string col) => table.Rows[row][table.IndexOf(col)];
            Assert.Equal("Entrance", Cell(0, "Name"));
            Assert.Equal("192.168.1.101", Cell(0, "Address"));
            Assert.Equal("0", Cell(0, "Channel"));
            Assert.Equal("806", Cell(0, "DriverNumber"));
            Assert.True(HardwareCsvValidator.TryParseBool(Cell(0, "Enabled"), out var en) && en);
            Assert.Equal("45.417, -122.732", Cell(0, "Coordinates"));
            Assert.Equal(string.Empty, Cell(1, "DriverNumber"));
            Assert.Equal("1", Cell(2, "Channel"));
            Assert.True(HardwareCsvValidator.TryParseBool(Cell(2, "Enabled"), out var en2) && !en2);
            Assert.Equal("/Imported cameras;/Lobby", Cell(2, "DeviceGroups"));

            var report = HardwareCsvValidator.Validate(table);
            Assert.Equal("3 rows detected, 3 valid, 0 invalid", report.SummaryText);
            Assert.Equal(2, report.Hardware.Count);
            Assert.Equal(new[] { 0, 1 }, report.Hardware.Single(h => h.Address.Host == "192.168.1.102").Channels.Select(c => c.Channel));
        }

        [Fact]
        public void Reports_real_excel_row_numbers_and_skips_blank_rows()
        {
            // openpyxl fixture: data on the second sheet, a blank row after the first data row.
            var table = HardwareImportFile.Read(Fixture("openpyxl-simple.xlsx"));
            Assert.Equal(new[] { 2, 4, 5 }, table.RowLineNumbers);
        }

        [Fact]
        public void Detects_milestonepstools_export_workbooks()
        {
            Assert.True(XlsxReader.ReadWorkbookInfo(Fixture("pstools-export-shaped.xlsx")).IsMilestonePSToolsExport);
            Assert.False(XlsxReader.ReadWorkbookInfo(Fixture("openpyxl-simple.xlsx")).IsMilestonePSToolsExport);
            Assert.Equal(new[] { "Notes", "Cameras to add" }, XlsxReader.ReadWorkbookInfo(Fixture("openpyxl-simple.xlsx")).Sheets.Select(s => s.Name));
        }

        [Fact]
        public void Writer_output_round_trips_and_keeps_formulas_as_text()
        {
            var rows = new List<IReadOnlyList<string>>
            {
                new[] { "=HYPERLINK(\"http://evil\")", "a & b <c>", "line1\nline2", "", "x\u0001y" }
            };
            using (var ms = new MemoryStream())
            {
                XlsxWriter.Write(ms, "Data: [1]", new[] { "A", "B", "C", "D", "E" }, rows);
                ms.Position = 0;
                var table = XlsxReader.ReadTable(ms);
                Assert.Equal(new[] { "A", "B", "C", "D", "E" }, table.Headers);
                Assert.Equal("=HYPERLINK(\"http://evil\")", table.Rows[0][0]);
                Assert.Equal("a & b <c>", table.Rows[0][1]);
                Assert.Equal("line1\nline2", table.Rows[0][2]);
                Assert.Equal("xy", table.Rows[0][4]);
                ms.Position = 0;
                Assert.Equal("Data 1", XlsxReader.ReadWorkbookInfo(ms).Sheets.Single().Name);
                ms.Position = 0;
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Read, true))
                using (var r = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml").Open()))
                    Assert.DoesNotContain("<f>", r.ReadToEnd());
            }
        }

        [Fact]
        public void Template_validates_with_the_onboarding_rules()
        {
            using (var ms = new MemoryStream())
            {
                XlsxWriter.Write(ms, "Hardware import", HardwareImportTemplate.Headers, HardwareImportTemplate.ExampleRows("RS01"));
                ms.Position = 0;
                var report = HardwareCsvValidator.Validate(XlsxReader.ReadTable(ms));
                Assert.Equal(3, report.ValidRows);
                Assert.Equal(2, report.Hardware.Count);
            }
        }

        [Theory]
        [InlineData("A1", 0)]
        [InlineData("Z9", 25)]
        [InlineData("AA10", 26)]
        [InlineData("AB12", 27)]
        [InlineData("XFD1", 16383)]
        [InlineData("1A", -1)]
        [InlineData("ABCD1", -1)]
        public void Column_references(string reference, int index)
        {
            Assert.Equal(index, XlsxReader.ColumnIndex(reference));
            if (index >= 0) Assert.Equal(reference.TrimEnd("0123456789".ToCharArray()), XlsxReader.ColumnName(index));
        }

        [Theory]
        [InlineData("806", "806")]
        [InlineData("806.0", "806")]
        [InlineData("45.417000000000002", "45.417")]
        [InlineData("-122.73200000000001", "-122.732")]
        [InlineData("abc", "abc")]
        public void Numbers_read_as_typed(string raw, string expected)
        {
            Assert.Equal(expected, XlsxReader.NormalizeNumber(raw));
        }

        private static MemoryStream Package(Dictionary<string, string> parts)
        {
            var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                foreach (var p in parts)
                {
                    using (var s = zip.CreateEntry(p.Key).Open())
                    {
                        var b = Encoding.UTF8.GetBytes(p.Value);
                        s.Write(b, 0, b.Length);
                    }
                }
            }

            ms.Position = 0;
            return ms;
        }

        private const string Workbook = "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"S\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>";
        private const string Rels = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"x\" Target=\"worksheets/sheet1.xml\"/></Relationships>";

        [Fact]
        public void Rejects_dtds_and_external_entities()
        {
            var sheet = "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>&e;</t></is></c></row></sheetData></worksheet>";
            using (var ms = Package(new Dictionary<string, string> { { "xl/workbook.xml", Workbook }, { "xl/_rels/workbook.xml.rels", Rels }, { "xl/worksheets/sheet1.xml", sheet } }))
                Assert.ThrowsAny<Exception>(() => XlsxReader.ReadTable(ms));
        }

        [Fact]
        public void Rejects_files_that_are_not_workbooks()
        {
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("Address\n10.0.0.1")))
                Assert.Throws<CsvFormatException>(() => XlsxReader.ReadTable(ms));
            using (var ms = Package(new Dictionary<string, string> { { "word/document.xml", "<x/>" } }))
                Assert.Throws<CsvFormatException>(() => XlsxReader.ReadTable(ms));
        }

        [Fact]
        public void Rejects_values_without_a_header_and_duplicate_headers()
        {
            var noHeader = "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>Address</t></is></c></row>" +
                           "<row r=\"2\"><c r=\"A2\" t=\"inlineStr\"><is><t>10.0.0.1</t></is></c><c r=\"C2\" t=\"inlineStr\"><is><t>stray</t></is></c></row></sheetData></worksheet>";
            using (var ms = Package(new Dictionary<string, string> { { "xl/workbook.xml", Workbook }, { "xl/_rels/workbook.xml.rels", Rels }, { "xl/worksheets/sheet1.xml", noHeader } }))
            {
                var ex = Assert.Throws<CsvFormatException>(() => XlsxReader.ReadTable(ms));
                Assert.Contains("C2", ex.Message);
            }

            var dup = "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>Name</t></is></c><c r=\"B1\" t=\"inlineStr\"><is><t>name</t></is></c></row></sheetData></worksheet>";
            using (var ms = Package(new Dictionary<string, string> { { "xl/workbook.xml", Workbook }, { "xl/_rels/workbook.xml.rels", Rels }, { "xl/worksheets/sheet1.xml", dup } }))
                Assert.Throws<CsvFormatException>(() => XlsxReader.ReadTable(ms));
        }

        [Fact]
        public void Shared_strings_with_rich_text_and_phonetic_runs()
        {
            var shared = "<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><si><t>Address</t></si><si><r><t>10.0.</t></r><r><t>0.1</t></r><rPh sb=\"0\" eb=\"1\"><t>IGNORED</t></rPh></si><si/></sst>";
            var sheet = "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c></row><row r=\"2\"><c r=\"A2\" t=\"s\"><v>1</v></c></row></sheetData></worksheet>";
            using (var ms = Package(new Dictionary<string, string> { { "xl/workbook.xml", Workbook }, { "xl/_rels/workbook.xml.rels", Rels }, { "xl/worksheets/sheet1.xml", sheet }, { "xl/sharedStrings.xml", shared } }))
            {
                var t = XlsxReader.ReadTable(ms);
                Assert.Equal("Address", t.Headers.Single());
                Assert.Equal("10.0.0.1", t.Rows.Single()[0]);
            }
        }
    }
}
