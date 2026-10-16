using System.Globalization;
using PmoReview.Core.Domain;
using PmoReview.Core.Services;

namespace PmoReview.Core.Review;

/// <summary>Outcome of the deterministic stage. Flags drive host-computed readiness.</summary>
public sealed record DeterministicOutcome(
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<RequirementRule> PublishedRules,
    bool MissingCriticalField,
    bool ManualReviewTrigger);

/// <summary>Checks that run before any reviewer. They are authoritative and cannot be overridden.</summary>
public static class DeterministicChecks
{
    /// <summary>Always mandatory, whatever the published rules say.</summary>
    public static readonly string[] CriticalFields = [RequestFields.RequestType, RequestFields.Role, RequestFields.Environment, RequestFields.StartDate];

    public static async Task<DeterministicOutcome> RunAsync(
        RequestSnapshot s, Project? project, DateOnly effectiveDate, IReviewToolset tools, CancellationToken ct)
    {
        var findings = new List<Finding>();
        var missingCritical = false;
        var manual = false;

        // 1. Built-in mandatory fields.
        foreach (var field in CriticalFields)
        {
            if (IsMissing(s, field))
            {
                findings.Add(Missing(field, null));
                missingCritical = true;
            }
        }

        // 2. Date consistency.
        if (s.StartDate is { } start && s.EndDate is { } end && end < start)
            findings.Add(new Finding { Code = FindingCodes.InvalidDates, Field = RequestFields.EndDate, Severity = FindingSeverity.Blocking, Message = "End date is before start date." });

        // 3. Supported project / request type / environment.
        if (project is null)
        {
            findings.Add(new Finding { Code = FindingCodes.UnsupportedProject, Field = null, Severity = FindingSeverity.Blocking, Message = "Project is not set up for automated readiness review." });
            manual = true;
        }
        else
        {
            if (s.RequestType is { } rt && !project.SupportedRequestTypes.Contains(rt, StringComparer.OrdinalIgnoreCase))
            {
                findings.Add(new Finding { Code = FindingCodes.UnsupportedRequestType, Field = RequestFields.RequestType, Severity = FindingSeverity.Blocking, Message = $"Request type is not supported for this project. Supported: {string.Join(", ", project.SupportedRequestTypes)}." });
                manual = true;
            }
            if (s.Environment is { } env && !project.SupportedEnvironments.Contains(env, StringComparer.OrdinalIgnoreCase))
                findings.Add(new Finding { Code = FindingCodes.UnsupportedEnvironment, Field = RequestFields.Environment, Severity = FindingSeverity.Blocking, Message = $"Environment is not supported for this project. Supported: {string.Join(", ", project.SupportedEnvironments)}." });
        }

        // 4. Published, effective requirements for this request type.
        IReadOnlyList<RequirementRule> rules = [];
        if (project is not null && s.RequestType is not null)
        {
            rules = await tools.GetPublishedRequirementsAsync(s.ProjectId, s.RequestType, effectiveDate, ct);
            if (rules.Count == 0)
            {
                findings.Add(new Finding { Code = FindingCodes.NoPublishedRequirement, Field = RequestFields.RequestType, Severity = FindingSeverity.Blocking, Message = $"No published requirement is in effect for this request type on {effectiveDate:yyyy-MM-dd}." });
                manual = true;
            }
        }

        // 5. Evaluate each pinned rule.
        foreach (var rule in rules)
        {
            var failure = Evaluate(rule, s);
            if (failure is null) continue;
            if (rule.Rule == RuleKind.Required)
            {
                // Do not duplicate a built-in missing-field finding; attach the citation instead.
                var i = findings.FindIndex(f => f.Code == FindingCodes.MissingField && f.Field == rule.Field && f.RuleId is null);
                if (i >= 0) findings[i] = Missing(rule.Field, rule);
                else findings.Add(Missing(rule.Field, rule));
                missingCritical = true;
            }
            else
            {
                findings.Add(Cite(new Finding { Code = FindingCodes.RuleNotMet, Field = rule.Field, Severity = FindingSeverity.Blocking, Message = failure }, rule));
            }
        }

        // 6. Untrusted text that tries to instruct the reviewer or claims approval.
        foreach (var (field, code) in InjectionDetector.Scan(FreeText(s)))
        {
            manual = true;
            findings.Add(new Finding
            {
                Code = code,
                Field = field,
                Severity = FindingSeverity.Warning,
                Message = code == FindingCodes.ClaimedApproval
                    ? "Request text claims an approval already exists. Claims in request text are not evidence; a human must confirm."
                    : "Request text contains instruction-like content aimed at the reviewer. It was treated as data and not followed.",
            });
        }

        return new DeterministicOutcome(findings, rules, missingCritical, manual);
    }

