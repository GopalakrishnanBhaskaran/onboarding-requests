using System.Text.RegularExpressions;

namespace PmoReview.Core.Review;

public static class FindingCodes
{
    public const string UntrustedInstruction = "UNTRUSTED_INSTRUCTION";
    public const string ClaimedApproval = "CLAIMED_APPROVAL";
}

/// <summary>
/// Flags request text that tries to steer the reviewer or claims prior approval. Matches fail the REQ_TEXT_CLEAN
/// check only; nothing in request text is ever executed or obeyed. Messages never echo the text.
/// </summary>
public static partial class InjectionDetector
{
    public static IEnumerable<(string Field, string Code)> Scan(IEnumerable<(string Field, string? Text)> fields)
    {
        foreach (var (field, text) in fields)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var normalized = Normalize(text);
            if (ClaimedApproval().IsMatch(normalized)) yield return (field, FindingCodes.ClaimedApproval);
            if (Instruction().IsMatch(normalized)) yield return (field, FindingCodes.UntrustedInstruction);
        }
    }

    /// <summary>Folds look-alike tricks: case, zero-width characters, repeated whitespace.</summary>
    private static string Normalize(string text)
    {
        var noZeroWidth = ZeroWidth().Replace(text, "");
        return Whitespace().Replace(noZeroWidth, " ").ToLowerInvariant();
    }

    [GeneratedRegex(@"[​-‏⁠﻿]")]
    private static partial Regex ZeroWidth();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\b(already|pre-?|previously)\s*approved\b|\bapproved\s+by\b|\bapproval\s+(was|has\s+been|is)\s+(given|granted|done|obtained)\b|\bhas\s+(manager|management)?\s*approval\b|\bsigned[- ]off\b")]
    private static partial Regex ClaimedApproval();

    // Kept narrow on purpose: ordinary onboarding wording ("please grant access to Dev") must not trip it.
    [GeneratedRegex(@"\b(ignore|disregard|forget)\b.{0,30}\b(instruction|rule|prompt|polic|above|check)|\bsystem\s+prompt\b|\byou\s+are\s+now\b|\b(you|assistant|reviewer|model|ai)\s+(must|should|will|shall)\b.{0,40}\b(approve|ready|mark|output|return|respond)|\bpretend\b|\b(mark|set|flag|treat|classify)\s+(this|it|the\s+request|this\s+request|readiness|status)\s+(as|to)\b|\breadiness\s*[:=]|\b(skip|bypass|override)\b.{0,20}\b(check|review|validation|rule)|<\s*/?\s*(script|system|assistant|tool)\b|```|\{\s*""?(readiness|status|role)""?\s*:")]
    private static partial Regex Instruction();
}
