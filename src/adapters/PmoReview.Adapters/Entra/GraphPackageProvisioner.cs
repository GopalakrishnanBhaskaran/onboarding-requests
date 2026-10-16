using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Core;
using PmoReview.Core.Handoff;

namespace PmoReview.Adapters.Entra;

/// <summary>
/// Phase 2c package set-up (Graph v1.0), only in the one allowed catalog: security group (created if missing; an existing
/// role-assignable or non-security group is refused), group as catalog resource, access package with the group's
/// Member role, and the "PMO Review auto-assign" policy (direct assignments only, no approval stage, 180 days).
/// Needs EntitlementManagement.ReadWrite.All and Group.ReadWrite.All.
/// </summary>
public sealed class GraphPackageProvisioner : IPackageProvisioner
{
    private readonly GraphHttp _graph;
    private readonly string _catalogId;
    private readonly string _policyName;

    public GraphPackageProvisioner(HttpClient http, TokenCredential credential, string catalogId, string policyName)
    {
        if (!Guid.TryParse(catalogId, out var c)) throw new ArgumentException("ENTRA_ALLOWED_CATALOG_ID must be a GUID.", nameof(catalogId));
        (_graph, _catalogId, _policyName) = (new GraphHttp(http, credential), c.ToString("D"), policyName);
    }

    public async Task<string> EnsureAsync(PackageSpec spec, CancellationToken ct)
    {
        var groupId = await EnsureGroupAsync(spec, ct);
        var (resourceId, role) = await EnsureCatalogResourceAsync(groupId, ct);
        var packageId = await EnsurePackageAsync(spec, ct);
        await EnsureRoleScopeAsync(packageId, groupId, resourceId, role, ct);
        await EnsurePolicyAsync(packageId, ct);
        return packageId;
    }

    private async Task<string> EnsureGroupAsync(PackageSpec spec, CancellationToken ct)
    {
        var filter = Uri.EscapeDataString($"displayName eq '{GraphHttp.Literal(spec.GroupName)}'");
        using var found = await _graph.GetAsync($"{GraphHttp.V1}groups?$filter={filter}&$select=id,displayName,securityEnabled,isAssignableToRole", "GraphError", ct);
        var groups = GraphHttp.Values(found).ToList();
        if (groups.Count > 1) throw new EntraWriteException("GroupNameAmbiguous");
        if (groups.Count == 1)
        {
            var g = groups[0];
            if (GraphHttp.Bool(g, "isAssignableToRole") || !GraphHttp.Bool(g, "securityEnabled")) throw new EntraWriteException("GroupNotAllowed");
            return GraphHttp.Str(g, "id") ?? throw new EntraWriteException("GraphError");
        }
        var nick = Regex.Replace(spec.GroupName, "[^A-Za-z0-9-]", "").ToLowerInvariant();
        using var created = await _graph.SendAsync(HttpMethod.Post, $"{GraphHttp.V1}groups", new
        {
            displayName = spec.GroupName,
            description = $"SAMPLE pilot: membership managed only by the Entra access package ({spec.PackageName}).",
            mailEnabled = false,
            mailNickname = nick.Length > 64 ? nick[..64] : nick,
            securityEnabled = true,
        }, "GroupCreateRejected", ct);
        return (created is null ? null : GraphHttp.Str(created.RootElement, "id")) ?? throw new EntraWriteException("GroupCreateRejected");
    }

    private async Task<(string ResourceId, JsonElement Role)> EnsureCatalogResourceAsync(string groupId, CancellationToken ct)
    {
        var resourcesUrl = $"{GraphHttp.Em}catalogs/{_catalogId}/resources?$filter={Uri.EscapeDataString($"originId eq '{groupId}'")}";
        string? resourceId;
        using (var res = await _graph.GetAsync(resourcesUrl, "CatalogNotFound", ct))
            resourceId = GraphHttp.Values(res).Select(r => GraphHttp.Str(r, "id")).FirstOrDefault();
        if (resourceId is null)
        {
            using var _ = await _graph.SendAsync(HttpMethod.Post, $"{GraphHttp.Em}resourceRequests", new
            {
                requestType = "adminAdd",
                resource = new { originId = groupId, originSystem = "AadGroup" },
                catalog = new { id = _catalogId },
            }, "CatalogResourceRejected", ct);
            using var again = await _graph.GetAsync(resourcesUrl, "CatalogNotFound", ct);
            resourceId = GraphHttp.Values(again).Select(r => GraphHttp.Str(r, "id")).FirstOrDefault()
                         ?? throw new EntraWriteException("CatalogResourcePending"); // appears shortly; next run continues
        }

        var roleFilter = Uri.EscapeDataString($"(originSystem eq 'AadGroup' and resource/id eq '{resourceId}')");
        using var roles = await _graph.GetAsync($"{GraphHttp.Em}catalogs/{_catalogId}/resourceRoles?$filter={roleFilter}&$expand=resource", "CatalogNotFound", ct);
        var member = GraphHttp.Values(roles).FirstOrDefault(r => GraphHttp.Str(r, "displayName") == "Member");
        if (member.ValueKind != JsonValueKind.Object) throw new EntraWriteException("CatalogResourcePending");
        return (resourceId, member.Clone());
    }

