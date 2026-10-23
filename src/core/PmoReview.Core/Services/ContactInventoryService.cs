using PmoReview.Core.Common;
using PmoReview.Core.Domain;
using PmoReview.Core.Security;
using PmoReview.Core.Storage;
using PmoReview.Core.Workflow;

namespace PmoReview.Core.Services;

/// <summary>
/// One project's contact inventory as typed on the page: each team's mail addresses separated by semicolons (Outlook's
/// "Name &lt;address&gt;; …" pastes too). Managers are one address each. Null keeps a field as it is.
/// </summary>
public sealed record ContactInventoryInput
{
    public string? Security { get; init; }
    public string? IdTeam { get; init; }
    public string? RoleTeam { get; init; }
    public string? Pmo { get; init; }
    public string? Operators { get; init; }
    public string? SecurityManager { get; init; }
    public string? IdTeamManager { get; init; }
    public string? RoleTeamManager { get; init; }
    public string? OwnerManagerFallback { get; init; }
}

/// <param name="Gaps">What will not be mailed as things stand (e.g. no Security address), in plain words.</param>
public sealed record ContactInventory(string ProjectId, string? TenantName, ProjectContacts Contacts, int Version, string? UpdatedBy,
    DateTimeOffset? UpdatedAt, bool CanEdit, IReadOnlyList<string> Gaps);

/// <summary>
/// The contact inventory: where every team mail of a project goes (Security, ID team, role assignment team, PMO,
/// Operators, the escalation managers). Kept on the project document, so it needs no new container. Everyone in the
/// project reads it; PMO and Operators change it (If-Match). Every change is versioned and mailed to Security and PMO —
/// the lists before and after — so nobody can quietly take Security off the approval mails. The approver checks never
/// use these addresses: who may approve stays the app roles (<c>userAccess</c>).
/// </summary>
public sealed class ContactInventoryService(DataStores stores, Notifier notifier, IClock clock)
{
    /// <summary>The teams a request cannot do without: approval requests and team tasks go to them.</summary>
    public static readonly IReadOnlyList<string> Required = ["security", "idTeam", "roleTeam"];

    public async Task<Versioned<ContactInventory>> GetAsync(CallerContext caller, string projectId, CancellationToken ct)
    {
        caller.Require(projectId, Permission.ViewContacts);
        var p = await stores.Projects.GetAsync(projectId, projectId, ct) ?? throw AppException.NotFound("Project");
        return new(View(p.Value, caller), p.ETag);
    }

    public async Task<Versioned<ContactInventory>> UpdateAsync(CallerContext caller, string projectId, ContactInventoryInput input, string? ifMatch, CancellationToken ct)
    {
        caller.Require(projectId, Permission.EditContacts);
        if (string.IsNullOrWhiteSpace(ifMatch)) throw AppException.EtagRequired();
        var current = await stores.Projects.GetAsync(projectId, projectId, ct) ?? throw AppException.NotFound("Project");
        if (current.ETag != ifMatch) throw AppException.Stale();
        var before = current.Value.Contacts;
        var problems = new List<string>();

        IReadOnlyList<string> List(string? text, IReadOnlyList<string> kept, string label, bool required)
        {
            if (text is null) return kept;
            var (valid, invalid) = MailAddresses.ParseList(text);
            problems.AddRange(invalid.Select(x => $"{label}: '{Shown(x)}' is not a mail address"));
            if (valid.Count > MailAddresses.MaxPerList) problems.Add($"{label}: at most {MailAddresses.MaxPerList} addresses (a mail-enabled group counts as one)");
            if (required && valid.Count == 0 && invalid.Count == 0) problems.Add($"{label}: at least one address is needed (approval requests or team tasks go here)");
            return valid;
        }

        string? One(string? text, string? kept, string label)
        {
            if (text is null) return kept;
            if (string.IsNullOrWhiteSpace(text)) return null;
            var (valid, invalid) = MailAddresses.ParseList(text);
            if (invalid.Count > 0 || valid.Count != 1) problems.Add($"{label}: one mail address");
            return valid.FirstOrDefault();
        }

        var next = new ProjectContacts
        {
            Security = List(input.Security, before.Security, "Security", true),
            IdTeam = List(input.IdTeam, before.IdTeam, "ID team", true),
            RoleTeam = List(input.RoleTeam, before.RoleTeam, "Role assignment team", true),
            Pmo = List(input.Pmo, before.Pmo, "PMO", false),
            Operators = List(input.Operators, before.Operators, "Operators", false),
            SecurityManager = One(input.SecurityManager, before.SecurityManager, "Security manager"),
            IdTeamManager = One(input.IdTeamManager, before.IdTeamManager, "ID team manager"),
            RoleTeamManager = One(input.RoleTeamManager, before.RoleTeamManager, "Role assignment team manager"),
            OwnerManagerFallback = One(input.OwnerManagerFallback, before.OwnerManagerFallback, "Owner's manager (fallback)"),
        };
        if (problems.Count > 0)
            throw new AppException(ErrorKind.Validation, "invalid_contacts", $"The contact inventory was not saved: {problems.Count} problem(s).", problems);

        var changes = Changes(before, next);
        if (changes.Count == 0) return new(View(current.Value, caller), current.ETag);
        var by = string.IsNullOrWhiteSpace(caller.DisplayName) ? caller.UserId : caller.DisplayName;
        var now = clock.UtcNow;
        var saved = current.Value with { Contacts = next, ContactsVersion = current.Value.ContactsVersion + 1, ContactsUpdatedBy = by, ContactsUpdatedAt = now };
        Versioned<Project> stored;
        try
        {
            stored = await stores.Projects.ReplaceAsync(saved, ifMatch, ct);
        }
        catch (ConcurrencyException)
        {
            throw AppException.Stale();
        }

        // Before and after, so a removed Security or PMO address learns of it too. A Security alert (where approval requests
        // go changed), not a kind of its own: the previous release still reads it after a rollback.
        var to = before.Security.Concat(next.Security).Concat(before.Pmo).Concat(next.Pmo).Cast<string?>();
        var name = saved.TenantName ?? saved.Name;
        await notifier.EnqueueAsync(projectId, null, $"contacts:v{saved.ContactsVersion}", NotificationKind.SecurityAlert, to,
            $"Contact inventory changed: {name} (version {saved.ContactsVersion})",
            $"{by} changed the contact inventory of {name} on {Messages.Time(now)}. Notifications for this project go to the new addresses from now on.\n\n" +
            $"{Messages.Bullets(changes)}\n\nIf you did not expect this, check the Contact inventory page. Who may approve does not change: that stays the app roles.", ct);
        return new(View(stored.Value, caller), stored.ETag);
    }

