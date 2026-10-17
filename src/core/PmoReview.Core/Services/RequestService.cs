using System.Globalization;
using System.Text.RegularExpressions;
using PmoReview.Core.Common;
using PmoReview.Core.Domain;
using PmoReview.Core.Security;
using PmoReview.Core.Storage;
using PmoReview.Core.Workflow;

namespace PmoReview.Core.Services;

/// <summary>
/// Stage 1 intake fields. Deliberately has no projectId, createdBy, revision, domain ID, status or result:
/// those come from the route, the client principal or the server, never the body.
/// </summary>
public sealed record RequestInput
{
    public string? EnterpriseId { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? Resource { get; init; }
    public string? AccessLevel { get; init; }
    public string? Justification { get; init; }
    public string? Owner { get; init; }
    public bool? Guest { get; init; }
}

public static class RequestFields
{
    public const string EnterpriseId = "enterpriseId";
    public const string FirstName = "firstName";
    public const string LastName = "lastName";
    public const string DomainId = "domainId";
    public const string Resource = "resource";
    public const string AccessLevel = "accessLevel";
    public const string Justification = "justification";
    public const string Owner = "owner";

    /// <summary>Required at intake, in form order. The domain-specific ID is derived, not typed.</summary>
    public static readonly IReadOnlyList<string> Required = [EnterpriseId, FirstName, LastName, Resource, AccessLevel, Justification, Owner];

    public static string Label(string field) => field switch
    {
        EnterpriseId => "Enterprise ID",
        FirstName => "First name",
        LastName => "Last name",
        DomainId => "Domain-specific ID",
        Resource => "Resource",
        AccessLevel => "Access level",
        Justification => "Justification",
        Owner => "Owner",
        _ => field,
    };
}

/// <summary>Stage 1: intake. Incomplete requests are rejected with exactly what is missing; nothing is saved.</summary>
public sealed partial class RequestService(DataStores stores, IClock clock)
{
    private const int ShortMax = 200;
    private const int JustificationMax = 2000;

    public async Task<IReadOnlyList<Versioned<AccessRequest>>> ListAsync(CallerContext caller, string projectId, CancellationToken ct)
    {
        caller.Require(projectId, Permission.ViewRequests);
        var all = await stores.Requests.ListAsync(projectId, ct);
        return all.Where(r => r.Value.ProjectId == projectId).OrderByDescending(r => r.Value.UpdatedAt).ToList();
    }

    public async Task<Versioned<AccessRequest>> GetAsync(CallerContext caller, string projectId, string requestId, CancellationToken ct)
    {
        caller.Require(projectId, Permission.ViewRequests);
        return await LoadAsync(projectId, requestId, ct);
    }

    /// <summary>Submits a new request (status Submitted). The caller then starts the AI review.</summary>
    public async Task<Versioned<AccessRequest>> CreateAsync(CallerContext caller, string projectId, RequestInput input, CancellationToken ct)
    {
        caller.Require(projectId, Permission.SubmitRequests);
        var (clean, fixes) = Validate(input);
        var project = (await stores.Projects.GetAsync(projectId, projectId, ct))?.Value;
        var now = clock.UtcNow;
        var flow = WorkflowState.Start(now, caller.UserId);
        if (fixes.Count > 0) flow = flow.Log(now, WorkflowState.AgentActor, "intake-format", $"Auto-fixed format: {string.Join(", ", fixes)}.");
        var doc = new AccessRequest
        {
            Id = Guid.NewGuid().ToString("n"),
            ProjectId = projectId,
            EnterpriseId = clean.EnterpriseId,
            FirstName = clean.FirstName,
            LastName = clean.LastName,
            DomainId = DomainIds.Derive(clean.EnterpriseId!, project?.DomainIdSuffix),
            Resource = clean.Resource,
            AccessLevel = clean.AccessLevel,
            Justification = clean.Justification,
            Owner = clean.Owner,
            Guest = clean.Guest ?? false,
            CreatedBy = caller.UserId,
            CreatedByName = caller.DisplayName,
            CreatedAt = now,
            UpdatedBy = caller.UserId,
            UpdatedAt = now,
            Revision = 1,
            Flow = flow,
        };
        return await stores.Requests.CreateAsync(doc, ct);
    }

    /// <summary>Edits a Returned request and submits it again (new revision, back to AI review).</summary>
    public async Task<Versioned<AccessRequest>> ResubmitAsync(
        CallerContext caller, string projectId, string requestId, RequestInput input, string? ifMatch, CancellationToken ct)
    {
        caller.Require(projectId, Permission.SubmitRequests);
        if (string.IsNullOrWhiteSpace(ifMatch)) throw AppException.EtagRequired();
        var (clean, fixes) = Validate(input);
        var current = await LoadAsync(projectId, requestId, ct);
        if (current.ETag != ifMatch) throw AppException.Stale();
        var flow = current.Value.Flow ?? throw AppException.Conflict("legacy_request", "This is a v1 request and cannot be changed. Submit a new request.");
        if (flow.Status != RequestStatus.Returned)
            throw AppException.Conflict("not_returned", $"Only a returned request can be changed. This one is {StatusText.Label(flow.Status)}.");

        var project = (await stores.Projects.GetAsync(projectId, projectId, ct))?.Value;
        var now = clock.UtcNow;
        var next = flow.To(RequestStatus.Submitted, WaitingOn.Agent, now, caller.UserId, "resubmit", "Request corrected and submitted again.");
        if (fixes.Count > 0) next = next.Log(now, WorkflowState.AgentActor, "intake-format", $"Auto-fixed format: {string.Join(", ", fixes)}.");
        var updated = current.Value with
        {
            EnterpriseId = clean.EnterpriseId,
            FirstName = clean.FirstName,
            LastName = clean.LastName,
            DomainId = DomainIds.Derive(clean.EnterpriseId!, project?.DomainIdSuffix),
            Resource = clean.Resource,
            AccessLevel = clean.AccessLevel,
            Justification = clean.Justification,
            Owner = clean.Owner,
            Guest = clean.Guest ?? false,
            UpdatedBy = caller.UserId,
            UpdatedAt = now,
            Revision = current.Value.Revision + 1,
            Flow = next with { ResumeStatus = null },
        };
        try
        {
            return await stores.Requests.ReplaceAsync(updated, ifMatch, ct);
        }
        catch (ConcurrencyException)
        {
            throw AppException.Stale();
        }
    }

