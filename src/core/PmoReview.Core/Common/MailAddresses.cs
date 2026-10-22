using System.Text.RegularExpressions;
using PmoReview.Core.Security;

namespace PmoReview.Core.Common;

/// <summary>
/// Mail addresses as the app sends to them. One rule for the contact inventory (typed or pasted from Outlook), the
/// request's people (sign-in names) and every notification: a plain, lower-case address, or nothing.
/// </summary>
public static partial class MailAddresses
{
    /// <summary>Most addresses one team list may hold (ACS sends one mail to at most 50 recipients, F70).</summary>
    public const int MaxPerList = 50;

    /// <summary>
    /// The plain address, or null when <paramref name="text"/> is not one. Accepts <c>Name &lt;a@b.com&gt;</c> (Outlook),
    /// <c>mailto:</c>, and the guest sign-in forms <c>a_b.com#EXT#@tenant…</c> and <c>live.com#a@b.com</c>, which name the
    /// same mailbox as <c>a@b.com</c>.
    /// </summary>
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        var lt = t.LastIndexOf('<');
        if (lt >= 0 && t.EndsWith('>')) t = t[(lt + 1)..^1].Trim();
        if (t.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) t = t[7..];
        t = CallerContext.SignInKey(t.Trim('"', '\'', ' '));
        return t.Length <= 254 && Address().IsMatch(t) ? t : null;
    }

    /// <summary>
    /// A team list as typed: addresses separated by semicolons (also new lines, or commas between bare addresses). Returns
    /// the addresses (normalised, de-duplicated, in order) and every entry that is not an address, as typed.
    /// </summary>
    public static (IReadOnlyList<string> Valid, IReadOnlyList<string> Invalid) ParseList(string? text)
    {
        var valid = new List<string>();
        var invalid = new List<string>();
        foreach (var part in Split(text ?? ""))
        {
            if (Normalize(part) is { } a)
            {
                if (!valid.Contains(a, StringComparer.Ordinal)) valid.Add(a);
            }
            else invalid.Add(part);
        }
        return (valid, invalid);
    }

    /// <summary>The list as the page shows it: <c>a@b.com; c@d.com</c>.</summary>
    public static string Join(IEnumerable<string> addresses) => string.Join("; ", addresses);

    private static IEnumerable<string> Split(string text)
    {
        foreach (var raw in text.Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // "Doe, Jane <jane@x.com>" is one entry (Outlook's display name); "a@x.com, b@y.com" is two.
            if (raw.Contains('<') || !raw.Contains(',')) yield return raw;
            else
                foreach (var p in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) yield return p;
        }
    }

    [GeneratedRegex(@"^[a-z0-9.!#$%&'*+/=?^_`{|}~-]+@[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+$")]
    private static partial Regex Address();
}
