using PmoReview.Core.Common;
using PmoReview.Core.Directory;
using PmoReview.Core.Domain;

namespace PmoReview.Core.Workflow;

/// <summary>
/// Who may receive a Temporary Access Pass. Gokul, 2 Nov 2026: the Enterprise ID entered on the request — the joiner's
/// enterprise mail, which the named Owner and then Security approved on this revision (the approval page and mail say the
/// pass goes there) — unless it is refused (<see cref="FromApprovedRequest"/>: guest form, personal domain, the requester's,
/// the Owner's or an approver's own address, or the people inventory says otherwise). Then, as F2 (v1.1, 30 Oct 2026; root
/// cause in docs/ROOT_CAUSE.md), the people inventory's <see cref="Person.EnterpriseEmail"/> of the account, when it equals
/// that Entra user's <c>mail</c> or <c>userPrincipalName</c>. Never the AlternateEmail, a manager, a redirect or any other
/// body field. Anything else: no mail, an escalation, the ID team hands the pass over.
/// </summary>
public static class TapRecipient
{
    /// <summary>The address to send to, or why nothing may be sent.</summary>
    /// <param name="Source">Where the address came from (trail, ID-team report, audit): <see cref="FromInventorySource"/> or <see cref="FromRequestSource"/>.</param>
    public sealed record Check(string? To, string? Reason, string? Source = null)
    {
        public bool Ok => To is not null;
        public static Check Refuse(string reason) => new(null, reason);
    }

    public const string FromInventorySource = "people inventory, checked against Entra";
    public const string FromRequestSource = "the Enterprise ID on the request, approved by the Owner and Security";

