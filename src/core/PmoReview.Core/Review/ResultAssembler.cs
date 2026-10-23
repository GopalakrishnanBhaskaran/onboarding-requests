using System.Text.RegularExpressions;
using PmoReview.Core.Common;
using PmoReview.Core.Domain;
using PmoReview.Core.Services;

namespace PmoReview.Core.Review;

/// <summary>
/// The host's final say. Host checks are kept as-is; reviewer output is untrusted: text is cleaned and capped, a
/// "Fail" recommendation or a blocking concern adds a failing AI check, and the outcome is computed here.
/// </summary>
public static partial class ResultAssembler
{
    public const string BaseLimitation = "The AI agent reviews and recommends only. It does not approve, create or assign anything; the Owner and Security decide.";

    public static ReviewResult Assemble(string reviewRunId, RequestSnapshot snapshot, HostReview host, ModelReview review)
    {
        var checks = new List<CheckResult>(host.Checks);
        var limitations = new List<string> { BaseLimitation };

        var findings = review.Findings.Take(20).Select(Clean).OfType<Finding>()
            .DistinctBy(f => (f.Code, f.Field)).ToList();
        if (review.UsedModel)
        {
            var blocking = findings.Where(f => f.Severity == FindingSeverity.Blocking).ToList();
            var fail = blocking.Count > 0 || string.Equals(review.Recommendation, "Fail", StringComparison.OrdinalIgnoreCase);
            checks.Add(new CheckResult(CheckCodes.AiReview, CheckGroups.AiReview, fail ? CheckStatus.Fail : CheckStatus.Pass, fail
                ? "The AI review recommends returning the request: " + (blocking.Count > 0 ? string.Join(" ", blocking.Select(f => f.Message)) : "see the AI notes.")
                : "The AI review found no blocking concern."));
        }

        foreach (var l in review.Limitations.Take(5))
            if (Text(l, 300) is { } t && !limitations.Contains(t)) limitations.Add(t);

        var outcome = checks.Any(c => c.Status == CheckStatus.Fail) ? ReviewOutcome.Fail
            : checks.Any(c => c.Status == CheckStatus.Pending) ? ReviewOutcome.Pending
            : ReviewOutcome.Pass;
        var reasons = checks.Where(c => c.Status == CheckStatus.Fail).Select(c => $"{c.Group}: {c.Detail}").ToList();

        return new ReviewResult
        {
            SchemaVersion = ReviewResult.CurrentSchemaVersion,
            ReviewRunId = reviewRunId,
            RequestRevision = snapshot.Revision,
            Outcome = outcome,
            Summary = ApproverSummary(snapshot, host, checks, outcome, review.UsedModel ? Text(review.Summary, 700) : null),
            Checks = checks,
            RiskFlags = host.RiskFlags,
            Mapping = host.Mapping,
            Findings = findings,
            ReturnReasons = reasons,
            HumanApprovalRequired = true,
            Limitations = limitations.Take(20).ToList(),
        };
    }

    /// <summary>What, why, group, role, risk flags and review results — carried with every approval request.</summary>
    public static string ApproverSummary(RequestSnapshot s, HostReview host, IReadOnlyList<CheckResult> checks, ReviewOutcome outcome, string? aiNotes)
    {
        var m = host.Mapping;
        var group = m.EntraGroup is null ? "not mapped" : m.GroupState == GroupState.ToBeCreated ? $"{m.EntraGroup} (to be created by the ID team)" : m.EntraGroup;
        var counted = checks.Where(c => c.Status is CheckStatus.Pass or CheckStatus.Fail or CheckStatus.Pending).ToList();
        var passed = counted.Count(c => c.Status == CheckStatus.Pass);
        var where = string.Join(", ", new[] { host.Tenant is null ? null : $"tenant {host.Tenant}", s.Environment is null ? null : $"environment {s.Environment}" }.OfType<string>());
        var lines = new List<string>
        {
            $"What: {s.FirstName} {s.LastName} ({s.DomainId}{(s.Guest ? ", guest" : "")}) requests '{s.AccessLevel}' on '{s.Resource}'{(where.Length == 0 ? "" : $" ({where})")}.",
            $"Why: {Text(SecretMasker.Apply(s.Justification), 300) ?? "—"}",
            $"Group: {group}.",
            $"Role: {m.Role ?? "not mapped"}{(m.RoleScope is null ? "" : $" at {m.RoleScope}")}.",
        };
        var grants = host.Row?.Grants() ?? [];
        if (grants.Count > 0)
            lines.Add($"Azure roles on the group ({grants.Count}): " +
                      string.Join("; ", grants.Take(12).Select(g => $"{g.RoleName} on {g.ResourceName ?? g.Scope ?? "scope not resolved"}")) + (grants.Count > 12 ? "; …" : "") + ".");
        if (host.Row?.Notes is { } notes && host.Row.Flag != RowFlag.None)
            lines.Add($"Mapping row note ({host.Row.Flag}): {Text(notes, 300)}");
        lines.Add($"Risk flags: {(host.RiskFlags.Count == 0 ? "none" : string.Join(", ", host.RiskFlags))}.");
        lines.Add($"Review: {outcome} — {passed}/{counted.Count} checks passed{Pending(counted)}.");
        if (aiNotes is not null) lines.Add($"AI notes: {aiNotes}");
        return Text(string.Join("\n", lines), 2500)!;
    }

    private static string Pending(IReadOnlyList<CheckResult> checks)
    {
        var pending = checks.Count(c => c.Status == CheckStatus.Pending);
        var failed = checks.Count(c => c.Status == CheckStatus.Fail);
        return (failed > 0 ? $", {failed} failed" : "") + (pending > 0 ? $", {pending} pending" : "");
    }

    private static Finding? Clean(Finding f)
    {
        var code = f.Code?.Trim().ToUpperInvariant() ?? "";
        if (!Code().IsMatch(code)) return null;
        var message = Text(SecretMasker.Apply(f.Message), 500);
        if (message is null) return null;
        return new Finding
        {
            Code = code,
            Field = f.Field is not null && RequestFields.Required.Contains(f.Field) ? f.Field : null,
            Severity = f.Severity,
            Message = message,
        };
    }

    /// <summary>Strips control characters and caps length. Returns null for empty text.</summary>
    public static string? Text(string? value, int max)
    {
        if (value is null) return null;
        var cleaned = new string(value.Where(c => !char.IsControl(c) || c == '\n').ToArray()).Trim();
        if (cleaned.Length == 0) return null;
        return cleaned.Length <= max ? cleaned : cleaned[..(max - 1)] + "…";
    }

    [GeneratedRegex("^[A-Z][A-Z_]{2,39}$")]
    private static partial Regex Code();
}