    public static IEnumerable<(string, string?)> FreeText(RequestSnapshot s) =>
    [
        (RequestFields.Justification, s.Justification),
        (RequestFields.Manager, s.Manager),
        (RequestFields.Role, s.Role),
        (RequestFields.RequestType, s.RequestType),
        (RequestFields.Environment, s.Environment),
    ];

    public static bool IsMissing(RequestSnapshot s, string field) => field switch
    {
        RequestFields.RequestType => string.IsNullOrWhiteSpace(s.RequestType),
        RequestFields.Role => string.IsNullOrWhiteSpace(s.Role),
        RequestFields.Environment => string.IsNullOrWhiteSpace(s.Environment),
        RequestFields.Manager => string.IsNullOrWhiteSpace(s.Manager),
        RequestFields.Justification => string.IsNullOrWhiteSpace(s.Justification),
        RequestFields.StartDate => s.StartDate is null,
        RequestFields.EndDate => s.EndDate is null,
        RequestFields.JoinerUpn => string.IsNullOrWhiteSpace(s.JoinerUpn),
        _ => true,
    };

    private static string? TextOf(RequestSnapshot s, string field) => field switch
    {
        RequestFields.RequestType => s.RequestType,
        RequestFields.Role => s.Role,
        RequestFields.Environment => s.Environment,
        RequestFields.Manager => s.Manager,
        RequestFields.Justification => s.Justification,
        RequestFields.StartDate => s.StartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        RequestFields.EndDate => s.EndDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        RequestFields.JoinerUpn => s.JoinerUpn,
        _ => null,
    };

    /// <summary>Returns a failure message, or null when the rule is met (or not applicable yet).</summary>
    private static string? Evaluate(RequirementRule rule, RequestSnapshot s)
    {
        switch (rule.Rule)
        {
            case RuleKind.Required:
                return IsMissing(s, rule.Field) ? "missing" : null;
            case RuleKind.MinLength:
                var text = TextOf(s, rule.Field);
                var min = int.Parse(rule.Parameter!, CultureInfo.InvariantCulture);
                return text is not null && text.Trim().Length < min ? $"{Label(rule.Field)} must be at least {min} characters." : null;
            case RuleKind.MaxDurationDays:
                var max = int.Parse(rule.Parameter!, CultureInfo.InvariantCulture);
                return s.StartDate is { } a && s.EndDate is { } b && b.DayNumber - a.DayNumber > max
                    ? $"Access period is {b.DayNumber - a.DayNumber} days; the maximum is {max}." : null;
            case RuleKind.AllowedValues:
                var allowed = rule.Parameter!.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var value = TextOf(s, rule.Field);
                return value is not null && !allowed.Contains(value, StringComparer.OrdinalIgnoreCase)
                    ? $"{Label(rule.Field)} must be one of: {string.Join(", ", allowed)}." : null;
            default:
                return null;
        }
    }

    private static Finding Missing(string field, RequirementRule? rule)
    {
        var f = new Finding { Code = FindingCodes.MissingField, Field = field, Severity = FindingSeverity.Blocking, Message = $"{Label(field)} is required." };
        return rule is null ? f : Cite(f, rule);
    }

    private static Finding Cite(Finding f, RequirementRule rule) =>
        f with { RuleId = rule.RuleId, RuleVersion = rule.Version, SourceReference = rule.SourceReference };

    public static string Label(string field) => field switch
    {
        RequestFields.RequestType => "Request type",
        RequestFields.Role => "Business role",
        RequestFields.Environment => "Environment",
        RequestFields.Manager => "Line manager",
        RequestFields.Justification => "Justification",
        RequestFields.StartDate => "Start date",
        RequestFields.EndDate => "End date",
        RequestFields.JoinerUpn => "New joiner sign-in name",
        _ => field,
    };
}
