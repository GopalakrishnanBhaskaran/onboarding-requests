using System.IO.Compression;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using PmoReview.Core.Common;
using PmoReview.Core.Uploads;

namespace PmoReview.Tests.Support;

/// <summary>Builds SAMPLE upload files in memory: CSV, a real .xlsx package (shared strings, numbers, dates), multipart bodies.</summary>
public static class UploadFiles
{
    public static readonly string[] MappingHeader =
        ["TenantId", "Domain", "ResourceGroupType", "Environment", "AccessLevel", "EntraGroupName", "EntraGroupObjectId", "Owner", "EffectiveFrom", "EffectiveTo"];
    public static readonly string[] InventoryHeader =
        ["EnterpriseId", "EnterpriseEmail", "DisplayName", "EmployeeId", "Domain", "ManagerEnterpriseId", "Status", "AlternateEmail"];

    public static byte[] Csv(IEnumerable<IEnumerable<object?>> rows, bool bom = false) =>
        Encoding.UTF8.GetBytes((bom ? "﻿" : "") + string.Join("\r\n", rows.Select(r => string.Join(",", r.Select(CsvCell)))) + "\r\n");

    private static string CsvCell(object? v)
    {
        var s = v switch { null => "", DateOnly d => d.ToString("yyyy-MM-dd"), _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "" };
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
    }

    /// <summary>An .xlsx with the given sheets. Strings go to shared strings (every other one inline), numbers and dates are numeric cells.</summary>
    public static byte[] Xlsx(IReadOnlyList<(string Name, IReadOnlyList<IReadOnlyList<object?>> Rows)> sheets, bool macro = false, bool vbaPart = false, bool date1904 = false)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var shared = new List<string>();
            var types = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
                + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>"
                + $"<Override PartName=\"/xl/workbook.xml\" ContentType=\"{(macro ? "application/vnd.ms-excel.sheet.macroEnabled.main+xml" : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")}\"/>");
            var wb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">"
                + (date1904 ? "<workbookPr date1904=\"1\"/>" : "<workbookPr/>") + "<sheets>");
            var rels = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            for (var i = 0; i < sheets.Count; i++)
            {
                wb.Append($"<sheet name=\"{SecurityElement.Escape(sheets[i].Name)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");
                rels.Append($"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>");
                types.Append($"<Override PartName=\"/xl/worksheets/sheet{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
                var sheet = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
                var rows = sheets[i].Rows;
                for (var r = 0; r < rows.Count; r++)
                {
                    sheet.Append($"<row r=\"{r + 1}\">");
                    for (var c = 0; c < rows[r].Count; c++)
                    {
                        var reference = $"{(char)('A' + c)}{r + 1}";
                        switch (rows[r][c])
                        {
                            case null: break;
                            case DateOnly d:
                                sheet.Append($"<c r=\"{reference}\" s=\"1\"><v>{d.ToDateTime(TimeOnly.MinValue).ToOADate()}</v></c>");
                                break;
                            case int or long or double:
                                sheet.Append($"<c r=\"{reference}\"><v>{Convert.ToString(rows[r][c], System.Globalization.CultureInfo.InvariantCulture)}</v></c>");
                                break;
                            default:
                                var text = rows[r][c]!.ToString()!;
                                if ((r + c) % 2 == 0) { shared.Add(text); sheet.Append($"<c r=\"{reference}\" t=\"s\"><v>{shared.Count - 1}</v></c>"); }
                                else sheet.Append($"<c r=\"{reference}\" t=\"inlineStr\"><is><t>{SecurityElement.Escape(text)}</t></is></c>");
                                break;
                        }
                    }
                    sheet.Append("</row>");
                }
                sheet.Append("</sheetData></worksheet>");
                Entry(zip, $"xl/worksheets/sheet{i + 1}.xml", sheet.ToString());
            }
            if (vbaPart) Entry(zip, "xl/vbaProject.bin", "SAMPLE not really VBA");
            Entry(zip, "[Content_Types].xml", types.Append("</Types>").ToString());
            Entry(zip, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
            Entry(zip, "xl/workbook.xml", wb.Append("</sheets></workbook>").ToString());
            Entry(zip, "xl/_rels/workbook.xml.rels", rels.Append("</Relationships>").ToString());
            Entry(zip, "xl/sharedStrings.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">"
                + string.Concat(shared.Select(t => $"<si><t>{SecurityElement.Escape(t)}</t></si>")) + "</sst>");
        }
        return ms.ToArray();
    }

    public static byte[] Xlsx(IEnumerable<IEnumerable<object?>> rows, string sheet = "Sheet1") =>
        Xlsx([(sheet, rows.Select(r => (IReadOnlyList<object?>)r.ToList()).ToList())]);

    private static void Entry(ZipArchive zip, string name, string content)
    {
        using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        w.Write(content);
    }

    public const string Boundary = "----SAMPLEboundary7MA4YWxk";

    public static byte[] Multipart(byte[] file, string fileName, string field = "file")
    {
        var head = Encoding.UTF8.GetBytes($"--{Boundary}\r\nContent-Disposition: form-data; name=\"note\"\r\n\r\nSAMPLE\r\n" +
            $"--{Boundary}\r\nContent-Disposition: form-data; name=\"{field}\"; filename=\"{fileName}\"\r\nContent-Type: application/octet-stream\r\n\r\n");
        var tail = Encoding.UTF8.GetBytes($"\r\n--{Boundary}--\r\n");
        return [.. head, .. file, .. tail];
    }

    public static string MultipartType => $"multipart/form-data; boundary={Boundary}";
}

