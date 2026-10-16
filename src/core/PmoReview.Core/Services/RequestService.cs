using PmoReview.Core.Common;
using PmoReview.Core.Domain;
using PmoReview.Core.Security;
using PmoReview.Core.Storage;

namespace PmoReview.Core.Services;

/// <summary>
/// Editable request fields. Deliberately has no projectId, createdBy, revision, status or result:
/// those come from the route, the client principal or the server, never the body.
/// Fields may be empty; the review engine reports what is missing.
/// </summary>
public sealed record RequestInput
{
    public string? RequestType { get; init; }
    public string? Role { get; init; }
    public string? Environment { get; init; }
    public string? Manager { get; init; }
    public string? Justification { get; init; }
    public DateOnly? StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public string? JoinerUpn { get; init; }
}

public static class RequestFields
{
    public const string RequestType = "requestType";
    public const string Role = "role";
    public const string Environment = "environment";
    public const string Manager = "manager";
    public const string Justification = "justification";
    public const string StartDate = "startDate";
    public const string EndDate = "endDate";
    public const string JoinerUpn = "joinerUpn";

    public static readonly IReadOnlyList<string> All = [RequestType, Role, Environment, Manager, Justification, StartDate, EndDate, JoinerUpn];
}

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

    public async Task<Versioned<AccessRequest>> CreateAsync(CallerContext caller, string projectId, RequestInput input, CancellationToken ct)
    {
        caller.Require(projectId, Permission.EditRequests);
        var clean = Validate(input);
        var now = clock.UtcNow;
        var doc = new AccessRequest
        {
            Id = Guid.NewGuid().ToString("n"),
            ProjectId = projectId,
            RequestType = clean.RequestType,
            Role = clean.Role,
            Environment = clean.Environment,
            Manager = clean.Manager,
            Justification = clean.Justification,
            StartDate = clean.StartDate,
            EndDate = clean.EndDate,
            JoinerUpn = clean.JoinerUpn,
            CreatedBy = caller.UserId,
            CreatedAt = now,
            UpdatedBy = caller.UserId,
            UpdatedAt = now,
            Revision = 1,
        };
        return await stores.Requests.CreateAsync(doc, ct);
    }

    public async Task<Versioned<AccessRequest>> UpdateAsync(
        CallerContext caller, string projectId, string requestId, RequestInput input, string? ifMatch, CancellationToken ct)
    {
        caller.Require(projectId, Permission.EditRequests);
        if (string.IsNullOrWhiteSpace(ifMatch)) throw AppException.EtagRequired();
        var clean = Validate(input);
        var current = await LoadAsync(projectId, requestId, ct);
        if (current.ETag != ifMatch) throw AppException.Stale();

        var updated = current.Value with
        {
            RequestType = clean.RequestType,
            Role = clean.Role,
            Environment = clean.Environment,
            Manager = clean.Manager,
            Justification = clean.Justification,
            StartDate = clean.StartDate,
            EndDate = clean.EndDate,
            JoinerUpn = clean.JoinerUpn,
            UpdatedBy = caller.UserId,
            UpdatedAt = clock.UtcNow,
            Revision = current.Value.Revision + 1,
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

    private static RequestInput Validate(RequestInput? input)
    {
        if (input is null) throw AppException.Invalid("Request body is required.");
        return new RequestInput
        {
            RequestType = Text(input.RequestType, RequestFields.RequestType, ShortMax),
            Role = Text(input.Role, RequestFields.Role, ShortMax),
            Environment = Text(input.Environment, RequestFields.Environment, ShortMax),
            Manager = Text(input.Manager, RequestFields.Manager, ShortMax),
            Justification = Text(input.Justification, RequestFields.Justification, JustificationMax),
            StartDate = input.StartDate,
            EndDate = input.EndDate,
            JoinerUpn = Upn(input.JoinerUpn),
        };
    }

    /// <summary>Format check only (name@domain.tld). The app never looks the user up.</summary>
    private static string? Upn(string? value)
    {
        var v = Text(value, RequestFields.JoinerUpn, ShortMax);
        if (v is null) return null;
        if (!UpnFormat().IsMatch(v)) throw AppException.Invalid("joinerUpn must be a sign-in name such as sample.joiner@contoso.onmicrosoft.com.");
        return v.ToLowerInvariant();
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9._%+'-]{1,64}@[A-Za-z0-9-]+(\\.[A-Za-z0-9-]+)+$")]
    private static partial System.Text.RegularExpressions.Regex UpnFormat();

    internal static string? Text(string? value, string field, int max)
    {
        if (value is null) return null;
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return null;
        if (trimmed.Length > max) throw AppException.Invalid($"{field} must be at most {max} characters.");
        if (trimmed.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t'))
            throw AppException.Invalid($"{field} contains control characters.");
        return trimmed;
    }
}
