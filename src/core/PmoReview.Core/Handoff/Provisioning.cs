using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PmoReview.Core.Common;
using PmoReview.Core.Domain;
using PmoReview.Core.Storage;

namespace PmoReview.Core.Handoff;

/// <summary>
/// Phase 2c (approved by Gokul, 16 Oct 2026): a Temporary Access Pass the worker issued for an account it created.
/// Shown once in the app, then the secret is removed. Cosmos TTL deletes the whole document after expiry.
/// </summary>
public sealed record JoinerPass : IDocument
{
    /// <summary>Same as the hand-off id.</summary>
    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    public required string JoinerUpn { get; init; }
    /// <summary>The pass itself. Null after it has been shown once. Never logged, never returned by a list call.</summary>
    public string? Secret { get; init; }
    public required DateTimeOffset ValidFrom { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public string? RevealedBy { get; init; }
    public DateTimeOffset? RevealedAt { get; init; }
    /// <summary>Cosmos per-item TTL in seconds (container defaultTtl = -1).</summary>
    [JsonPropertyName("ttl")] public int Ttl { get; init; }
    public int SchemaVersion { get; init; } = 1;
    [JsonIgnore] public string PartitionKey => ProjectId;
}

/// <summary>What the app shows about a pass. Never the secret.</summary>
public sealed record JoinerPassSummary(string HandoffId, bool Available, DateTimeOffset ValidFrom, DateTimeOffset ExpiresAt, string? RevealedBy, DateTimeOffset? RevealedAt)
{
    public static JoinerPassSummary From(JoinerPass p, DateTimeOffset now) =>
        new(p.Id, p.Secret is not null && p.ExpiresAt > now, p.ValidFrom, p.ExpiresAt, p.RevealedBy, p.RevealedAt);
}

public sealed record TemporaryAccessPass(string Secret, DateTimeOffset ValidFrom, DateTimeOffset ExpiresAt);

/// <summary>New joiner account: built only from the reviewed snapshot UPN.</summary>
public sealed record NewJoinerAccount(string UserPrincipalName, string DisplayName, string MailNickname, string UsageLocation);

/// <summary>
/// Entra writes for joiner accounts (worker only). Implementations create users, assign the licence and issue a TAP;
/// they never change or delete an existing user.
/// </summary>
public interface IJoinerDirectory
{
    /// <summary>Object id, or null when no user has this UPN.</summary>
    Task<string?> FindUserAsync(string upn, CancellationToken ct);
    Task<string> CreateUserAsync(NewJoinerAccount account, CancellationToken ct);
    Task AssignLicenseAsync(string userId, CancellationToken ct);
    Task<TemporaryAccessPass> CreateTemporaryAccessPassAsync(string userId, DateTimeOffset start, int lifetimeMinutes, CancellationToken ct);
}

/// <summary>
/// Guard-rails for joiner accounts. Pilot: only UPNs in the tenant domain that start with the SAMPLE prefix.
/// </summary>
public sealed record JoinerProvisioningSettings
{
    public required string TenantDomain { get; init; }
    public string UpnPrefix { get; init; } = "sample.";
    public string UsageLocation { get; init; } = "IN";
    public int TapLifetimeMinutes { get; init; } = 480;

    /// <summary>Null when allowed; otherwise the error category.</summary>
    public string? Refuse(string upn)
    {
        var at = upn.LastIndexOf('@');
        if (at <= 0) return "JoinerUpnInvalid";
        if (!string.Equals(upn[(at + 1)..], TenantDomain, StringComparison.OrdinalIgnoreCase)) return "JoinerDomainNotAllowed";
        if (!upn.StartsWith(UpnPrefix, StringComparison.OrdinalIgnoreCase)) return "JoinerNameNotAllowed";
        return null;
    }

