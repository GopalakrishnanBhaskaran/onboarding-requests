using PmoReview.Core.Common;
using PmoReview.Core.Directory;
using PmoReview.Core.Domain;

namespace PmoReview.Core.Workflow;

/// <summary>
/// F2 (v1.1, 30 Oct 2026; root cause in docs/ROOT_CAUSE.md): who may receive a Temporary Access Pass. Only the people
/// inventory's <see cref="Person.EnterpriseEmail"/> of the person whose Enterprise ID is the account's UPN, and only when it
/// equals that Entra user's <c>mail</c> or <c>userPrincipalName</c>. Never the request body, the AlternateEmail, a manager,
/// a guest form, a personal domain or a redirect. Anything else: no mail, an escalation, the ID team hands the pass over.
/// </summary>
public static class TapRecipient
{
    /// <summary>The address to send to, or why nothing may be sent.</summary>
    public sealed record Check(string? To, string? Reason)
    {
        public bool Ok => To is not null;
        public static Check Refuse(string reason) => new(null, reason);
    }

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
        return new Check(to, null);
    }

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
    private static string Domain(string address) => address[(address.IndexOf('@') + 1)..];
}