    internal async Task<Versioned<AccessRequest>> LoadAsync(string projectId, string requestId, CancellationToken ct)
    {
        var found = await stores.Requests.GetAsync(projectId, requestId, ct);
        if (found is null || found.Value.ProjectId != projectId) throw AppException.NotFound("Request");
        return found;
    }

    /// <summary>
    /// Cleans the input ("fix ID/name format" auto-fix: trim, single spaces, lower-case IDs, capitalised names) and
    /// rejects an incomplete request listing exactly what is missing.
    /// </summary>
    public static (RequestInput Clean, IReadOnlyList<string> Fixes) Validate(RequestInput? input)
    {
        if (input is null) throw AppException.Invalid("Request body is required.");
        var fixes = new List<string>();
        var clean = new RequestInput
        {
            EnterpriseId = Fix(Text(input.EnterpriseId, RequestFields.EnterpriseId, 64), s => s.Replace(" ", "").ToLowerInvariant(), RequestFields.EnterpriseId, fixes),
            FirstName = Fix(Text(input.FirstName, RequestFields.FirstName, 100), Name, RequestFields.FirstName, fixes),
            LastName = Fix(Text(input.LastName, RequestFields.LastName, 100), Name, RequestFields.LastName, fixes),
            Resource = Fix(Text(input.Resource, RequestFields.Resource, ShortMax), Spaces, RequestFields.Resource, fixes),
            AccessLevel = Fix(Text(input.AccessLevel, RequestFields.AccessLevel, ShortMax), Spaces, RequestFields.AccessLevel, fixes),
            // A secret pasted into free text is masked before it is stored or shown to anyone.
            Justification = SecretMasker.Apply(Text(input.Justification, RequestFields.Justification, JustificationMax)),
            Owner = Fix(Text(input.Owner, RequestFields.Owner, ShortMax), s => s.ToLowerInvariant(), RequestFields.Owner, fixes),
            Guest = input.Guest,
        };

        var missing = RequestFields.Required.Where(f => Value(clean, f) is null).Select(RequestFields.Label).ToList();
        if (missing.Count > 0) throw AppException.Incomplete(missing);

        if (!EnterpriseIdFormat().IsMatch(clean.EnterpriseId!))
            throw AppException.Invalid("Enterprise ID may contain only letters, digits, dot, dash and underscore (2-64 characters).");
        if (!NameFormat().IsMatch(clean.FirstName!) || !NameFormat().IsMatch(clean.LastName!))
            throw AppException.Invalid("First and last name may contain only letters, spaces, dots, hyphens and apostrophes.");
        if (!SignInFormat().IsMatch(clean.Owner!))
            throw AppException.Invalid("Owner must be the Owner's sign-in name, such as owner.name@contoso.com.");
        return (clean, fixes);
    }

    private static string? Value(RequestInput r, string field) => field switch
    {
        RequestFields.EnterpriseId => r.EnterpriseId,
        RequestFields.FirstName => r.FirstName,
        RequestFields.LastName => r.LastName,
        RequestFields.Resource => r.Resource,
        RequestFields.AccessLevel => r.AccessLevel,
        RequestFields.Justification => r.Justification,
        RequestFields.Owner => r.Owner,
        _ => null,
    };

    private static string? Fix(string? value, Func<string, string> fix, string field, List<string> fixes)
    {
        if (value is null) return null;
        var fixedValue = fix(value);
        if (fixedValue != value) fixes.Add(RequestFields.Label(field));
        return fixedValue;
    }

    private static string Spaces(string s) => string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Capitalises names typed all in lower or upper case; mixed case (McDonald, de Vries) is kept.</summary>
    private static string Name(string s)
    {
        var spaced = Spaces(s);
        if (spaced.Any(char.IsLower) && spaced.Any(char.IsUpper)) return spaced;
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(spaced.ToLowerInvariant());
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{1,63}$")]
    private static partial Regex EnterpriseIdFormat();

    [GeneratedRegex(@"^[\p{L}][\p{L} .'-]{0,99}$")]
    private static partial Regex NameFormat();

    [GeneratedRegex("^[A-Za-z0-9._%+'-]{1,64}@[A-Za-z0-9-]+(\\.[A-Za-z0-9-]+)+$")]
    private static partial Regex SignInFormat();

    internal static string? Text(string? value, string field, int max)
    {
        if (value is null) return null;
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return null;
        if (trimmed.Length > max) throw AppException.Invalid($"{RequestFields.Label(field)} must be at most {max} characters.");
        if (trimmed.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t'))
            throw AppException.Invalid($"{RequestFields.Label(field)} contains control characters.");
        return trimmed;
    }
}

public static class DomainIds
{
    /// <summary>Domain-specific ID = normalised Enterprise ID + the project's domain suffix (e.g. "@contoso.onmicrosoft.com").</summary>
    public static string Derive(string enterpriseId, string? suffix)
    {
        var id = enterpriseId.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(suffix) ? id : id + suffix.Trim().ToLowerInvariant();
    }
}