    public static NewJoinerAccount AccountFor(string upn, string usageLocation)
    {
        var local = upn[..upn.IndexOf('@')];
        var nick = Regex.Replace(local, "[^A-Za-z0-9._-]", "");
        if (nick.Length > 64) nick = nick[..64];
        var words = local.Split(['.', '_', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]);
        var display = string.Join(' ', words);
        return new NewJoinerAccount(upn.ToLowerInvariant(), display.Length == 0 ? local : display, nick, usageLocation);
    }
}

/// <summary>Joiner provisioning as used by <see cref="HandoffSync"/>: account, then licence, then Temporary Access Pass.</summary>
public sealed class JoinerProvisioning(IJoinerDirectory directory, JoinerProvisioningSettings settings, DataStores stores, IClock clock)
{
    /// <summary>
    /// Runs the steps not yet done for this hand-off. Returns the record with every step that succeeded, plus the error
    /// category of the step that failed (null when all are done), so progress is never lost between runs.
    /// </summary>
    public async Task<(EntraHandoff Handoff, string? Error)> EnsureAsync(EntraHandoff h, CancellationToken ct)
    {
        var progress = h;
        try
        {
            await StepsAsync(h, x => progress = x, ct);
            return (progress, null);
        }
        catch (EntraWriteException e)
        {
            return (progress, e.Category);
        }
    }

    private async Task StepsAsync(EntraHandoff h, Action<EntraHandoff> save, CancellationToken ct)
    {
        var upn = h.JoinerUpn ?? throw new EntraWriteException("IncompleteHandoff");
        var now = clock.UtcNow;

        if (h.JoinerObjectId is null)
        {
            var existing = await directory.FindUserAsync(upn, ct);
            if (existing is not null)
            {
                // An existing account is never changed: no licence, no pass. Only the access package is assigned.
                h = h with { JoinerObjectId = existing, JoinerAccountCreatedByApp = false };
                save(h);
            }
            else
            {
                if (settings.Refuse(upn) is { } refused) throw new EntraWriteException(refused);
                var id = await directory.CreateUserAsync(JoinerProvisioningSettings.AccountFor(upn, settings.UsageLocation), ct);
                h = h with
                {
                    JoinerObjectId = id,
                    JoinerAccountCreatedByApp = true,
                    History = [.. h.History, new HandoffEvent(now, h.Status, "The app created the joiner's Entra account (no password is known to anyone).")],
                };
                save(h);
            }
        }

        if (!h.JoinerAccountCreatedByApp) return;

        if (h.JoinerLicensedAt is null)
        {
            await directory.AssignLicenseAsync(h.JoinerObjectId!, ct);
            h = h with { JoinerLicensedAt = now, History = [.. h.History, new HandoffEvent(now, h.Status, "The app assigned the Entra ID P2 licence.")] };
            save(h);
        }

        if (h.TapIssuedAt is null)
        {
            // A pass saved by an earlier run whose record update was lost counts as issued (a user can hold one TAP).
            var saved = await stores.Passes.GetAsync(h.ProjectId, h.Id, ct);
            if (saved is null)
            {
                var start = h.RequestedStart is { } s ? new DateTimeOffset(s.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : now;
                if (start < now) start = now;
                var tap = await directory.CreateTemporaryAccessPassAsync(h.JoinerObjectId!, start, settings.TapLifetimeMinutes, ct);
                var ttl = (int)Math.Clamp((tap.ExpiresAt - now).TotalSeconds + TimeSpan.FromDays(1).TotalSeconds, 3600, TimeSpan.FromDays(60).TotalSeconds);
                try
                {
                    await stores.Passes.CreateAsync(new JoinerPass
                    {
                        Id = h.Id, ProjectId = h.ProjectId, JoinerUpn = upn, Secret = tap.Secret,
                        ValidFrom = tap.ValidFrom, ExpiresAt = tap.ExpiresAt, CreatedAt = now, Ttl = ttl,
                    }, ct);
                }
                catch (DocumentConflictException)
                {
                    // Saved by a concurrent run; keep that one.
                }
            }
            h = h with { TapIssuedAt = now, History = [.. h.History, new HandoffEvent(now, h.Status, "The app issued a Temporary Access Pass. A PMO or Reviewer can show it once on this page.")] };
            save(h);
        }
    }
}

/// <summary>Spec for one role mapping's Entra set-up. Built from a published mapping.</summary>
public sealed record PackageSpec(string GroupName, string PackageName, string Description);

/// <summary>
/// Entra writes for automatic package set-up (worker only): the group (created only if missing, never role-assignable),
/// the group in the PMO catalog, the access package with its Member role, and the "PMO Review auto-assign" policy.
/// Returns the access package id. Idempotent: reuses what exists.
/// </summary>
public interface IPackageProvisioner
{
    Task<string> EnsureAsync(PackageSpec spec, CancellationToken ct);
}

/// <summary>
/// Background: for each latest published mapping without an Entra package, set it up and publish the next mapping
/// version with the link. Failures are written to the mapping (category only) and retried on the next run.
/// </summary>
public sealed class PackageSync(DataStores stores, IPackageProvisioner provisioner, string tenantDomain, string groupPrefix, IClock clock, ILogger<PackageSync> logger)
{
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(4);

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var all = await stores.Mappings.ListAllAsync(ct);
        var todo = all
            .GroupBy(m => (m.Value.ProjectId, m.Value.MappingId))
            .Select(g => g.MaxBy(m => m.Value.Version)!)
            .Where(m => m.Value.PublicationStatus == PublicationStatus.Published && m.Value.AccessPackageId is null
                        && (m.Value.EffectiveTo is null || m.Value.EffectiveTo >= today)
                        && (m.Value.EntraSetupAttemptAt is null || clock.UtcNow - m.Value.EntraSetupAttemptAt >= RetryAfter))
            .ToList();

