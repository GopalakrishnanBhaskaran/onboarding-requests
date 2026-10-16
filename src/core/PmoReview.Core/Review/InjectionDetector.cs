using System.Text.RegularExpressions;

namespace PmoReview.Core.Review;

public static class FindingCodes
{
    public const string MissingField = "MISSING_FIELD";
    public const string InvalidDates = "INVALID_DATES";
    public const string UnsupportedProject = "UNSUPPORTED_PROJECT";
    public const string UnsupportedRequestType = "UNSUPPORTED_REQUEST_TYPE";
    public const string UnsupportedEnvironment = "UNSUPPORTED_ENVIRONMENT";
    public const string NoPublishedRequirement = "NO_PUBLISHED_REQUIREMENT";
    public const string RuleNotMet = "RULE_NOT_MET";
    public const string UnknownRole = "UNKNOWN_ROLE";
    public const string AmbiguousRole = "AMBIGUOUS_ROLE";
    public const string UntrustedInstruction = "UNTRUSTED_INSTRUCTION";
    public const string ClaimedApproval = "CLAIMED_APPROVAL";
    public const string UnsupportedCitation = "UNSUPPORTED_CITATION";
    public const string ToolScopeViolation = "TOOL_SCOPE_VIOLATION";
}

/// <summary>
/// Flags request text that tries to steer the reviewer or claims prior approval. Matches become
/// findings only; nothing in request text is ever executed or obeyed. Messages never echo the text.
/// </summary>
public static partial class InjectionDetector
{
    public static readonly string[] FreeTextFields = ["justification", "manager", "role", "requestType", "environment"];

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