    /// <summary>Consumer mail domains: never a TAP recipient (an enterprise account's pass goes to an enterprise mailbox).</summary>
    public static readonly IReadOnlySet<string> PersonalDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "gmail.com", "googlemail.com", "outlook.com", "hotmail.com", "live.com", "msn.com", "yahoo.com", "ymail.com", "icloud.com",
        "me.com", "mac.com", "aol.com", "proton.me", "protonmail.com", "gmx.com", "gmx.net", "mail.com", "yandex.com", "zoho.com",
        "rediffmail.com", "qq.com", "163.com",
    };

    /// <summary>
    /// Before the account exists: the inventory alone (person, status, a plain enterprise address in a non-personal domain,
    /// not the manager's, not the alternate one). <paramref name="upn"/> is the account the agent creates (the request's
    /// domain-specific ID): the request's typed Enterprise ID is never used.
    /// </summary>
    public static Check FromInventory(Person? person, string upn)
    {
        if (person is null) return Check.Refuse($"{upn} is not in the people inventory");
        if (person.Retired || person.Status != PersonStatus.Active) return Check.Refuse($"{upn} is not an active person in the people inventory ({(person.Retired ? "retired" : person.Status)})");
        if (!Same(person.EnterpriseId, upn)) return Check.Refuse($"the people inventory names {person.EnterpriseId}, not {upn}");
        var raw = person.EnterpriseEmail?.Trim();
        if (string.IsNullOrEmpty(raw)) return Check.Refuse($"the EnterpriseEmail of {upn} is empty in the people inventory");
        if (Guest(raw)) return Check.Refuse($"the EnterpriseEmail of {upn} is a guest sign-in form (#EXT#), not a mailbox");
        // Strict: a plain address only (no display name, no mailto:, no guest form turned into another mailbox).
        var to = raw.ToLowerInvariant();
        if (MailAddresses.Normalize(to) != to) return Check.Refuse($"the EnterpriseEmail of {upn} is not a plain mail address");
        if (PersonalDomains.Contains(Domain(to))) return Check.Refuse($"the EnterpriseEmail of {upn} is in a personal mail domain ({Domain(to)})");
        if (person.AlternateEmail is { } alt && Same(alt, to)) return Check.Refuse($"the EnterpriseEmail of {upn} equals its AlternateEmail, which is never used for a TAP");
        if (person.ManagerEnterpriseId is { } manager && Same(manager, to)) return Check.Refuse($"the EnterpriseEmail of {upn} is the manager's address");
        return new Check(to, null, FromInventorySource);
    }

    /// <summary>
    /// Gokul, 2 Nov 2026: the request's Enterprise ID, when both current approvals cover it (the Owner and Security saw it as
    /// the recipient of the pass). Refused: no or not a plain address, a guest form, a personal domain, the requester's, the
    /// named Owner's or an approver's own address; a person the inventory lists as not active; an inventory EnterpriseEmail,
    /// AlternateEmail or manager that says otherwise.
    /// </summary>
    public static Check FromApprovedRequest(AccessRequest r, Person? person, string upn)
    {
        var raw = r.EnterpriseId?.Trim();
        if (string.IsNullOrEmpty(raw)) return Check.Refuse("the request has no Enterprise ID");
        if (Guest(raw)) return Check.Refuse("the request's Enterprise ID is a guest sign-in form (#EXT#), not a mailbox");
        var to = raw.ToLowerInvariant();
        if (MailAddresses.Normalize(to) != to) return Check.Refuse("the request's Enterprise ID is not a plain mail address");
        if (PersonalDomains.Contains(Domain(to))) return Check.Refuse($"the request's Enterprise ID is in a personal mail domain ({Domain(to)})");
        var owner = r.Flow?.CurrentApproval(ApprovalStage.Owner);
        var security = r.Flow?.CurrentApproval(ApprovalStage.Security);
        if (owner?.Decision != ApprovalDecision.Approved || security?.Decision != ApprovalDecision.Approved)
            return Check.Refuse("the Owner and Security approvals of this request are not both current");
        foreach (var (who, address) in new[] { ("requester", r.CreatedByName), ("named Owner", r.Owner), ("Owner approver", owner.ByName), ("Security approver", security.ByName) })
            if (SameMailbox(address, to)) return Check.Refuse($"the request's Enterprise ID is the {who}'s own address");
        if (person is not null)
        {
            if (person.Retired || person.Status != PersonStatus.Active)
                return Check.Refuse($"{upn} is not an active person in the people inventory ({(person.Retired ? "retired" : person.Status)})");
            if (!string.IsNullOrWhiteSpace(person.EnterpriseEmail) && !SameMailbox(person.EnterpriseEmail, to))
                return Check.Refuse($"the people inventory names another enterprise mail for {upn} ({Masked(person.EnterpriseEmail.Trim())}) than the request ({Masked(to)})");
            if (SameMailbox(person.AlternateEmail, to)) return Check.Refuse($"the request's Enterprise ID is the AlternateEmail of {upn}, which is never used for a TAP");
            if (SameMailbox(person.ManagerEnterpriseId, to)) return Check.Refuse($"the request's Enterprise ID is the manager's address of {upn}");
        }
        return new Check(to, null, FromRequestSource);
    }

    /// <summary>Before the account exists: the approved request's Enterprise ID, else the inventory.</summary>
    public static Check Before(Person? person, string upn, AccessRequest r)
    {
        var request = FromApprovedRequest(r, person, upn);
        return request.Ok ? request : Either(request, FromInventory(person, upn));
    }

    /// <summary>After the account exists (never a guest or another account): the approved request's Enterprise ID, else the inventory checked against Entra.</summary>
    public static Check After(Person? person, string upn, DirectoryUser user, AccessRequest r)
    {
        if (user.IsGuest || Guest(user.UserPrincipalName)) return Check.Refuse($"{upn} is a guest account in Entra");
        if (!Same(user.UserPrincipalName, upn)) return Check.Refuse($"Entra returned {user.UserPrincipalName}, not {upn}");
        var request = FromApprovedRequest(r, person, upn);
        return request.Ok ? request : Either(request, CrossCheck(person, upn, user));
    }

    private static Check Either(Check request, Check inventory) =>
        inventory.Ok ? inventory : Check.Refuse($"{request.Reason}; and {inventory.Reason}");

    /// <summary>After the account exists: the inventory address must be the Entra user's own <c>mail</c> or UPN (F95).</summary>
    public static Check CrossCheck(Person? person, string upn, DirectoryUser user)
    {
        var first = FromInventory(person, upn);
        if (!first.Ok) return first;
        if (user.IsGuest || Guest(user.UserPrincipalName)) return Check.Refuse($"{upn} is a guest account in Entra");
        if (!Same(user.UserPrincipalName, upn)) return Check.Refuse($"Entra returned {user.UserPrincipalName}, not {upn}");
        if (Same(user.Mail, first.To) || Same(user.UserPrincipalName, first.To)) return first;
        return Check.Refuse($"the people inventory's EnterpriseEmail for {upn} ({Masked(first.To!)}) is neither the Entra mail ({(user.Mail is null ? "none" : Masked(user.Mail))}) nor the UPN");
    }

    /// <summary>For the audit and the trail: first letter of the mailbox, then <c>***</c>, then the domain.</summary>
    public static string Masked(string address)
    {
        var at = address.IndexOf('@');
        return at <= 0 ? SecretMasker.Mask : $"{address[0]}***{address[at..].ToLowerInvariant()}";
    }

    private static bool Guest(string? s) => s?.Contains("#EXT#", StringComparison.OrdinalIgnoreCase) == true || s?.StartsWith("live.com#", StringComparison.OrdinalIgnoreCase) == true;
    private static bool Same(string? a, string? b) => a is not null && b is not null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    /// <summary>The same mailbox, also through a display name, mailto: or a guest sign-in form.</summary>
    private static bool SameMailbox(string? a, string? b) => MailAddresses.Normalize(a) is { } x && MailAddresses.Normalize(b) is { } y && string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
    private static string Domain(string address) => address[(address.IndexOf('@') + 1)..];
}