        var published = 0;
        foreach (var current in todo)
        {
            var m = current.Value;
            try
            {
                if (!m.GroupReference.StartsWith(groupPrefix, StringComparison.Ordinal)) throw new EntraWriteException("GroupNameNotAllowed");
                var packageId = await provisioner.EnsureAsync(
                    new PackageSpec(m.GroupReference, PackageName(m), $"PMO Review mapping {m.MappingId}: {m.GroupReference} (Member)."), ct);
                var next = m with
                {
                    Id = $"{m.MappingId}:v{m.Version + 1}",
                    Version = m.Version + 1,
                    AccessPackageId = packageId,
                    MyAccessLink = $"https://myaccess.microsoft.com/@{tenantDomain}#/access-packages/{packageId}",
                    Owner = "pmo-worker",
                    PublicationStatus = PublicationStatus.Published,
                    EntraSetupError = null,
                    EntraSetupAttemptAt = clock.UtcNow,
                };
                await stores.Mappings.CreateAsync(next, ct);
                published++;
                logger.LogInformation("package sync: published {MappingId} v{Version} package={PackageId}", m.MappingId, next.Version, packageId);
            }
            catch (EntraWriteException e)
            {
                logger.LogWarning("package sync: {MappingId} v{Version} failed {Category}", m.MappingId, m.Version, e.Category);
                await Mark(current, e.Category, ct);
            }
            catch (DocumentConflictException)
            {
                // A Maintainer created the next version meanwhile; theirs stands.
            }
        }
        return published;
    }

    /// <summary>One package per mapping id, so two mappings for the same role and environment never share a package.</summary>
    public static string PackageName(RoleMapping m) => $"{m.BusinessRole} – {m.Environment} [{m.MappingId}]";

    private async Task Mark(Versioned<RoleMapping> current, string category, CancellationToken ct)
    {
        try
        {
            await stores.Mappings.ReplaceAsync(current.Value with { EntraSetupError = category, EntraSetupAttemptAt = clock.UtcNow }, current.ETag, ct);
        }
        catch (ConcurrencyException)
        {
            // Changed meanwhile; next run re-reads.
        }
    }
}
