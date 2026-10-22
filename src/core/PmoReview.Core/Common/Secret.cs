using System.Security.Cryptography;

namespace PmoReview.Core.Common;

/// <summary>
/// A value that must never be stored, logged or shown: an initial password or a Temporary Access Pass the AI agent mails
/// once to the joiner (v2.1 agent TAP). <see cref="ToString"/> is masked, so a record or log line that contains it prints
/// <c>******</c>; only <see cref="Reveal"/> gives the value, and only the mail that is sent (never the stored copy) uses it.
/// </summary>
public sealed class Secret(string value)
{
    public string Reveal() => value;

    public override string ToString() => SecretMasker.Mask;

    private const string PasswordChars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!#$%&*+-=?@^_";

    /// <summary>
    /// A random initial password (16 characters, upper, lower, digit and symbol; no look-alike characters) that Entra
    /// accepts. The user must change it at the first password sign-in.
    /// </summary>
    public static Secret NewPassword()
    {
        while (true)
        {
            var p = RandomNumberGenerator.GetString(PasswordChars, 16);
            if (p.Any(char.IsAsciiLetterUpper) && p.Any(char.IsAsciiLetterLower) && p.Any(char.IsAsciiDigit) && p.Any(c => !char.IsAsciiLetterOrDigit(c)))
                return new Secret(p);
        }
    }
}
