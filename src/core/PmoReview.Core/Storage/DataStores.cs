using PmoReview.Core.Domain;

namespace PmoReview.Core.Storage;

public static class ContainerNames
{
    public const string Projects = "projects";
    public const string RequirementRules = "requirementRules";
    public const string RoleMappings = "roleMappings";
    public const string Requests = "requests";
    public const string ReviewRuns = "reviewRuns";
    public const string ReviewDecisions = "reviewDecisions";
    public const string UserAccess = "userAccess";
}

/// <summary>All containers the application uses.</summary>
public sealed record DataStores(
    IDocumentStore<Project> Projects,
    IDocumentStore<RequirementRule> Rules,
    IDocumentStore<RoleMapping> Mappings,
    IDocumentStore<AccessRequest> Requests,
    IDocumentStore<ReviewRun> Runs,
    IDocumentStore<ReviewDecision> Decisions,
    IDocumentStore<UserAccess> UserAccess);
