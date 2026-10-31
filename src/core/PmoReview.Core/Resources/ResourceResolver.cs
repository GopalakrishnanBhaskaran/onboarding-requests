using System.Globalization;
using System.Text;
using PmoReview.Core.Domain;

namespace PmoReview.Core.Resources;

/// <summary>F1 output contract item (GET /api/resources/resolve).</summary>
public sealed record ResolvedAccess(string GroupObjectId, string GroupName, string AccessLevel, IReadOnlyList<string> Sources, string Status);

/// <summary>F1 output contract: a mapping quality note for the Data Steward.</summary>
public sealed record MappingQualityNote(string VariantName, string CanonicalName, int SheetRow, string Reason);

/// <summary>F1 output contract (GET /api/resources/resolve). Field names and types are the contract: do not add fields.</summary>
public sealed record ResolveResult(
    string TenantId,
    string Environment,
    IReadOnlyList<string> SelectedResources,
    IReadOnlyList<ResolvedAccess> ResolvedAccess,
    IReadOnlyList<MappingQualityNote> MappingQualityIssues,
    string MappingVersion);

public static class ResolveStatus
{
    public const string Auto = "Auto";
    public const string NeedsHumanApproval = "NeedsHumanApproval";
    public const string Unresolved = "Unresolved";
}

/// <summary>What the service knows about a mapping group in Entra (worker reads, or the sheet's object id).</summary>
public sealed record GroupLookup(bool Resolved, string? ObjectId, string Why);

/// <summary>One visible line of the resolved list plus the mapping row behind it (used to create the requests).</summary>
public sealed record ResolvedEntry(ResolvedAccess Access, MappingRow Row, string Key);

public sealed record Resolution(IReadOnlyList<ResolvedEntry> Entries, IReadOnlyList<MappingQualityNote> Notes);

/// <summary>An option that is not offered for this tenant (e.g. another tenant's domain): never resolved.</summary>
public sealed class UnknownResourceException(IReadOnlyList<string> unknown) : Exception($"Not a resource of this tenant: {string.Join(", ", unknown)}")
{
    public IReadOnlyList<string> Unknown { get; } = unknown;
}

/// <summary>
/// F1 Resource* selector (pure, no I/O). Options come only from the rows it is given — the caller passes the signed-in
/// user's tenant rows, filtered server-side. The resolved list is de-duplicated by Entra object id (else the normalised
/// group name); the same group at two levels shows the highest; near-duplicate names (normalised, edit distance ≤ 2) are
/// never merged silently: the canonical group is shown, the variant hidden and a mapping quality note raised; a group
/// that does not resolve to an object id is Unresolved (never granted); Prod above Read is NeedsHumanApproval.
/// </summary>
public static class ResourceResolver
{
    public const int NearDuplicateDistance = 2;

    /// <summary>The I1 normalisation: Unicode compatibility form, lower case, trimmed, spaces collapsed, then "-", "_", "." and spaces removed.</summary>
    public static string Normalise(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var s = Fold(name).Trim().ToLower(CultureInfo.InvariantCulture);
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            if (!char.IsWhiteSpace(ch) && ch is not ('-' or '_' or '.') && !char.IsControl(ch) && CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.Format)
                sb.Append(ch);
        return sb.ToString();
    }

