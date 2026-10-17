using System.Text.RegularExpressions;

namespace PmoReview.Core.Common;

/// <summary>
/// Masks secrets before text is stored, logged, sent (mail, Azure DevOps) or put in a model prompt. The app has no field
/// for a Temporary Access Pass; this catches one pasted into free text ("TAP: …", "password=…", tokens, keys).
/// </summary>
public static partial class SecretMasker
{
    public const string Mask = "******";

    public static string? Apply(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var t = Labelled().Replace(text, m => $"{m.Groups["label"].Value}{m.Groups["sep"].Value}{Mask}");
        t = Jwt().Replace(t, Mask);
        t = KeyValueInConnectionString().Replace(t, m => $"{m.Groups["k"].Value}={Mask}");
        t = LongToken().Replace(t, m => IsHex(m.Value) ? m.Value : Mask);
        return t;
    }

    private static bool IsHex(string s) => s.All(char.IsAsciiHexDigit);

    [GeneratedRegex(@"(?<label>\b(?:temporary\s+access\s+pass|tap|pass(?:word|code|phrase)?|pwd|secret|token|api[-_ ]?key|client[-_ ]?secret|otp|pin)\b)(?<sep>\s*(?:[:=]|\bis\b)\s*)(?<value>[^\s,;]+)", RegexOptions.IgnoreCase)]
    private static partial Regex Labelled();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"(?<k>AccountKey|SharedAccessKey|sig|Password)=[^;\s]+", RegexOptions.IgnoreCase)]
    private static partial Regex KeyValueInConnectionString();

    [GeneratedRegex(@"\b[A-Za-z0-9+/]{40,}={0,2}")]
    private static partial Regex LongToken();
}
