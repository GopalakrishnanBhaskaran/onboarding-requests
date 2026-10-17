namespace PmoReview.Core.Domain;

/// <summary>One recorded check (Stage 2 review or a verification). <see cref="Detail"/> never contains secrets.</summary>
public sealed record CheckResult(string Code, string Group, CheckStatus Status, string Detail);

public static class CheckGroups
{
    public const string Intake = "Intake";
    public const string MappingSheet = "Mapping sheet";
    public const string ResponseDocument = "Response document";
    public const string RequestVsMapping = "Request vs mapping";
    public const string EntraCrossCheck = "Entra cross-check";
    public const string RiskFlags = "Risk flags";
    public const string AiReview = "AI review";
    public const string IdTeam = "ID team";
    public const string RoleTeam = "Role assignment";
    public const string FinalVerification = "Final verification";
}

public static class RiskFlags
{
    public const string Privileged = "Privileged access";
    public const string Guest = "Guest user";
    public const string BroadAccess = "Existing broad access";
    public const string Sensitive = "Sensitive resource";
}
