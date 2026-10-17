using System.Text.RegularExpressions;
using PmoReview.Core.Directory;
using PmoReview.Core.Domain;
using PmoReview.Core.Services;
using PmoReview.Core.Sources;

namespace PmoReview.Core.Review;

public static class CheckCodes
{
    public const string IntakeComplete = "INTAKE_COMPLETE";
    public const string MapRowExists = "MAP_ROW_EXISTS";
    public const string MapSingleGroupRole = "MAP_SINGLE_GROUP_ROLE";
    public const string MapNoConflict = "MAP_NO_CONFLICT";
    public const string MapGroupName = "MAP_GROUP_NAME";
    public const string MapNotDeprecated = "MAP_NOT_DEPRECATED";
    public const string RespFormat = "RESP_FORMAT";
    public const string RespNoDuplicate = "RESP_NO_DUPLICATE";
    public const string RespNoConflict = "RESP_NO_CONFLICT";
    public const string RespRoleMatch = "RESP_ROLE_MATCH";
    public const string RespJustification = "RESP_JUSTIFICATION";
    public const string ReqLevelJustified = "REQ_LEVEL_JUSTIFIED";
    public const string ReqPrivilegedFlagged = "REQ_PRIVILEGED_FLAGGED";
    public const string ReqTextClean = "REQ_TEXT_CLEAN";
    public const string EntraGroup = "ENTRA_GROUP";
    public const string EntraUser = "ENTRA_USER";
    public const string RiskPrivileged = "RISK_PRIVILEGED";
    public const string RiskGuest = "RISK_GUEST";
    public const string RiskBroadAccess = "RISK_BROAD_ACCESS";
    public const string RiskSensitive = "RISK_SENSITIVE";
    public const string AiReview = "AI_REVIEW";
}

/// <summary>Entra read-only cross-check input. Null = no directory reader in this process (the worker runs it).</summary>
public sealed record CrossCheck(IReadOnlyList<DirectoryGroup> Groups, DirectoryUser? User, string? Error);

/// <summary>The rows for the requested resource and those matching the access level.</summary>
public sealed record RowMatch(IReadOnlyList<MappingRow> ResourceRows, IReadOnlyList<MappingRow> Matches)
{
    public MappingRow? Row => Matches.Count == 1 ? Matches[0] : null;
}

public sealed record HostReview(IReadOnlyList<CheckResult> Checks, IReadOnlyList<string> RiskFlags, MappingAssessment Mapping, IReadOnlyList<ResponseRecord> UserRecords);

/// <summary>
/// Stage 2 host checks, recorded pass/fail one by one. Authoritative: a reviewer model cannot override them.
/// Groups: mapping sheet, response document, request vs mapping, Entra cross-check, risk flags.
/// </summary>
public static partial class ReviewChecks
{
    public static RowMatch Match(RequestSnapshot s, IReadOnlyList<MappingRow> sheet)
    {
        var resourceRows = MappingSheet.ForResource(sheet, s.Resource);
        var matches = resourceRows.Where(r => MappingSheet.Same(r.AccessLevel, s.AccessLevel)).ToList();
        return new RowMatch(resourceRows, matches);
    }

