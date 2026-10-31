using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace PmoReview.Core.Uploads;

/// <summary>One data row of an uploaded sheet: its row number in the file, the cells by header, and which cells were numbers.</summary>
public sealed record TableRow(int Number, IReadOnlyDictionary<string, string> Cells, IReadOnlySet<string> Numeric)
{
    public string? this[string column] => Cells.TryGetValue(column, out var v) ? v : null;
}

/// <summary>The first (or named) sheet of an uploaded file: headers as written (trimmed) and the non-empty data rows.</summary>
public sealed record Table(IReadOnlyList<string> Headers, IReadOnlyList<TableRow> Rows, bool FromWorkbook);

/// <summary>A file the upload refuses as a whole (row 0 in the error list).</summary>
public sealed class UploadFileException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// F4: reads an uploaded .csv or .xlsx as untrusted data (R6). Only these two formats; macro-enabled and binary Excel
/// formats are refused (F98), as is an .xlsx whose package declares a macro or VBA part. Formulas are never evaluated: an
/// .xlsx cell gives its stored value only. Limits keep a crafted file (zip bomb, huge sheet) from exhausting the API.
/// </summary>
public static class TabularFile
{
    public const int MaxRows = 20_000;
    public const int MaxColumns = 64;
    public const int MaxCellChars = 2_000;
    /// <summary>Unpacked size of every part of an .xlsx together.</summary>
    public const long MaxUnpackedBytes = 64L * 1024 * 1024;
    public const int MaxZipEntries = 1_000;

    private static readonly HashSet<string> MacroExtensions = new(StringComparer.OrdinalIgnoreCase) { ".xlsm", ".xltm", ".xlsb", ".xlam" };
    private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static Table Read(byte[] bytes, string? fileName, string? sheet = null)
    {
        if (bytes.Length == 0) throw new UploadFileException("EmptyFile", "The file is empty.");
        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        if (MacroExtensions.Contains(ext))
            throw new UploadFileException("MacroFile", $"Macro-enabled or binary Excel files ({ext}) are refused. Save the sheet as .xlsx or .csv.");
        var zip = bytes.Length >= 4 && bytes[0] == 'P' && bytes[1] == 'K' && bytes[2] == 3 && bytes[3] == 4;
        var ole = bytes.Length >= 8 && bytes[0] == 0xD0 && bytes[1] == 0xCF && bytes[2] == 0x11 && bytes[3] == 0xE0;
        return ext switch
        {
            ".xlsx" when zip => ReadXlsx(bytes, sheet),
            ".xlsx" => throw new UploadFileException("InvalidFile", "The file is not a valid .xlsx workbook."),
            ".csv" when zip || ole => throw new UploadFileException("UnsupportedFormat", "The .csv file is a workbook: upload it as .xlsx, or save it as CSV."),
            ".csv" => ReadCsv(bytes),
            ".xls" => throw new UploadFileException("UnsupportedFormat", "The old binary .xls format is refused (it can carry macros). Save the sheet as .xlsx or .csv."),
            _ => throw new UploadFileException("UnsupportedFormat", "Only .xlsx and .csv files are accepted."),
        };
    }

    // ---- CSV (RFC 4180; comma, or semicolon when the header has no comma) ------------------------------------------

    private static Table ReadCsv(byte[] bytes)
    {
        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new UploadFileException("InvalidFile", "The .csv file is not UTF-8 text. Save it as \"CSV UTF-8\".");
        }
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        if (text.Contains('\0')) throw new UploadFileException("InvalidFile", "The .csv file contains binary data.");
        var firstLine = text.Split('\n', 2)[0];
        var delimiter = !firstLine.Contains(',') && firstLine.Contains(';') ? ';' : ',';