/// <summary>Signs SAMPLE Entra-style v2.0 access tokens with a test RSA key (never a real key or tenant).</summary>
public sealed class TestTokens : ISigningKeys, IDisposable
{
    public const string Tenant = "11111111-2222-4333-8444-555555555555";
    public const string Audience = "99999999-8888-4777-8666-555555555555";
    public const string ClientApp = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee";
    private readonly RSA rsa = RSA.Create(2048);
    public string Kid { get; } = "sample-kid-1";
    public int Lookups { get; private set; }

    public Task<SigningKey?> FindAsync(string tenantId, string kid, CancellationToken ct)
    {
        Lookups++;
        var p = rsa.ExportParameters(false);
        return Task.FromResult<SigningKey?>(tenantId == Tenant && kid == Kid ? new SigningKey(Kid, p.Modulus!, p.Exponent!) : null);
    }

    public string Token(DateTimeOffset now, string[]? roles = null, string tenant = Tenant, string audience = Audience, string? issuer = null, string ver = "2.0",
        TimeSpan? lifetime = null, bool delegated = false, RSA? signer = null, string? kid = null)
    {
        var header = new Dictionary<string, object> { ["alg"] = "RS256", ["typ"] = "JWT", ["kid"] = kid ?? Kid };
        var claims = new Dictionary<string, object>
        {
            ["aud"] = audience, ["iss"] = issuer ?? $"https://login.microsoftonline.com/{tenant}/v2.0", ["tid"] = tenant, ["ver"] = ver,
            ["iat"] = now.ToUnixTimeSeconds(), ["nbf"] = now.ToUnixTimeSeconds(), ["exp"] = (now + (lifetime ?? TimeSpan.FromHours(1))).ToUnixTimeSeconds(),
            ["azp"] = ClientApp, ["oid"] = "cccccccc-dddd-4eee-8fff-000000000000", ["idtyp"] = delegated ? "user" : "app",
            ["roles"] = roles ?? [UploadTokenValidator.Role],
        };
        if (delegated) claims["scp"] = "Uploads.Write";
        var body = $"{B64(AppJson.Serialize(header))}.{B64(AppJson.Serialize(claims))}";
        var sig = (signer ?? rsa).SignData(Encoding.ASCII.GetBytes(body), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{body}.{B64(sig)}";
    }

    private static string B64(string s) => B64(Encoding.UTF8.GetBytes(s));
    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose() => rsa.Dispose();
}
