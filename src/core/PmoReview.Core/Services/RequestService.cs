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
    /// <summary>Required when the project lists environments (e.g. Dev, PreProd, Prod).</summary>
    public string? Environment { get; init; }
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
    public const string Environment = "environment";

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
        Environment => "Environment",
        _ => field,
    };
}

/// <summary>Stage 1: intake. Incomplete requests are rejected with exactly what is missing; nothing is saved.</summary>
public sealed partial class RequestService(DataStores stores, IClock clock, WorkflowSettings? settings = null)
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
    /// <param name="bundleId">Set by the server for requests submitted together from one Resource* selection (F1); never from the body.</param>
    /// <param name="simulated">A developer's simulation (Permission.Simulate): set here by the server, never from the body.</param>
    public async Task<Versioned<AccessRequest>> CreateAsync(CallerContext caller, string projectId, RequestInput input, CancellationToken ct, string? bundleId = null,
        bool simulated = false)
    {
        caller.Require(projectId, simulated ? Permission.Simulate : Permission.SubmitRequests);
        var (clean, fixes) = Validate(input);
        var project = (await stores.Projects.GetAsync(projectId, projectId, ct))?.Value;
        var environment = EnvironmentFor(project, input.Environment);
        RefuseBadDomainId(DomainIds.Derive(clean.EnterpriseId!, project?.DomainIdSuffix));
        await RefuseLeaverAsync(projectId, DomainIds.Derive(clean.EnterpriseId!, project?.DomainIdSuffix), ct);
        var now = clock.UtcNow;
        var flow = WorkflowState.Start(now, caller.UserId);
        if (fixes.Count > 0) flow = flow.Log(now, WorkflowState.AgentActor, Workflow.WorkflowAgent.IntakeFormatStep, $"Auto-fixed format: {string.Join(", ", fixes)}.");
        // Live simulation (Gokul, 7 Nov 2026): decided here by the server only, and re-checked before every agent write.
        var liveRefused = simulated ? (settings is null ? "live simulations are switched off" : LiveSimulation.Refused(settings, project, environment, clean.Guest ?? false)) : null;
        var live = simulated && liveRefused is null;
        if (live)
            flow = flow.Log(now, caller.UserId, "simulation", $"LIVE SIMULATION by {caller.DisplayName}: the developer acts for every stage, and the AI agent creates the user, group and membership and assigns the roles in Entra and Azure for real (same switches, allow-lists and checks as a real request). Mails go out as for a real request. Clean up with Offboard user.");
        else if (simulated)
            flow = flow.Log(now, caller.UserId, "simulation", $"SIMULATION by {caller.DisplayName}: nothing is created in Entra or Azure, mails stay in the app, and the developer acts for every stage" +
                (settings?.SimulationLiveWrites == true ? $" (not live: {liveRefused})." : "."));
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
            Environment = environment,
            Justification = clean.Justification,
            Owner = clean.Owner,
            Guest = clean.Guest ?? false,
            BundleId = bundleId,
            Simulated = simulated,
            LiveWrites = live,
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

    /// <summary>The domain-specific ID the request would create must be one Entra accepts (7 Nov 2026).</summary>
    private static void RefuseBadDomainId(string domainId)
    {
        if (DomainIds.EntraProblem(domainId) is { } why)
            throw AppException.Invalid($"The domain-specific ID {domainId} cannot be created in Entra: {why}. Correct the Enterprise ID.");
    }

    /// <summary>
    /// v1.1 F5: a person the people inventory marks Leaver (offboarded) gets no new access: a new or corrected request is
    /// refused until the Data Steward marks the person Active again (a rehire).
    /// </summary>
    private async Task RefuseLeaverAsync(string projectId, string domainId, CancellationToken ct)
    {
        var person = (await stores.PeopleInventory.GetAsync(projectId, Person.MakeId(domainId), ct))?.Value;
        if (person is { Status: PersonStatus.Leaver, Retired: false })
            throw AppException.Conflict("person_is_leaver",
                $"{domainId} is marked as a leaver (offboarded) in the people inventory. A rehire is marked Active again by the Data Steward first.");
        // Offboarded in this project and not (or no longer) in the people inventory as Active: the same, until a rehire is added.
        if (person is not { Status: PersonStatus.Active, Retired: false }
            && (await stores.OffboardingRuns.ListAsync(projectId, ct)).Any(r => string.Equals(r.Value.EnterpriseId, domainId, StringComparison.OrdinalIgnoreCase)
                                                                             && r.Value.Status is OffboardingStatus.Completed or OffboardingStatus.PartiallyCompleted))
            throw AppException.Conflict("person_is_leaver",
                $"{domainId} was offboarded in this project. A rehire is added to the people inventory as Active by the Data Steward first.");
    }

    /// <summary>Edits a Returned request and submits it again (new revision, back to AI review).</summary>
    public async Task<Versioned<AccessRequest>> ResubmitAsync(
        CallerContext caller, string projectId, string requestId, RequestInput input, string? ifMatch, CancellationToken ct)
    {
        if (!caller.InProject(projectId)) throw AppException.NotFound("Project");
        if (string.IsNullOrWhiteSpace(ifMatch)) throw AppException.EtagRequired();
        var current = await LoadAsync(projectId, requestId, ct);
        WorkflowService.RequireFor(caller, current.Value, Permission.SubmitRequests); // a simulation: the developer resubmits it
        var (clean, fixes) = Validate(input);
        if (current.ETag != ifMatch) throw AppException.Stale();
        var flow = current.Value.Flow ?? throw AppException.Conflict("legacy_request", "This is a v1 request and cannot be changed. Submit a new request.");
        if (flow.Status != RequestStatus.Returned)
            throw AppException.Conflict("not_returned", $"Only a returned request can be changed. This one is {StatusText.Label(flow.Status)}.");

        var project = (await stores.Projects.GetAsync(projectId, projectId, ct))?.Value;
        var environment = EnvironmentFor(project, input.Environment);
        RefuseBadDomainId(DomainIds.Derive(clean.EnterpriseId!, project?.DomainIdSuffix));
        await RefuseLeaverAsync(projectId, DomainIds.Derive(clean.EnterpriseId!, project?.DomainIdSuffix), ct);
        var now = clock.UtcNow;
        var next = flow.To(RequestStatus.Submitted, WaitingOn.Agent, now, caller.UserId, "resubmit", "Request corrected and submitted again.");
        if (fixes.Count > 0) next = next.Log(now, WorkflowState.AgentActor, Workflow.WorkflowAgent.IntakeFormatStep, $"Auto-fixed format: {string.Join(", ", fixes)}.");
        var updated = current.Value with
        {
            EnterpriseId = clean.EnterpriseId,
            FirstName = clean.FirstName,
            LastName = clean.LastName,
            DomainId = DomainIds.Derive(clean.EnterpriseId!, project?.DomainIdSuffix),
            Resource = clean.Resource,
            AccessLevel = clean.AccessLevel,
            Environment = environment,
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

    /// <summary>
    /// The project's spelling of the requested environment (dev / ppd / prd are understood). Required when the project
    /// lists environments; a project without environments ignores the field.
    /// </summary>
    public static string? EnvironmentFor(Project? project, string? requested)
    {
        if (project is null || project.Environments.Count == 0) return null;
        var text = Text(requested, RequestFields.Environment, 40);
        if (text is null) throw AppException.Incomplete([RequestFields.Label(RequestFields.Environment)]);
        return Domain.Environments.Canonical(project, text)
               ?? throw AppException.Invalid($"Environment must be one of {string.Join(", ", Domain.Environments.Of(project))} for {Tenants.Label(project)}.");
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
            EnterpriseId = Fix(Text(input.EnterpriseId, RequestFields.EnterpriseId, 200), s => s.Replace(" ", "").ToLowerInvariant(), RequestFields.EnterpriseId, fixes),
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

        // A mail address only (22 Oct 2026): notifications and — with the agent TAP — the sign-in details go there.
        if (!EnterpriseMailFormat().IsMatch(clean.EnterpriseId!))
            throw AppException.Invalid("Enterprise ID must be the joiner's enterprise mail address, e.g. jane.doe@company.com: notifications and the sign-in details are sent there.");
        // The form's justification template marks what to fill in as [ … ]: an unfilled template is not a justification.
        if (TemplatePlaceholder().IsMatch(clean.Justification!))
            throw AppException.Invalid("Justification: replace every [ … ] of the template with your own details before submitting.");
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

    // 7 Nov 2026: dots only between characters (no dot first, last or twice before '@'; RFC 5322 dot-atom), at most 64
    // characters before '@'. A dot right before '@' was accepted and gave a domain ID Entra refuses (VERIFIED_FACTS F136).
    [GeneratedRegex("^(?=[^@]{1,64}@)[a-z0-9][a-z0-9_'+-]*(\\.[a-z0-9_'+-]+)*@[a-z0-9-]+(\\.[a-z0-9-]+)+$")]
    private static partial Regex EnterpriseMailFormat();

    [GeneratedRegex(@"^[\p{L}][\p{L} .'-]{0,99}$")]
    private static partial Regex NameFormat();

    /// <summary>A placeholder of the form's justification template (src/web/src/lib/flow.ts, same pattern).</summary>
    [GeneratedRegex(@"\[[a-z][a-z0-9 ,./()-]{1,160}\]")]
    public static partial Regex TemplatePlaceholder();

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

public static partial class DomainIds
{
    /// <summary>
    /// Domain-specific ID = normalised Enterprise ID (the part before '@' of an enterprise mail address, without
    /// characters a sign-in name cannot hold) + the project's domain suffix (e.g. "@contoso.onmicrosoft.com").
    /// </summary>
    public static string Derive(string enterpriseId, string? suffix)
    {
        var id = enterpriseId.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(suffix)) return id;
        var local = id.Contains('@') ? UpnUnsafe().Replace(id[..id.IndexOf('@')], "") : id;
        return local + suffix.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Why Entra would refuse <paramref name="domainId"/> as a user principal name, or null (VERIFIED_FACTS F136, Learn:
    /// "Can't contain a period character '.' immediately preceding the '@' symbol"; at most 64 characters before '@').
    /// Leading and doubled dots are refused too (not a valid mail address either).
    /// </summary>
    public static string? EntraProblem(string domainId)
    {
        var at = domainId.IndexOf('@');
        var local = at < 0 ? domainId : domainId[..at];
        if (local.Length == 0) return "the part before '@' is empty";
        if (local.Length > 64) return "the part before '@' is longer than 64 characters";
        if (local.EndsWith('.')) return "it ends with a dot right before '@', which Entra does not allow";
        if (local.StartsWith('.') || local.Contains("..", StringComparison.Ordinal)) return "it starts with a dot or has two dots in a row";
        return null;
    }

    /// <summary>The Enterprise ID when it is a mail address (where the joiner is notified), else null.</summary>
    public static string? MailOf(string? enterpriseId) =>
        enterpriseId is not null && enterpriseId.Contains('@') ? enterpriseId.Trim().ToLowerInvariant() : null;

    [GeneratedRegex("[^a-z0-9._'-]")]
    private static partial Regex UpnUnsafe();
}