        var records = new List<List<string>>();
        var record = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        var atStart = true;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
                continue;
            }
            if (c == '"' && atStart) { quoted = true; atStart = false; continue; }
            if (c == delimiter) { record.Add(cell.ToString()); cell.Clear(); atStart = true; continue; }
            if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                record.Add(cell.ToString()); cell.Clear(); atStart = true;
                records.Add(record); record = [];
                if (records.Count > MaxRows + 1) throw TooManyRows();
                continue;
            }
            cell.Append(c);
            atStart = false;
        }
        if (quoted) throw new UploadFileException("InvalidFile", "The .csv file has an unclosed quote.");
        if (cell.Length > 0 || record.Count > 0) { record.Add(cell.ToString()); records.Add(record); }

        var rows = records.Select((r, i) => (Number: i + 1, Cells: r)).Where(r => r.Cells.Any(x => x.Trim().Length > 0)).ToList();
        if (rows.Count == 0) throw new UploadFileException("EmptyFile", "The file has no header row.");
        var headers = Headers(rows[0].Cells);
        var data = rows.Skip(1).Select(r => Row(r.Number, headers, r.Cells, new HashSet<string>())).ToList();
        return new Table(headers.Where(h => h.Length > 0).ToList(), data, FromWorkbook: false);
    }

    // ---- XLSX (stored values only) ---------------------------------------------------------------------------------

    private static Table ReadXlsx(byte[] bytes, string? sheetName)
    {
        try
        {
            using var archive = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
            if (archive.Entries.Count > MaxZipEntries) throw new UploadFileException("InvalidFile", "The workbook has too many parts.");
            if (archive.Entries.Sum(e => e.Length) > MaxUnpackedBytes)
                throw new UploadFileException("FileTooLarge", $"The workbook is larger than {MaxUnpackedBytes / (1024 * 1024)} MB when unpacked.");
            if (archive.Entries.Any(e => e.FullName.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase))
                || Text(archive, "[Content_Types].xml") is { } types && (types.Contains("macroEnabled", StringComparison.OrdinalIgnoreCase) || types.Contains("vbaProject", StringComparison.OrdinalIgnoreCase)))
                throw new UploadFileException("MacroFile", "The workbook contains macros (VBA). Save it as a plain .xlsx without macros, or as .csv.");

            var workbook = Xml(archive, "xl/workbook.xml") ?? throw new UploadFileException("InvalidFile", "The file is not a valid .xlsx workbook.");
            var pr = workbook.GetElementsByTagName("workbookPr", MainNs).OfType<XmlElement>().FirstOrDefault();
            if (pr?.GetAttribute("date1904") is "1" or "true")
                throw new UploadFileException("Date1904", "The workbook uses the 1904 date system. Use the 1900 date system or write dates as text (yyyy-mm-dd).");
            var sheets = workbook.GetElementsByTagName("sheet", MainNs).OfType<XmlElement>()
                .Select(e => (Name: e.GetAttribute("name"), Rel: e.GetAttribute("id", RelNs))).ToList();
            if (sheets.Count == 0) throw new UploadFileException("InvalidFile", "The workbook has no sheet.");
            var chosen = sheetName is null ? sheets[0] : sheets.FirstOrDefault(s => string.Equals(s.Name.Trim(), sheetName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (chosen.Name is null) throw new UploadFileException("SheetNotFound", $"The workbook has no sheet named \"{sheetName}\".");

            var rels = Xml(archive, "xl/_rels/workbook.xml.rels");
            var target = rels?.GetElementsByTagName("Relationship").OfType<XmlElement>().FirstOrDefault(r => r.GetAttribute("Id") == chosen.Rel)?.GetAttribute("Target")
                         ?? throw new UploadFileException("InvalidFile", "The workbook's sheet list is damaged.");
            var path = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
            var shared = SharedStrings(archive);
            var entry = archive.GetEntry(path) ?? throw new UploadFileException("InvalidFile", "The workbook's sheet is missing.");
            return ReadSheet(entry, shared);
        }
        catch (InvalidDataException)
        {
            throw new UploadFileException("InvalidFile", "The file is not a valid .xlsx workbook.");
        }
        catch (XmlException)
        {
            throw new UploadFileException("InvalidFile", "The workbook's content is damaged.");
        }
    }

    private static Table ReadSheet(ZipArchiveEntry entry, IReadOnlyList<string> shared)
    {
        var raw = new List<(int Number, Dictionary<int, string> Cells, HashSet<int> Numeric)>();
        using (var stream = entry.Open())
        using (var reader = XmlReader.Create(stream, Safe()))
        {
            var rowNumber = 0;
            Dictionary<int, string>? cells = null;
            HashSet<int>? numeric = null;
            var nextColumn = 0;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "row")
                {
                    rowNumber = int.TryParse(reader.GetAttribute("r"), out var r) ? r : rowNumber + 1;
                    cells = []; numeric = []; nextColumn = 0;
                    if (reader.IsEmptyElement) cells = null;
                    else raw.Add((rowNumber, cells, numeric));
                    if (raw.Count > MaxRows + 1) throw TooManyRows();
                }
                else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "c" && cells is not null)
                {
                    var column = Column(reader.GetAttribute("r")) ?? nextColumn;
                    nextColumn = column + 1;
                    var type = reader.GetAttribute("t");
                    var (value, isNumber) = CellValue(reader, type, shared); // leaves the reader on </c>
                    if (column >= MaxColumns) throw new UploadFileException("InvalidFile", $"The file has more than {MaxColumns} columns.");
                    if (value is not null) cells[column] = value;
                    if (isNumber) numeric!.Add(column);
                }
            }
        }
        var rows = raw.Where(r => r.Cells.Values.Any(v => v.Trim().Length > 0)).ToList();
        if (rows.Count == 0) throw new UploadFileException("EmptyFile", "The sheet has no header row.");
        var width = rows.Max(r => r.Cells.Count == 0 ? 0 : r.Cells.Keys.Max() + 1);
        List<string> Line(Dictionary<int, string> c) => Enumerable.Range(0, width).Select(i => c.TryGetValue(i, out var v) ? v : "").ToList();
        var headers = Headers(Line(rows[0].Cells));
        var data = rows.Skip(1).Select(r => Row(r.Number, headers, Line(r.Cells),
            r.Numeric.Where(i => i < headers.Count && headers[i].Length > 0).Select(i => headers[i]).ToHashSet(StringComparer.OrdinalIgnoreCase))).ToList();
        return new Table(headers.Where(h => h.Length > 0).ToList(), data, FromWorkbook: true);
    }

    /// <summary>The stored value of a cell (never a formula result computed here) and whether it is a number.</summary>
    private static (string? Value, bool Number) CellValue(XmlReader reader, string? type, IReadOnlyList<string> shared)
    {
        if (reader.IsEmptyElement) return (null, false);
        string? v = null;
        var inline = new StringBuilder();
        using (var cell = reader.ReadSubtree())
        {
            var inInline = false;
            cell.Read(); // <c>
            while (!cell.EOF)
            {
                if (cell.NodeType == XmlNodeType.Element && cell.LocalName == "v") { v = cell.ReadElementContentAsString(); continue; }
                if (cell.NodeType == XmlNodeType.Element && cell.LocalName == "is") inInline = true;
                if (cell.NodeType == XmlNodeType.Element && cell.LocalName == "rPh") { cell.Skip(); continue; }
                if (inInline && cell.NodeType == XmlNodeType.Element && cell.LocalName == "t") { inline.Append(cell.ReadElementContentAsString()); continue; }
                cell.Read();
            }
        }
        return type switch
        {
            "s" => (int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < shared.Count ? shared[i] : "", false),
            "inlineStr" => (inline.ToString(), false),
            "b" => (v == "1" ? "TRUE" : "FALSE", false),
            "str" or "e" or "d" => (v ?? "", false),
            _ => (v, v is not null),
        };
    }

    private static IReadOnlyList<string> SharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return [];
        var list = new List<string>();
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, Safe());
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "si") continue;
            var sb = new StringBuilder();
            if (!reader.IsEmptyElement)
            {
                using var si = reader.ReadSubtree();
                si.Read(); // <si>
                while (!si.EOF)
                {
                    if (si.NodeType == XmlNodeType.Element && si.LocalName == "rPh") { si.Skip(); continue; } // phonetic hints
                    if (si.NodeType == XmlNodeType.Element && si.LocalName == "t") { sb.Append(si.ReadElementContentAsString()); continue; }
                    si.Read();
                }
            }
            list.Add(sb.ToString());
        }
        return list;
    }

    /// <summary>"BC12" → 54 (zero-based column), or null.</summary>
    private static int? Column(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return null;
        var n = 0;
        var letters = 0;
        foreach (var ch in reference)
        {
            if (ch is >= 'A' and <= 'Z') { n = n * 26 + (ch - 'A' + 1); letters++; }
            else break;
        }
        return letters is 0 or > 3 ? null : n - 1;
    }

    private static XmlDocument? Xml(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name);
        if (entry is null) return null;
        var doc = new XmlDocument { XmlResolver = null };
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, Safe());
        doc.Load(reader);
        return doc;
    }

    private static string? Text(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name);
        if (entry is null) return null;
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private static XmlReaderSettings Safe() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, IgnoreProcessingInstructions = true,
        MaxCharactersInDocument = MaxUnpackedBytes,
    };

    // ---- shared ------------------------------------------------------------------------------------------------------

    private static List<string> Headers(IReadOnlyList<string> cells)
    {
        if (cells.Count > MaxColumns) throw new UploadFileException("InvalidFile", $"The file has more than {MaxColumns} columns.");
        var headers = cells.Select(c => c.Trim()).ToList();
        var duplicate = headers.Where(h => h.Length > 0).GroupBy(h => h, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new UploadFileException("DuplicateColumn", $"The column \"{duplicate.Key}\" appears more than once.");
        return headers;
    }

    private static TableRow Row(int number, IReadOnlyList<string> headers, IReadOnlyList<string> cells, IReadOnlySet<string> numeric)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < headers.Count; i++)
        {
            if (headers[i].Length == 0) continue;
            var value = i < cells.Count ? cells[i] : "";
            map[headers[i]] = value.Length > MaxCellChars ? value[..(MaxCellChars + 1)] : value;
        }
        return new TableRow(number, map, numeric);
    }

    private static UploadFileException TooManyRows() => new("TooManyRows", $"The file has more than {MaxRows:N0} rows.");
}
