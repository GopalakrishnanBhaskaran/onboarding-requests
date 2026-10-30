using System.Globalization;
using System.Text.RegularExpressions;
using PmoReview.Core.Common;
using PmoReview.Core.Domain;
using PmoReview.Core.Resources;

namespace PmoReview.Core.Uploads;

/// <summary>One finding of a validation (F4 contract). Row 0 = the file as a whole.</summary>
public sealed record UploadError(int Row, string Column, string Code, string Message);

/// <summary>A valid I1 mapping sheet row.</summary>
public sealed record MappingUpload(int Row, string TenantId, string Domain, string ResourceGroupType, string Environment, string AccessLevel,
    string EntraGroupName, string? EntraGroupObjectId, string Owner, DateOnly EffectiveFrom, DateOnly? EffectiveTo)
{
    public string Key => UploadSchema.MappingKey(TenantId, Domain, ResourceGroupType, Environment, EntraGroupName, AccessLevel);
}

/// <summary>A valid I2 people inventory row.</summary>
public sealed record PersonUpload(int Row, string EnterpriseId, string? EnterpriseEmail, string DisplayName, string? EmployeeId, string Domain,
    string? ManagerEnterpriseId, PersonStatus Status, string? AlternateEmail)
{
    public string Key => Person.KeyOf(EnterpriseId);
}