    private static ContactInventory View(Project p, CallerContext caller) => new(p.ProjectId, p.TenantName, p.Contacts, p.ContactsVersion, p.ContactsUpdatedBy,
        p.ContactsUpdatedAt, caller.Has(p.ProjectId, Permission.EditContacts), Gaps(p.Contacts));

    /// <summary>What the project would not mail as things stand.</summary>
    public static IReadOnlyList<string> Gaps(ProjectContacts c)
    {
        var gaps = new List<string>();
        if (c.Security.Count == 0) gaps.Add("No Security address: Security approval requests, alerts and summaries are not mailed.");
        if (c.IdTeam.Count == 0) gaps.Add("No ID team address: ID team tasks are not mailed.");
        if (c.RoleTeam.Count == 0) gaps.Add("No role assignment team address: role tasks are not mailed.");
        if (c.Pmo.Count == 0) gaps.Add("No PMO address: returned requests and completion mails do not reach PMO.");
        if (c.Operators.Count == 0) gaps.Add("No Operators address: developer reports and stuck-request reminders are not mailed.");
        return gaps;
    }

    private static List<string> Changes(ProjectContacts a, ProjectContacts b)
    {
        var changes = new List<string>();
        void ListChange(string label, IReadOnlyList<string> x, IReadOnlyList<string> y)
        {
            var added = y.Except(x).ToList();
            var removed = x.Except(y).ToList();
            if (added.Count + removed.Count == 0) return;
            changes.Add($"{label}: {(added.Count > 0 ? $"added {MailAddresses.Join(added)}" : "")}{(added.Count > 0 && removed.Count > 0 ? "; " : "")}" +
                        $"{(removed.Count > 0 ? $"removed {MailAddresses.Join(removed)}" : "")}");
        }
        void OneChange(string label, string? x, string? y)
        {
            if (!string.Equals(x, y, StringComparison.Ordinal)) changes.Add($"{label}: {x ?? "(none)"} → {y ?? "(none)"}");
        }
        ListChange("Security", a.Security, b.Security);
        ListChange("ID team", a.IdTeam, b.IdTeam);
        ListChange("Role assignment team", a.RoleTeam, b.RoleTeam);
        ListChange("PMO", a.Pmo, b.Pmo);
        ListChange("Operators", a.Operators, b.Operators);
        OneChange("Security manager", a.SecurityManager, b.SecurityManager);
        OneChange("ID team manager", a.IdTeamManager, b.IdTeamManager);
        OneChange("Role assignment team manager", a.RoleTeamManager, b.RoleTeamManager);
        OneChange("Owner's manager (fallback)", a.OwnerManagerFallback, b.OwnerManagerFallback);
        return changes;
    }

    /// <summary>An entry as typed, cut short for the error list.</summary>
    private static string Shown(string x) => x.Length > 80 ? x[..80] + "…" : x;
}