    /// <summary>An option label as shown: trimmed, inner whitespace collapsed (Unicode spaces included).</summary>
    public static string Label(string? value) =>
        string.Join(' ', Fold(value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Full-width ASCII forms (U+FF01–U+FF5E, ideographic space) to ASCII, then the Unicode compatibility form. The apps run
    /// with invariant globalization, where <see cref="string.Normalize(NormalizationForm)"/> leaves non-ASCII text as it is,
    /// so the fold that matters for pasted names is done here explicitly.
    /// </summary>
    public static string Fold(string value)
    {
        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] is >= '\uFF01' and <= '\uFF5E') chars[i] = (char)(chars[i] - 0xFEE0);
            else if (chars[i] == '\u3000') chars[i] = ' ';
        }
        var folded = new string(chars);
        try
        {
            return folded.Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            return folded; // invalid code points: keep the folded text
        }
    }

    /// <summary>
    /// Levenshtein distance; above <paramref name="max"/> it returns max + 1. The common prefix and suffix are skipped and,
    /// with a small max, only the band |i − j| ≤ max is computed (group names share long prefixes such as azr-…-rg-data-).
    /// </summary>
    public static int EditDistance(string a, string b, int max = int.MaxValue)
    {
        var s = 0;
        while (s < a.Length && s < b.Length && a[s] == b[s]) s++;
        int ea = a.Length, eb = b.Length;
        while (ea > s && eb > s && a[ea - 1] == b[eb - 1]) { ea--; eb--; }
        var x = a.AsSpan(s, ea - s);
        var y = b.AsSpan(s, eb - s);
        if (x.Length == 0 || y.Length == 0) return Math.Max(x.Length, y.Length) is var d && d > max ? max + 1 : d;
        if (Math.Abs(x.Length - y.Length) > max) return max + 1;
        var band = max == int.MaxValue ? int.MaxValue : max;
        var big = max == int.MaxValue ? int.MaxValue / 2 : max + 1;
        var prev = new int[y.Length + 1];
        var cur = new int[y.Length + 1];
        for (var j = 0; j <= y.Length; j++) prev[j] = j <= band ? j : big;
        for (var i = 1; i <= x.Length; i++)
        {
            var from = band == int.MaxValue ? 1 : Math.Max(1, i - band);
            var to = band == int.MaxValue ? y.Length : Math.Min(y.Length, i + band);
            cur[0] = i <= band ? i : big;
            if (from > 1) cur[from - 1] = big;
            var rowMin = from == 1 ? cur[0] : big;
            for (var j = from; j <= to; j++)
            {
                var v = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (x[i - 1] == y[j - 1] ? 0 : 1));
                cur[j] = Math.Min(v, big);
                rowMin = Math.Min(rowMin, cur[j]);
            }
            if (to < y.Length) cur[to + 1] = big;
            if (rowMin > max) return max + 1;
            (prev, cur) = (cur, prev);
        }
        var result = prev[y.Length];
        return result > max ? max + 1 : result;
    }

    public static bool NearDuplicate(string a, string b) => EditDistance(Normalise(a), Normalise(b), NearDuplicateDistance) <= NearDuplicateDistance;

    /// <summary>(Domain, ResourceGroupType) of a row; rows imported before v1.1 count as their Resource, type Domain.</summary>
    public static (string Domain, string Type) Classify(MappingRow r) =>
        (Label(string.IsNullOrWhiteSpace(r.Domain) ? r.Resource : r.Domain), ResourceGroupTypes.Canonical(r.ResourceGroupType) ?? ResourceGroupTypes.Domain);

    /// <summary>Rows a requester may be offered: not retired or deprecated, effective today, in the environment (or for every environment).</summary>
    public static IReadOnlyList<MappingRow> Offerable(IEnumerable<MappingRow> rows, string? environment, DateOnly today) =>
        rows.Where(r => !r.Retired && !r.Deprecated && r.Flag != RowFlag.Red
                        && (r.EffectiveFrom is null || r.EffectiveFrom <= today) && (r.EffectiveTo is null || r.EffectiveTo >= today)
                        && (string.IsNullOrWhiteSpace(environment) || r.Environment is null || Environments.Same(r.Environment, environment)))
            .ToList();

    /// <summary>Every distinct Domain, then PH and ACQ when the tenant has such rows, then "All Domains + PH + ACQ".</summary>
    public static IReadOnlyList<string> Options(IEnumerable<MappingRow> offerable)
    {
        var rows = offerable.Select(Classify).ToList();
        var domains = rows.Where(c => c.Type == ResourceGroupTypes.Domain).Select(c => c.Domain)
            .GroupBy(Normalise).Select(g => g.First()).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var options = new List<string>(domains);
        if (rows.Any(c => c.Type == ResourceGroupTypes.Ph)) options.Add(ResourceGroupTypes.Ph);
        if (rows.Any(c => c.Type == ResourceGroupTypes.Acq)) options.Add(ResourceGroupTypes.Acq);
        if (options.Count > 0) options.Add(ResourceGroupTypes.All);
        return options;
    }

    /// <summary>
    /// The selection as a list of offered options: "All" stands for every option; unknown values (another tenant's
    /// domain, free text) throw <see cref="UnknownResourceException"/>. Order follows the options.
    /// </summary>
    public static IReadOnlyList<string> Expand(IEnumerable<string?> selected, IReadOnlyList<string> options)
    {
        var picked = selected.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => Label(s)).ToList();
        var real = options.Where(o => o != ResourceGroupTypes.All).ToList();
        if (picked.Any(p => Normalise(p) == Normalise(ResourceGroupTypes.All))) return real;
        var unknown = picked.Where(p => !real.Any(o => Normalise(o) == Normalise(p))).Distinct().ToList();
        if (unknown.Count > 0) throw new UnknownResourceException(unknown);
        return real.Where(o => picked.Any(p => Normalise(p) == Normalise(o))).ToList();
    }

    /// <summary>Order of levels for "show the highest": the I1 rank; a job-role level ranks as Read when it only reads.</summary>
    public static int LevelRank(MappingRow r)
    {
        var rank = AccessLevels.Rank(r.AccessLevel);
        if (rank >= 0) return rank * 10;
        return ProdGuard.AboveReadInProd("Prod", r.AccessLevel, r.Grants(), r.Privileged) is null ? 0 : 5;
    }

    public static Resolution Resolve(IReadOnlyList<MappingRow> offerable, IReadOnlyList<string> selection, Func<MappingRow, GroupLookup> lookup, string? environment)
    {
        // 1. Rows of the chosen resources, with the options that led to each (each row classified once).
        var wanted = selection.Select(o => (Option: o, Key: Normalise(o))).ToList();
        var hits = new List<(MappingRow Row, string Option, GroupLookup Look)>();
        foreach (var row in offerable.OrderBy(r => r.SheetRow ?? int.MaxValue).ThenBy(r => r.RowId, StringComparer.Ordinal))
        {
            var (domain, type) = Classify(row);
            var domainKey = Normalise(domain);
            GroupLookup? look = null;
            foreach (var (option, key) in wanted)
            {
                var match = option switch
                {
                    ResourceGroupTypes.Ph => type == ResourceGroupTypes.Ph,
                    ResourceGroupTypes.Acq => type == ResourceGroupTypes.Acq,
                    _ => type == ResourceGroupTypes.Domain && domainKey == key,
                };
                if (match) hits.Add((row, option, look ??= lookup(row)));
            }
        }

        // 2. One entry per key: the sheet's object id, else the id Entra resolved for the name, else the normalised name.
        //    The same group spelled the same way (case and spacing aside) via several resources is listed once with every
        //    source; another spelling of the same key is a variant: hidden, never merged silently, noted for the Data Steward.
        string KeyOf(MappingRow r, GroupLookup l) =>
            Guid.TryParse(r.GroupObjectId, out var g) ? g.ToString("D") : l.Resolved && Guid.TryParse(l.ObjectId, out var h) ? h.ToString("D") : "name:" + Normalise(r.EntraGroup);
        string Spelling(MappingRow r) => Label(r.EntraGroup).ToLowerInvariant();
        var notes = new List<MappingQualityNote>();
        var hiddenBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // variant name → name shown instead
        var entries = new List<(string Key, MappingRow Row, GroupLookup Look, List<string> Sources, int Rows)>();
        foreach (var group in hits.GroupBy(h => KeyOf(h.Row, h.Look)))
        {
            var spellings = group.GroupBy(h => Spelling(h.Row)).ToList();
            var canonical = spellings
                .OrderByDescending(s => s.Any(h => Guid.TryParse(h.Row.GroupObjectId, out _)))
                .ThenByDescending(s => s.Select(h => h.Row.RowId).Distinct().Count())
                .ThenBy(s => s.Min(h => h.Row.SheetRow ?? int.MaxValue)).ThenBy(s => s.Key, StringComparer.Ordinal).First();
            foreach (var variant in spellings.Where(s => s.Key != canonical.Key))
            {
                var v = variant.First().Row;
                hiddenBy[Label(v.EntraGroup)] = Label(canonical.First().Row.EntraGroup);
                notes.Add(new MappingQualityNote(Label(v.EntraGroup), Label(canonical.First().Row.EntraGroup), v.SheetRow ?? 0,
                    $"Near-duplicate group name (normalised edit distance {EditDistance(Normalise(v.EntraGroup), Normalise(canonical.First().Row.EntraGroup))}, same group key): " +
                    $"'{Label(v.EntraGroup)}' is hidden, '{Label(canonical.First().Row.EntraGroup)}' is shown. Not merged: correct the mapping sheet."));
            }
            var byLevel = canonical.OrderByDescending(h => LevelRank(h.Row)).ThenBy(h => h.Row.SheetRow ?? int.MaxValue).ThenBy(h => h.Row.RowId, StringComparer.Ordinal).ToList();
            var top = byLevel[0];
            foreach (var l in byLevel.Select(h => h.Row).Where(r => Normalise(r.AccessLevel) != Normalise(top.Row.AccessLevel)).DistinctBy(r => Normalise(r.AccessLevel)))
                notes.Add(new MappingQualityNote(Label(l.EntraGroup), Label(top.Row.EntraGroup), l.SheetRow ?? 0,
                    $"Same group at two levels in {(string.IsNullOrWhiteSpace(environment) ? "this tenant" : environment)}: {Label(top.Row.AccessLevel)} is shown, {Label(l.AccessLevel)} is included."));
            var sources = selection.Where(o => canonical.Any(h => h.Option == o)).ToList();
            entries.Add((group.Key, top.Row, top.Look, sources, canonical.Select(h => h.Row.RowId).Distinct().Count()));
        }

        // 3. Near-duplicate names across different keys: canonical shown (the one that resolves in Entra), variant hidden, a note.
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        var ordered = entries.OrderByDescending(e => e.Look.Resolved).ThenByDescending(e => e.Rows)
            .ThenBy(e => e.Row.SheetRow ?? int.MaxValue).ThenBy(e => e.Row.EntraGroup, StringComparer.Ordinal).ToList();
        var norm = ordered.Select(e => Normalise(e.Row.EntraGroup)).ToArray();
        var noted = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < ordered.Count; i++)
        {
            if (hidden.Contains(ordered[i].Key)) continue;
            for (var j = i + 1; j < ordered.Count; j++)
            {
                if (hidden.Contains(ordered[j].Key) || Math.Abs(norm[i].Length - norm[j].Length) > NearDuplicateDistance) continue;
                var d = EditDistance(norm[i], norm[j], NearDuplicateDistance);
                if (d > NearDuplicateDistance) continue;
                var variant = Label(ordered[j].Row.EntraGroup);
                var shown = Label(ordered[i].Row.EntraGroup);
                if (ordered[i].Look.Resolved && ordered[j].Look.Resolved)
                {
                    if (!noted.Add(ordered[j].Key)) continue; // one note per name is enough (keeps the list linear on big sheets)
                    // Two real groups (different object ids) with similar names, e.g. domain codes "ph" / "hr": neither is
                    // "the one that resolves", so neither is hidden; the Data Steward is told to check the names.
                    notes.Add(new MappingQualityNote(variant, shown, ordered[j].Row.SheetRow ?? 0,
                        $"Similar group names (normalised edit distance {d}) that are two different Entra groups: both are shown. Check the mapping sheet."));
                    continue;
                }
                hidden.Add(ordered[j].Key);
                hiddenBy[variant] = shown;
                notes.Add(new MappingQualityNote(variant, shown, ordered[j].Row.SheetRow ?? 0,
                    $"Near-duplicate group name (normalised edit distance {d}): '{variant}' is hidden, '{shown}' is shown" +
                    $"{(ordered[i].Look.Resolved ? " (its object id resolves in Entra)" : "")}. Not merged: correct the mapping sheet."));
            }
        }
        // A variant of a variant names the group that is finally shown.
        string Shown(string name)
        {
            for (var guard = 0; guard < 16 && hiddenBy.TryGetValue(name, out var next) && !string.Equals(next, name, StringComparison.OrdinalIgnoreCase); guard++) name = next;
            return name;
        }
        notes = notes.Select(n => n with { CanonicalName = Shown(n.CanonicalName) }).ToList();

        // 4. Status of each visible entry.
        var result = new List<ResolvedEntry>();
        foreach (var e in entries.Where(e => !hidden.Contains(e.Key)))
        {
            // An unresolved group has no object id to show (a sheet id that Entra contradicts is not shown either).
            var objectId = !e.Look.Resolved ? "" : Guid.TryParse(e.Row.GroupObjectId, out var g) ? g.ToString("D") : e.Look.ObjectId ?? "";
            var status = !e.Look.Resolved ? ResolveStatus.Unresolved
                : ProdGuard.AboveReadInProd(e.Row, environment) is not null ? ResolveStatus.NeedsHumanApproval
                : ResolveStatus.Auto;
            result.Add(new ResolvedEntry(new ResolvedAccess(objectId, Label(e.Row.EntraGroup), Label(e.Row.AccessLevel), e.Sources, status), e.Row, e.Key));
        }
        return new Resolution(
            result.OrderBy(x => x.Access.GroupName, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Key, StringComparer.Ordinal).ToList(),
            notes.DistinctBy(n => (n.VariantName, n.CanonicalName, n.Reason)).ToList());
    }

}