/// <summary>
/// F4 step 2: the column contract of the two uploads (I1, I2) and the row checks. Column names are matched
/// case-insensitively after trimming; a missing or renamed column stops the file (the app never guesses a column). Every
/// value is untrusted data: checked against its type, enum or format, never interpreted.
/// </summary>
public static partial class UploadSchema
{
    public static readonly IReadOnlyList<string> MappingColumns =
        ["TenantId", "Domain", "ResourceGroupType", "Environment", "AccessLevel", "EntraGroupName", "EntraGroupObjectId", "Owner", "EffectiveFrom", "EffectiveTo"];
    public static readonly IReadOnlySet<string> MappingOptional = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "EntraGroupObjectId", "EffectiveTo" };
    public static readonly IReadOnlyList<string> InventoryColumns =
        ["EnterpriseId", "EnterpriseEmail", "DisplayName", "EmployeeId", "Domain", "ManagerEnterpriseId", "Status", "AlternateEmail"];
    public static readonly IReadOnlySet<string> InventoryOptional = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AlternateEmail" };

    /// <summary>I1 environments, plus PreProd for the existing projects (decision 9).</summary>
    public static readonly IReadOnlyList<string> UploadEnvironments = ["Dev", "Test", "UAT", "PreProd", "Prod"];

    public static IReadOnlyList<string> Columns(DataKind kind) => kind == DataKind.Mapping ? MappingColumns : InventoryColumns;
    private static IReadOnlySet<string> Optional(DataKind kind) => kind == DataKind.Mapping ? MappingOptional : InventoryOptional;

    /// <summary>
    /// The mapping upload key (F4): TenantId + Domain + ResourceGroupType + Environment + EntraGroupName + AccessLevel, each
    /// trimmed, spaces collapsed, lower case.
    /// </summary>
    public static string MappingKey(string? tenantId, string? domain, string? type, string? environment, string? group, string? level) =>
        string.Join("|", new[] { tenantId, domain, type, environment, group, level }.Select(v => Collapse(v).ToLowerInvariant()));

    /// <summary>The key of a stored row, filling the I1 fields older rows lack (tenant = the project's, domain = resource, type = Domain).</summary>
    public static string KeyOf(MappingRow r, string? projectTenant) =>
        MappingKey(r.TenantId ?? projectTenant, r.Domain ?? r.Resource, ResourceGroupTypes.Canonical(r.ResourceGroupType) ?? ResourceGroupTypes.Domain,
            UploadEnvironments.FirstOrDefault(e => Environments.Same(e, r.Environment)) ?? r.Environment, r.EntraGroup, AccessLevels.Canonical(r.AccessLevel) ?? r.AccessLevel);

    /// <summary>Required columns that are missing: the file stops here. A header that differs only in spaces/underscores is named, not used.</summary>
    public static List<UploadError> MissingColumns(DataKind kind, Table t)
    {
        var errors = new List<UploadError>();
        foreach (var column in Columns(kind).Where(c => !Optional(kind).Contains(c)))
        {
            if (t.Headers.Contains(column, StringComparer.OrdinalIgnoreCase)) continue;
            var similar = t.Headers.FirstOrDefault(h => Loose(h) == Loose(column));
            errors.Add(new UploadError(0, column, "MissingColumn", similar is null
                ? $"Required column \"{column}\" is missing. The file is not imported."
                : $"Required column \"{column}\" is missing; the file has \"{similar}\". Rename it to \"{column}\" (the app does not guess). The file is not imported."));
        }
        return errors;
    }

    // ---- mapping sheet (I1) ------------------------------------------------------------------------------------------

    public static (List<MappingUpload> Rows, List<UploadError> Errors) Mapping(Table t, Project project, string? tenant)
    {
        var errors = MissingColumns(DataKind.Mapping, t);
        var rows = new List<MappingUpload>();
        if (errors.Count > 0) return (rows, errors);
        if (string.IsNullOrWhiteSpace(tenant))
            return (rows, [new UploadError(0, "TenantId", "NoTenant", "This project has no Entra tenant id yet, so no TenantId can match it. PMO sets the project's tenant first.")]);
        if (t.Rows.Count == 0) return (rows, [new UploadError(0, "", "NoRows", "The file has a header but no rows.")]);

        foreach (var row in t.Rows)
        {
            var e = new RowErrors(row, errors);
            var tenantId = e.Required("TenantId", 36);
            if (tenantId is not null && !Guid.TryParse(tenantId, out _)) e.Add("TenantId", "InvalidGuid", "TenantId must be a GUID.");
            else if (tenantId is not null && !string.Equals(tenantId, tenant.Trim(), StringComparison.OrdinalIgnoreCase))
                e.Add("TenantId", "WrongTenant", $"TenantId {tenantId} is not this project's tenant; rows of another tenant are never imported.");
            var domain = e.Required("Domain", 100);
            var type = e.Enum("ResourceGroupType", ResourceGroupTypes.Values);
            var env = e.Enum("Environment", UploadEnvironments);
            if (env is not null && project.Environments.Count > 0 && Environments.Canonical(project, env) is null)
                e.Add("Environment", "InvalidValue", $"The project has no environment {env} (it has {string.Join(", ", project.Environments)}).");
            var level = e.Enum("AccessLevel", AccessLevels.Ordered);
            var group = e.Required("EntraGroupName", 256);
            var objectId = e.Optional("EntraGroupObjectId", 36);
            if (objectId is not null && !Guid.TryParse(objectId, out _)) e.Add("EntraGroupObjectId", "InvalidGuid", "EntraGroupObjectId must be a GUID (or empty).");
            var owner = e.Mail("Owner", required: true);
            var from = e.Date("EffectiveFrom", required: true);
            var to = e.Date("EffectiveTo", required: false);
            if (from is not null && to is not null && to < from) e.Add("EffectiveTo", "DateOrder", "EffectiveTo is before EffectiveFrom.");
            if (e.Ok)
                rows.Add(new MappingUpload(row.Number, tenantId!.ToLowerInvariant(), domain!, type!, Environments.Canonical(project, env) ?? env!, level!, group!,
                    objectId?.ToLowerInvariant(), owner!, from!.Value, to));
        }
        Duplicates(rows, r => r.Key, r => r with { Row = 0 }, r => r.Row, "TenantId+Domain+ResourceGroupType+Environment+EntraGroupName+AccessLevel", "EntraGroupName", errors);
        NearDuplicates(rows, errors);
        return (rows, errors);
    }

    /// <summary>
    /// F1 rule inside the file: two group names of the same environment that normalise to the same text or are within edit
    /// distance 2 are an error, unless both carry (different) object ids. Never merged silently.
    /// </summary>
    private static void NearDuplicates(List<MappingUpload> rows, List<UploadError> errors)
    {
        foreach (var env in rows.GroupBy(r => r.Environment, StringComparer.OrdinalIgnoreCase))
        {
            var names = env.GroupBy(r => Collapse(r.EntraGroupName).ToLowerInvariant())
                .Select(g => (Name: g.First().EntraGroupName, Row: g.Min(x => x.Row), Norm: ResourceResolver.Normalise(g.First().EntraGroupName),
                    Ids: g.Select(x => x.EntraGroupObjectId).Where(x => x is not null).Distinct().ToList()))
                .OrderBy(n => n.Row).ToList();
            for (var i = 0; i < names.Count; i++)
            for (var j = 0; j < i; j++)
            {
                var (a, b) = (names[j], names[i]);
                if (Math.Abs(a.Norm.Length - b.Norm.Length) > 2 || ResourceResolver.EditDistance(a.Norm, b.Norm, 2) > 2) continue;
                if (a.Ids.Count == 1 && b.Ids.Count == 1 && a.Ids[0] != b.Ids[0]) continue; // two real groups
                errors.Add(new UploadError(b.Row, "EntraGroupName", "NearDuplicateGroup",
                    $"\"{b.Name}\" is a near-duplicate of \"{a.Name}\" (row {a.Row}, {env.Key}). Fix the spelling, or give both their EntraGroupObjectId."));
                break;
            }
        }
    }

    // ---- people inventory (I2) ---------------------------------------------------------------------------------------

    public static (List<PersonUpload> Rows, List<UploadError> Errors) Inventory(Table t)
    {
        var errors = MissingColumns(DataKind.Inventory, t);
        var rows = new List<PersonUpload>();
        if (errors.Count > 0) return (rows, errors);
        if (t.Rows.Count == 0) return (rows, [new UploadError(0, "", "NoRows", "The file has a header but no rows.")]);
        foreach (var row in t.Rows)
        {
            var e = new RowErrors(row, errors);
            var id = e.Required("EnterpriseId", 113);
            if (id is not null && !EnterpriseIdFormat().IsMatch(id))
                e.Add("EnterpriseId", "InvalidEnterpriseId", "EnterpriseId must be a UPN like jane.doe@company.com (no guest #EXT# form, no spaces).");
            var mail = e.Mail("EnterpriseEmail", required: false);
            var name = e.Required("DisplayName", 256);
            var employee = e.Optional("EmployeeId", 64);
            var domain = e.Required("Domain", 100);
            var manager = e.Optional("ManagerEnterpriseId", 113);
            if (manager is not null && !EnterpriseIdFormat().IsMatch(manager))
                e.Add("ManagerEnterpriseId", "InvalidEnterpriseId", "ManagerEnterpriseId must be a UPN (or empty).");
            var status = e.Enum("Status", ["Active", "Leaver"]);
            var alternate = e.Mail("AlternateEmail", required: false);
            if (e.Ok)
                rows.Add(new PersonUpload(row.Number, id!.ToLowerInvariant(), mail, name!, employee, domain!, manager?.ToLowerInvariant(),
                    Enum.Parse<PersonStatus>(status!), alternate));
        }
        Duplicates(rows, r => r.Key, r => r with { Row = 0 }, r => r.Row, "EnterpriseId", "EnterpriseId", errors);
        return (rows, errors);
    }

    // ---- shared ------------------------------------------------------------------------------------------------------

    /// <summary>Identical rows (DuplicateRow) and one key with different values (DuplicateKey): both block the file.</summary>
    private static void Duplicates<T>(List<T> rows, Func<T, string> key, Func<T, T> withoutRow, Func<T, int> rowOf, string keyName, string column, List<UploadError> errors)
    {
        foreach (var g in rows.GroupBy(key).Where(g => g.Count() > 1))
        {
            var first = g.First();
            foreach (var other in g.Skip(1))
                errors.Add(Equals(withoutRow(other), withoutRow(first))
                    ? new UploadError(rowOf(other), column, "DuplicateRow", $"Row {rowOf(other)} repeats row {rowOf(first)}.")
                    : new UploadError(rowOf(other), column, "DuplicateKey", $"Row {rowOf(other)} has the same {keyName} as row {rowOf(first)} with different values."));
        }
    }

    /// <summary>Collects the findings of one row; values are trimmed, control characters and over-long values refused.</summary>
    private sealed class RowErrors(TableRow row, List<UploadError> errors)
    {
        public bool Ok { get; private set; } = true;

        public void Add(string column, string code, string message)
        {
            errors.Add(new UploadError(row.Number, column, code, message));
            Ok = false;
        }

        public string? Optional(string column, int max)
        {
            var v = row[column]?.Trim();
            if (string.IsNullOrEmpty(v)) return null;
            if (v.Any(c => char.IsControl(c))) { Add(column, "ControlCharacters", $"{column} contains control characters."); return null; }
            if (v.Length > max) { Add(column, "TooLong", $"{column} is longer than {max} characters."); return null; }
            return v;
        }

        public string? Required(string column, int max)
        {
            var before = errors.Count;
            var v = Optional(column, max);
            if (v is null && errors.Count == before) Add(column, "Required", $"{column} is required.");
            return v;
        }

        public string? Enum(string column, IReadOnlyList<string> allowed)
        {
            var v = Required(column, 40);
            if (v is null) return null;
            var canonical = allowed.FirstOrDefault(a => string.Equals(a, v, StringComparison.OrdinalIgnoreCase));
            if (canonical is null) Add(column, "InvalidValue", $"{column} \"{v}\" is not one of {string.Join(", ", allowed)}.");
            return canonical;
        }

        public string? Mail(string column, bool required)
        {
            var v = required ? Required(column, 254) : Optional(column, 254);
            if (v is null) return null;
            var plain = v.ToLowerInvariant();
            if (plain.Contains("#ext#", StringComparison.Ordinal) || MailAddresses.Normalize(plain) != plain)
            {
                Add(column, "InvalidMail", $"{column} must be one plain mail address (no display name, no guest #EXT# form).");
                return null;
            }
            return plain;
        }

        /// <summary>yyyy-mm-dd text, or (workbooks only) a date cell, which Excel stores as an OADate serial number (F100).</summary>
        public DateOnly? Date(string column, bool required)
        {
            var v = required ? Required(column, 40) : Optional(column, 40);
            if (v is null) return null;
            if (DateOnly.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
            if (DateTime.TryParseExact(v, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) return DateOnly.FromDateTime(dt);
            if (row.Numeric.Contains(column) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial is >= 1 and < 2958466)
                return DateOnly.FromDateTime(DateTime.FromOADate(Math.Floor(serial)));
            Add(column, "InvalidDate", $"{column} \"{v}\" is not a date: write yyyy-mm-dd (e.g. 2026-11-01) or use an Excel date cell.");
            return null;
        }
    }

    private static string Collapse(string? s) => string.Join(' ', (s ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static string Loose(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    [GeneratedRegex("^[A-Za-z0-9._%+'-]{1,64}@[A-Za-z0-9-]+(\\.[A-Za-z0-9-]+)+$")]
    private static partial Regex EnterpriseIdFormat();
}