    private async Task<string> EnsurePackageAsync(PackageSpec spec, CancellationToken ct)
    {
        using (var list = await _graph.GetAsync($"{GraphHttp.Em}catalogs/{_catalogId}/accessPackages", "CatalogNotFound", ct))
        {
            var id = GraphHttp.Values(list).Where(p => GraphHttp.Str(p, "displayName") == spec.PackageName).Select(p => GraphHttp.Str(p, "id")).FirstOrDefault();
            if (id is not null) return id;
        }
        using var created = await _graph.SendAsync(HttpMethod.Post, $"{GraphHttp.Em}accessPackages", new
        {
            displayName = spec.PackageName,
            description = spec.Description,
            isHidden = false,
            catalog = new { id = _catalogId },
        }, "PackageCreateRejected", ct);
        return (created is null ? null : GraphHttp.Str(created.RootElement, "id")) ?? throw new EntraWriteException("PackageCreateRejected");
    }

    private async Task EnsureRoleScopeAsync(string packageId, string groupId, string resourceId, JsonElement role, CancellationToken ct)
    {
        using (var pkg = await _graph.GetAsync($"{GraphHttp.Em}accessPackages/{packageId}?$expand=resourceRoleScopes($expand=role,scope)", "AccessPackageNotFound", ct))
        {
            var has = pkg.RootElement.TryGetProperty("resourceRoleScopes", out var scopes) && scopes.ValueKind == JsonValueKind.Array
                      && scopes.EnumerateArray().Any(s => s.TryGetProperty("scope", out var sc) && GraphHttp.Str(sc, "originId") == groupId);
            if (has) return;
        }
        using var _ = await _graph.SendAsync(HttpMethod.Post, $"{GraphHttp.Em}accessPackages/{packageId}/resourceRoleScopes", new
        {
            role = new
            {
                id = GraphHttp.Str(role, "id"),
                originId = GraphHttp.Str(role, "originId"),
                originSystem = "AadGroup",
                resource = new { id = resourceId, originId = groupId, originSystem = "AadGroup" },
            },
            scope = new { originId = groupId, originSystem = "AadGroup" },
        }, "RoleScopeRejected", ct);
    }

    private async Task EnsurePolicyAsync(string packageId, CancellationToken ct)
    {
        var url = $"{GraphHttp.Em}assignmentPolicies?$expand=accessPackage&$filter={Uri.EscapeDataString($"accessPackage/id eq '{packageId}'")}";
        using (var list = await _graph.GetAsync(url, "AccessPackageNotFound", ct))
            if (GraphHttp.Values(list).Any(p => GraphHttp.SameName(GraphHttp.Str(p, "displayName"), _policyName))) return;

        using var _ = await _graph.SendAsync(HttpMethod.Post, $"{GraphHttp.Em}assignmentPolicies", new
        {
            displayName = _policyName,
            description = "Direct assignments filed by the PMO Review app after a second Reviewer accepts. No self-requests.",
            allowedTargetScope = "notSpecified",
            specificAllowedTargets = Array.Empty<object>(),
            expiration = new { type = "afterDuration", duration = "P180D" },
            requestorSettings = new
            {
                enableTargetsToSelfAddAccess = false, enableTargetsToSelfUpdateAccess = false, enableTargetsToSelfRemoveAccess = false,
                allowCustomAssignmentSchedule = true,
                enableOnBehalfRequestorsToAddAccess = false, enableOnBehalfRequestorsToUpdateAccess = false, enableOnBehalfRequestorsToRemoveAccess = false,
                onBehalfRequestors = Array.Empty<object>(),
            },
            requestApprovalSettings = new { isApprovalRequiredForAdd = false, isApprovalRequiredForUpdate = false, stages = Array.Empty<object>() },
            accessPackage = new { id = packageId },
        }, "PolicyCreateRejected", ct);
    }
}