    public static HostReview Evaluate(
        RequestSnapshot s, Project? project, IReadOnlyList<MappingRow> sheet, IReadOnlyList<ResponseRecord> responses,
        RowMatch match, CrossCheck? entra, ReviewPolicy policy, bool skipEntra = false)
    {
        var checks = new List<CheckResult>();
        void Add(string code, string group, CheckStatus status, string detail) => checks.Add(new CheckResult(code, group, status, detail));
        var row = match.Row;
        const string NoRow = "Not checked: the request does not map to exactly one mapping row.";

        // Intake (defensive: Stage 1 already rejects incomplete requests).
        var missing = RequestFields.Required.Where(f => string.IsNullOrWhiteSpace(Field(s, f))).Select(RequestFields.Label).ToList();
        Add(CheckCodes.IntakeComplete, CheckGroups.Intake, missing.Count == 0 ? CheckStatus.Pass : CheckStatus.Fail,
            missing.Count == 0 ? "All intake fields are present." : $"Missing: {string.Join(", ", missing)}.");

        // 1. Mapping sheet.
        if (match.Matches.Count == 0)
        {
            var levels = match.ResourceRows.Select(r => r.AccessLevel).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Add(CheckCodes.MapRowExists, CheckGroups.MappingSheet, CheckStatus.Fail, levels.Count == 0
                ? $"The mapping sheet has no row for resource '{s.Resource}'."
                : $"The mapping sheet has no row for resource '{s.Resource}' with access level '{s.AccessLevel}'. Levels on the sheet: {string.Join(", ", levels)}.");
        }
        else
        {
            Add(CheckCodes.MapRowExists, CheckGroups.MappingSheet, CheckStatus.Pass,
                $"Found {Rows(match.Matches)} for '{s.Resource}' / '{s.AccessLevel}'.");
        }

        if (match.Matches.Count > 1)
        {
            var distinct = match.Matches.Select(r => (G: r.EntraGroup.ToUpperInvariant(), R: r.Role.ToUpperInvariant())).Distinct().Count();
            Add(CheckCodes.MapSingleGroupRole, CheckGroups.MappingSheet, CheckStatus.Fail, distinct == 1
                ? $"Duplicate rows for this resource and access level: {Rows(match.Matches)}. PMO must keep one."
                : $"Conflicting rows map this resource and access level to different groups or roles: {string.Join("; ", match.Matches.Select(r => $"{r.RowId} → {r.EntraGroup} / {r.Role}"))}.");
        }
        else if (row is not null)
        {
            Add(CheckCodes.MapSingleGroupRole, CheckGroups.MappingSheet, CheckStatus.Pass, $"Row {row.RowId} maps to exactly one group ({row.EntraGroup}) and one role ({row.Role}).");
        }
        else
        {
            Add(CheckCodes.MapSingleGroupRole, CheckGroups.MappingSheet, CheckStatus.NotApplicable, NoRow);
        }

        if (row is null)
        {
            Add(CheckCodes.MapNoConflict, CheckGroups.MappingSheet, CheckStatus.NotApplicable, NoRow);
            Add(CheckCodes.MapGroupName, CheckGroups.MappingSheet, CheckStatus.NotApplicable, NoRow);
            Add(CheckCodes.MapNotDeprecated, CheckGroups.MappingSheet, CheckStatus.NotApplicable, NoRow);
        }
        else
        {
            // One group carries one role for one resource/level: another live row on the same group is a conflict.
            var sharing = sheet.Where(r => r.RowId != row.RowId && !r.Deprecated && MappingSheet.Same(r.EntraGroup, row.EntraGroup)).ToList();
            Add(CheckCodes.MapNoConflict, CheckGroups.MappingSheet, sharing.Count == 0 ? CheckStatus.Pass : CheckStatus.Fail, sharing.Count == 0
                ? $"No other row uses group {row.EntraGroup}."
                : $"Group {row.EntraGroup} is also used by {string.Join("; ", sharing.Select(r => $"{r.RowId} ({r.Resource} / {r.AccessLevel} → {r.Role})"))}. One group must map to one resource, access level and role.");

            var pattern = string.IsNullOrWhiteSpace(project?.GroupNamePattern) ? policy.DefaultGroupNamePattern : project!.GroupNamePattern!;
            var follows = GroupNameFollows(row.EntraGroup, pattern);
            Add(CheckCodes.MapGroupName, CheckGroups.MappingSheet, follows ? CheckStatus.Pass : CheckStatus.Fail, follows
                ? $"Group name {row.EntraGroup} follows the naming convention."
                : $"Group name {row.EntraGroup} does not follow the naming convention ({pattern}).");

            Add(CheckCodes.MapNotDeprecated, CheckGroups.MappingSheet, row.Deprecated ? CheckStatus.Fail : CheckStatus.Pass, row.Deprecated
                ? $"Row {row.RowId} is marked deprecated. PMO must point this resource and access level to a current row."
                : $"Row {row.RowId} is current (not deprecated).");
        }

        // 2. Response document (this user's other records, and this request's own record if it exists).
        var userRecords = responses.Where(r => r.RequestId != s.RequestId && MappingSheet.Same(r.UserId, s.DomainId)).ToList();
        var own = responses.FirstOrDefault(r => r.RequestId == s.RequestId);
        var invalid = userRecords.Append(own).OfType<ResponseRecord>().Where(r => !WellFormed(r)).Select(r => r.Id).ToList();
        Add(CheckCodes.RespFormat, CheckGroups.ResponseDocument, invalid.Count == 0 ? CheckStatus.Pass : CheckStatus.Fail, invalid.Count == 0
            ? $"Response document format is valid ({userRecords.Count + (own is null ? 0 : 1)} record(s) for this user)."
            : $"Response document record(s) {string.Join(", ", invalid)} are incomplete (user, resource, access level, group, role, justification and state are required).");

        var active = userRecords.Where(r => r.IsActive).ToList();
        var duplicates = active.Where(r => row is not null
            ? MappingSheet.Same(r.EntraGroup, row.EntraGroup) && MappingSheet.Same(r.Role, row.Role)
            : MappingSheet.Same(r.Resource, s.Resource) && MappingSheet.Same(r.AccessLevel, s.AccessLevel)).ToList();
        Add(CheckCodes.RespNoDuplicate, CheckGroups.ResponseDocument, duplicates.Count == 0 ? CheckStatus.Pass : CheckStatus.Fail, duplicates.Count == 0
            ? "No existing or pending assignment of the same access for this user."
            : $"This user already has this access: {string.Join("; ", duplicates.Select(Describe))}.");

        var conflicts = active.Except(duplicates).Where(r => MappingSheet.Same(r.Resource, s.Resource)
            && (!MappingSheet.Same(r.AccessLevel, s.AccessLevel) || (row is not null && !MappingSheet.Same(r.Role, row.Role)))).ToList();
        Add(CheckCodes.RespNoConflict, CheckGroups.ResponseDocument, conflicts.Count == 0 ? CheckStatus.Pass : CheckStatus.Fail, conflicts.Count == 0
            ? "No conflicting assignment for this user on this resource."
            : $"Conflicts with existing access on the same resource: {string.Join("; ", conflicts.Select(Describe))}. Change or revoke that assignment first.");

        if (row is null)
        {
            Add(CheckCodes.RespRoleMatch, CheckGroups.ResponseDocument, CheckStatus.NotApplicable, NoRow);
        }
        else if (own is null)
        {
            Add(CheckCodes.RespRoleMatch, CheckGroups.ResponseDocument, CheckStatus.Pass, $"No record for this request yet; the reviewed role {row.Role} is recorded when the review passes.");
        }
        else if (MappingSheet.Same(own.Role, row.Role) && MappingSheet.Same(own.EntraGroup, row.EntraGroup))
        {
            Add(CheckCodes.RespRoleMatch, CheckGroups.ResponseDocument, CheckStatus.Pass, $"Recorded role {own.Role} on {own.EntraGroup} matches the mapping.");
        }
        else if (own.State == ResponseState.Assigned)
        {
            Add(CheckCodes.RespRoleMatch, CheckGroups.ResponseDocument, CheckStatus.Fail,
                $"Assigned {own.Role} on {own.EntraGroup}, but the mapping now says {row.Role} on {row.EntraGroup}. The role assignment team must correct or revoke it.");
        }
        else
        {
            Add(CheckCodes.RespRoleMatch, CheckGroups.ResponseDocument, CheckStatus.Pass,
                $"Recorded {own.Role} on {own.EntraGroup} differs from the mapping ({row.Role} on {row.EntraGroup}); the record is updated and approvals are requested again.");
        }

        var rank = Rank(s.AccessLevel, row, policy);
        var min = policy.MinJustificationByRank[Math.Clamp(rank, 1, policy.MinJustificationByRank.Count) - 1];
        var length = s.Justification?.Trim().Length ?? 0;
        Add(CheckCodes.RespJustification, CheckGroups.ResponseDocument, length >= min ? CheckStatus.Pass : CheckStatus.Fail, length >= min
            ? $"Justification present ({length} characters; '{s.AccessLevel}' needs at least {min})."
            : $"Justification has {length} characters; access level '{s.AccessLevel}' needs at least {min}, with the business reason.");

        // 3. Request vs mapping.
        var levelIssue = LevelNotJustified(rank, s.Justification);
        Add(CheckCodes.ReqLevelJustified, CheckGroups.RequestVsMapping, levelIssue is null ? CheckStatus.Pass : CheckStatus.Fail,
            levelIssue ?? $"Access level '{s.AccessLevel}' is consistent with the justification.");

        var looksPrivileged = row is not null && (rank >= 3 || HasWord(row.Role, policy.PrivilegedWords));
        if (row is null)
            Add(CheckCodes.ReqPrivilegedFlagged, CheckGroups.RequestVsMapping, CheckStatus.NotApplicable, NoRow);
        else
            Add(CheckCodes.ReqPrivilegedFlagged, CheckGroups.RequestVsMapping, looksPrivileged && !row.Privileged ? CheckStatus.Fail : CheckStatus.Pass,
                looksPrivileged && !row.Privileged
                    ? $"Role {row.Role} ('{row.AccessLevel}') is privileged but row {row.RowId} is not flagged Privileged. PMO must flag it."
                    : row.Privileged ? $"Privileged role {row.Role} is flagged on row {row.RowId}." : $"Role {row.Role} is not privileged.");

        var injected = InjectionDetector.Scan(FreeText(s)).ToList();
        Add(CheckCodes.ReqTextClean, CheckGroups.RequestVsMapping, injected.Count == 0 ? CheckStatus.Pass : CheckStatus.Fail, injected.Count == 0
            ? "Request text contains no instructions or approval claims."
            : $"Request text in {string.Join(", ", injected.Select(i => RequestFields.Label(i.Field)).Distinct())} contains instruction-like text or claims an approval. Approvals come only from the Owner and Security in this app; rewrite it.");

        // 4. Entra cross-check (read-only).
        if (row is null)
        {
            Add(CheckCodes.EntraGroup, CheckGroups.EntraCrossCheck, CheckStatus.NotApplicable, NoRow);
        }
        else if (entra is null && skipEntra)
        {
            Add(CheckCodes.EntraGroup, CheckGroups.EntraCrossCheck, CheckStatus.NotApplicable, "Not checked in Entra (local development: no directory reader).");
        }
        else if (entra is null)
        {
            Add(CheckCodes.EntraGroup, CheckGroups.EntraCrossCheck, CheckStatus.Pending, "The read-only Entra check runs on the background agent within a few minutes.");
        }
        else if (entra.Error is not null)
        {
            Add(CheckCodes.EntraGroup, CheckGroups.EntraCrossCheck, CheckStatus.Pending, $"Entra could not be read ({entra.Error}); the agent retries.");
        }
        else
        {
            var (status, detail) = GroupCheck(row, entra.Groups);
            Add(CheckCodes.EntraGroup, CheckGroups.EntraCrossCheck, status, detail);
        }

        if (entra is null && skipEntra)
            Add(CheckCodes.EntraUser, CheckGroups.EntraCrossCheck, CheckStatus.NotApplicable, "Not checked in Entra (local development).");
        else if (entra is null || entra.Error is not null)
            Add(CheckCodes.EntraUser, CheckGroups.EntraCrossCheck, CheckStatus.Pending, "Waiting for the read-only Entra check.");
        else
            Add(CheckCodes.EntraUser, CheckGroups.EntraCrossCheck, CheckStatus.Pass, entra.User is null
                ? $"No user {s.DomainId} in Entra yet; the ID team creates it."
                : $"User {s.DomainId} already exists in Entra ({entra.User.UserType ?? "Member"}{(entra.User.AccountEnabled ? "" : ", disabled")}); the ID team reuses it.");

        // 5. Risk flags: recorded for approvers, never a failure on their own.
        var flags = new List<string>();
        void Flag(string code, bool on, string flag, string onDetail, string offDetail)
        {
            if (on) flags.Add(flag);
            Add(code, CheckGroups.RiskFlags, on ? CheckStatus.Flagged : CheckStatus.Pass, on ? onDetail : offDetail);
        }
        Flag(CheckCodes.RiskPrivileged, row is not null && (row.Privileged || looksPrivileged), RiskFlags.Privileged,
            $"Privileged access: {row?.Role}.", "Not privileged access.");
        var guest = s.Guest || entra?.User?.IsGuest == true;
        Flag(CheckCodes.RiskGuest, guest, RiskFlags.Guest, "Guest (external) user.", "Not a guest user.");
        var broad = active.Count >= policy.BroadAccessThreshold || active.Any(r => r.Privileged);
        Flag(CheckCodes.RiskBroadAccess, broad, RiskFlags.BroadAccess,
            $"User already has {active.Count} active or pending assignment(s){(active.Any(r => r.Privileged) ? ", including privileged access" : "")}.",
            $"User has {active.Count} other active or pending assignment(s).");
        Flag(CheckCodes.RiskSensitive, row?.SensitiveResource == true, RiskFlags.Sensitive, $"Sensitive resource: {s.Resource}.", "Not marked sensitive.");

        var mapping = match.Matches.Count switch
        {
            0 => new MappingAssessment { Status = MappingStatus.NotFound, Explanation = "No mapping row for this resource and access level." },
            1 => new MappingAssessment
            {
                Status = MappingStatus.Matched, RowId = row!.RowId, RowVersion = row.Version, EntraGroup = row.EntraGroup, Role = row.Role,
                RoleScope = row.RoleScope, GroupState = row.GroupState, Explanation = $"Row {row.RowId} v{row.Version} ({row.SourceReference}).",
                GroupExistsInEntra = entra is null || entra.Error is not null ? null : entra.Groups.Count == 1,
            },
            _ => new MappingAssessment { Status = MappingStatus.Ambiguous, Explanation = $"{match.Matches.Count} rows match: {Rows(match.Matches)}." },
        };
        return new HostReview(checks, flags, mapping, userRecords);
    }

