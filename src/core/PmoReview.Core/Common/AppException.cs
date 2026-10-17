namespace PmoReview.Core.Common;

public enum ErrorKind
{
    Unauthenticated,
    Forbidden,
    NotFound,
    Validation,
    Conflict,
    PreconditionFailed,
    PreconditionRequired,
    PayloadTooLarge,
}

/// <summary>
/// Expected, user-safe failure. The message is returned to the caller, so it must never
/// contain request text, connection details or stack information.
/// </summary>
public sealed class AppException(ErrorKind kind, string code, string message, IReadOnlyList<string>? details = null) : Exception(message)
{
    public ErrorKind Kind { get; } = kind;
    public string Code { get; } = code;
    /// <summary>Optional list shown to the caller (e.g. exactly which intake fields are missing).</summary>
    public IReadOnlyList<string>? Details { get; } = details;

    public static AppException NotFound(string what) => new(ErrorKind.NotFound, "not_found", $"{what} not found.");
    public static AppException Forbidden() => new(ErrorKind.Forbidden, "forbidden", "You do not have permission for this action.");
    public static AppException Unauthenticated() => new(ErrorKind.Unauthenticated, "unauthenticated", "Sign-in required.");
    public static AppException Invalid(string message) => new(ErrorKind.Validation, "invalid_input", message);
    public static AppException Stale() => new(ErrorKind.PreconditionFailed, "stale_etag", "The record changed since you loaded it. Refresh and try again.");
    public static AppException Incomplete(IReadOnlyList<string> missing) =>
        new(ErrorKind.Validation, "incomplete_request", $"The request is incomplete. Missing: {string.Join(", ", missing)}.", missing);
    public static AppException Conflict(string code, string message) => new(ErrorKind.Conflict, code, message);
    public static AppException EtagRequired() => new(ErrorKind.PreconditionRequired, "etag_required", "If-Match header is required for updates.");
}
