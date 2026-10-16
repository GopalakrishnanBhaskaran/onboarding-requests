namespace PmoReview.Core.Domain;

public enum AppRole { PMO, Reviewer, Maintainer, Operator }

public enum PublicationStatus { Draft, Published, Retired }

public enum ProcessingStatus { Queued, Running, Completed, Failed, Superseded }

public enum Readiness { NeedsInformation, ReadyForHumanReview, ManualReviewRequired }

public enum DecisionOutcome { Accepted, Corrected, Escalated }

public enum ErrorCategory { Timeout, MalformedResult, Conflict, DependencyUnavailable, Internal }

/// <summary>Deterministic rule kinds a Maintainer can publish.</summary>
public enum RuleKind
{
    /// <summary>Field must have a value.</summary>
    Required,
    /// <summary>Text field must be at least N characters (parameter = N).</summary>
    MinLength,
    /// <summary>endDate - startDate must be at most N days (parameter = N).</summary>
    MaxDurationDays,
    /// <summary>Field must be one of the pipe-separated values (parameter = "a|b|c").</summary>
    AllowedValues,
}