    public static (CheckStatus, string) GroupCheck(MappingRow row, IReadOnlyList<DirectoryGroup> groups)
    {
        if (groups.Count > 1) return (CheckStatus.Fail, $"{groups.Count} Entra groups are named {row.EntraGroup}; the name must be unique.");
        if (groups.Count == 0)
            return row.GroupState == GroupState.ToBeCreated
                ? (CheckStatus.Pass, $"Group {row.EntraGroup} does not exist yet; flagged to be created by the ID team.")
                : (CheckStatus.Fail, $"The mapping sheet says group {row.EntraGroup} exists, but Entra has no group with that name. PMO must fix the sheet or flag it 'to be created'.");
        if (!groups[0].SecurityEnabled) return (CheckStatus.Fail, $"Entra group {row.EntraGroup} is not a security group.");
        return row.GroupState == GroupState.ToBeCreated
            ? (CheckStatus.Pass, $"Group {row.EntraGroup} already exists in Entra; the ID team reuses it instead of creating it.")
            : (CheckStatus.Pass, $"Group {row.EntraGroup} exists in Entra (security group).");
    }

    public static bool GroupNameFollows(string name, string pattern)
    {
        try
        {
            return Regex.IsMatch(name, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100));
        }
        catch (Exception e) when (e is ArgumentException or RegexMatchTimeoutException)
        {
            return false; // a broken pattern never passes a name
        }
    }

    /// <summary>1 = read, 2 = write/use (default), 3 = admin/privileged.</summary>
    public static int Rank(string? accessLevel, MappingRow? row, ReviewPolicy policy)
    {
        if (row?.Privileged == true || HasWord(accessLevel, policy.PrivilegedWords)) return 3;
        if (HasWord(accessLevel, WriteLevelWords)) return 2; // "Read/Write" is write
        if (HasWord(accessLevel, policy.ReadWords)) return 1;
        return 2;
    }

    /// <summary>
    /// Rule-only consistency check between the access level and the justification (the AI model, when on, adds its own
    /// view): admin access needs an administrative reason; write access must not describe read-only work.
    /// </summary>
    public static string? LevelNotJustified(int rank, string? justification)
    {
        var j = justification?.ToLowerInvariant() ?? "";
        if (rank >= 3 && !AdminReason().IsMatch(j))
            return "Admin-level access is requested, but the justification gives no administrative task (e.g. manage, configure, deploy, support).";
        if (rank == 2 && ReadOnlyWork().IsMatch(j) && !WriteWork().IsMatch(j))
            return "The justification describes read-only work; request a read access level instead.";
        return null;
    }

    private static readonly string[] WriteLevelWords = ["write", "edit", "contribute", "develop", "developer", "modify", "user", "use", "run"];

    public static IEnumerable<(string, string?)> FreeText(RequestSnapshot s) =>
    [
        (RequestFields.Justification, s.Justification),
        (RequestFields.Resource, s.Resource),
        (RequestFields.AccessLevel, s.AccessLevel),
        (RequestFields.FirstName, s.FirstName),
        (RequestFields.LastName, s.LastName),
    ];

    private static string? Field(RequestSnapshot s, string field) => field switch
    {
        RequestFields.EnterpriseId => s.EnterpriseId,
        RequestFields.FirstName => s.FirstName,
        RequestFields.LastName => s.LastName,
        RequestFields.Resource => s.Resource,
        RequestFields.AccessLevel => s.AccessLevel,
        RequestFields.Justification => s.Justification,
        RequestFields.Owner => s.Owner,
        _ => null,
    };

    private static bool WellFormed(ResponseRecord r) =>
        !string.IsNullOrWhiteSpace(r.UserId) && !string.IsNullOrWhiteSpace(r.Resource) && !string.IsNullOrWhiteSpace(r.AccessLevel)
        && !string.IsNullOrWhiteSpace(r.EntraGroup) && !string.IsNullOrWhiteSpace(r.Role) && !string.IsNullOrWhiteSpace(r.Justification)
        && Enum.IsDefined(r.State);

    private static string Describe(ResponseRecord r) => $"{r.Resource} / {r.AccessLevel} → {r.EntraGroup} / {r.Role} ({r.State}, request {r.RequestId})";

    private static string Rows(IEnumerable<MappingRow> rows) => string.Join(", ", rows.Select(r => $"row {r.RowId}"));

    private static bool HasWord(string? text, IEnumerable<string> words)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = $" {Regex.Replace(text.ToLowerInvariant(), "[^a-z0-9-]+", " ")} ";
        return words.Any(w => t.Contains($" {w.ToLowerInvariant()} ", StringComparison.Ordinal)
                              || (w.Contains(' ') && t.Contains(w.ToLowerInvariant(), StringComparison.Ordinal)));
    }

    [GeneratedRegex(@"\b(admin\w*|manag\w*|configur\w*|maintain\w*|operat\w*|support\w*|deploy\w*|incident\w*|own\w*|govern\w*|provision\w*)\b")]
    private static partial Regex AdminReason();

    [GeneratedRegex(@"\b(view\w*|read\w*|report\w*|monitor\w*|dashboard\w*|look\w*|audit\w*)\b")]
    private static partial Regex ReadOnlyWork();

    [GeneratedRegex(@"\b(build\w*|develop\w*|writ\w*|edit\w*|chang\w*|deploy\w*|load\w*|ingest\w*|creat\w*|updat\w*|run\w*|pipeline\w*|transform\w*|maintain\w*|fix\w*|test\w*)\b")]
    private static partial Regex WriteWork();
}
