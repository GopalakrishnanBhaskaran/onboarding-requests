using Microsoft.Extensions.Logging.Abstractions;
using PmoReview.Adapters;
using PmoReview.Adapters.Storage;
using PmoReview.Core.Domain;
using PmoReview.Core.Handoff;
using PmoReview.Core.Storage;
using PmoReview.Tests.Support;
using Xunit;

namespace PmoReview.Tests.Unit;

/// <summary>
/// Phase 2c (approved 16 Oct 2026): joiner account (create, P2 licence, Temporary Access Pass shown once) and automatic
/// Entra package set-up for published mappings. Guard-rails: SAMPLE prefixes, tenant domain, never change existing users.
/// </summary>
public class ProvisioningTests
{
    private const string Pkg = "0b4c2a4e-6a51-4c47-9e0f-2f6f3c1d9a10";
    private const string Domain = "sample.onmicrosoft.com";
    private const string Joiner = "sample.joiner2@" + Domain;
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);
    private static readonly JoinerProvisioningSettings Settings = new() { TenantDomain = Domain };

    private static EntraHandoff Handoff(string upn = Joiner) => new()
    {
        Id = "req2.r1", ProjectId = TestApp.Alpha, RequestId = "req2", RequestRevision = 1, ReviewRunId = "req2.r1.a0",
        AcceptedBy = TestApp.Rita, Status = HandoffStatus.Queued, JoinerUpn = upn, AccessPackageId = Pkg,
        MappingId = "A-MAP-ENG-DEV", MappingVersion = 2,
        RequestedStart = new DateOnly(2026, 11, 2), RequestedEnd = new DateOnly(2026, 11, 30), CreatedAt = Now.AddMinutes(-1),
    };

    [Theory]
    [InlineData("sample.joiner2@sample.onmicrosoft.com", null)]
    [InlineData("SAMPLE.Joiner2@SAMPLE.onmicrosoft.com", null)]
    [InlineData("sample.joiner2@other.com", "JoinerDomainNotAllowed")]
    [InlineData("ceo@sample.onmicrosoft.com", "JoinerNameNotAllowed")]
    [InlineData("nodomain", "JoinerUpnInvalid")]
    public void Only_sample_joiners_in_the_tenant_domain_can_be_created(string upn, string? refused) =>
        Assert.Equal(refused, Settings.Refuse(upn));

    [Fact]
    public void Account_fields_come_from_the_upn_only()
    {
        var a = JoinerProvisioningSettings.AccountFor("Sample.Joiner_2@sample.onmicrosoft.com", "IN");
        Assert.Equal(("sample.joiner_2@sample.onmicrosoft.com", "Sample Joiner 2", "Sample.Joiner_2", "IN"), (a.UserPrincipalName, a.DisplayName, a.MailNickname, a.UsageLocation));
    }

    [Fact]
    public async Task New_joiner_gets_account_licence_and_one_pass_then_the_assignment()
    {
        var stores = InMemoryDataStores.Create();
        await stores.Handoffs.CreateAsync(Handoff());
        var dir = new FakeDirectory();
        var writer = new FakeWriter();
        var clock = new FixedClock(Now);
        HandoffSync Sync() => new(stores, new EmptyReader(), clock, NullLogger<HandoffSync>.Instance, writer, new JoinerProvisioning(dir, Settings, stores, clock));

        await Sync().RunOnceAsync(CancellationToken.None);

        var h = (await stores.Handoffs.GetAsync(TestApp.Alpha, "req2.r1"))!.Value;
        Assert.Equal(("oid-new", true), (h.JoinerObjectId, h.JoinerAccountCreatedByApp));
        Assert.NotNull(h.JoinerLicensedAt);
        Assert.NotNull(h.TapIssuedAt);
        Assert.Equal(HandoffStatus.PendingApproval, h.Status);
        Assert.Equal(["create:sample.joiner2@sample.onmicrosoft.com", "licence:oid-new", "tap:oid-new"], dir.Calls);
        Assert.Single(writer.Orders);

        // The TAP starts on the reviewed start date and is stored once for the show-once page.
        var pass = (await stores.Passes.GetAsync(TestApp.Alpha, "req2.r1"))!.Value;
        Assert.Equal("TAP-SECRET", pass.Secret);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero), pass.ValidFrom);
        Assert.True(pass.Ttl > 0);
        Assert.DoesNotContain("TAP-SECRET", string.Join(" ", h.History.Select(e => e.Note)));

        // Next run: nothing is created twice.
        clock.Advance(TimeSpan.FromMinutes(5));
        await Sync().RunOnceAsync(CancellationToken.None);
        Assert.Equal(3, dir.Calls.Count);
        Assert.Single(writer.Orders);
    }

    [Fact]
    public async Task Existing_account_is_never_changed()
    {
        var stores = InMemoryDataStores.Create();
        await stores.Handoffs.CreateAsync(Handoff("ceo@" + Domain));
        var dir = new FakeDirectory { Existing = { ["ceo@" + Domain] = "oid-ceo" } };
        var writer = new FakeWriter();
        var clock = new FixedClock(Now);
        await new HandoffSync(stores, new EmptyReader(), clock, NullLogger<HandoffSync>.Instance, writer, new JoinerProvisioning(dir, Settings, stores, clock)).RunOnceAsync(CancellationToken.None);

        var h = (await stores.Handoffs.GetAsync(TestApp.Alpha, "req2.r1"))!.Value;
        Assert.Equal(("oid-ceo", false), (h.JoinerObjectId, h.JoinerAccountCreatedByApp));
        Assert.Null(h.JoinerLicensedAt);
        Assert.Null(h.TapIssuedAt);
        Assert.Empty(dir.Calls); // no create, no licence, no pass
        Assert.Null(await stores.Passes.GetAsync(TestApp.Alpha, "req2.r1"));
        Assert.Single(writer.Orders); // only the access package
    }

    [Fact]
    public async Task Refused_upn_creates_nothing_and_assigns_nothing()
    {
        var stores = InMemoryDataStores.Create();
        await stores.Handoffs.CreateAsync(Handoff("someone@" + Domain));
        var dir = new FakeDirectory();
        var writer = new FakeWriter();
        var clock = new FixedClock(Now);
        await new HandoffSync(stores, new EmptyReader(), clock, NullLogger<HandoffSync>.Instance, writer, new JoinerProvisioning(dir, Settings, stores, clock)).RunOnceAsync(CancellationToken.None);
        var h = (await stores.Handoffs.GetAsync(TestApp.Alpha, "req2.r1"))!.Value;
        Assert.Equal(("JoinerNameNotAllowed", HandoffStatus.Queued), (h.LastSyncError, h.Status));
        Assert.Empty(dir.Calls);
        Assert.Empty(writer.Orders);
    }

    [Fact]
    public async Task A_failed_step_keeps_earlier_progress_and_resumes()
    {
        var stores = InMemoryDataStores.Create();
        await stores.Handoffs.CreateAsync(Handoff());
        var dir = new FakeDirectory { FailLicence = true };
        var writer = new FakeWriter();
        var clock = new FixedClock(Now);
        HandoffSync Sync() => new(stores, new EmptyReader(), clock, NullLogger<HandoffSync>.Instance, writer, new JoinerProvisioning(dir, Settings, stores, clock));

        await Sync().RunOnceAsync(CancellationToken.None);
        var h = (await stores.Handoffs.GetAsync(TestApp.Alpha, "req2.r1"))!.Value;
        Assert.Equal(("oid-new", true, "LicenseUnavailable"), (h.JoinerObjectId, h.JoinerAccountCreatedByApp, h.LastSyncError));
        Assert.Empty(writer.Orders);

        dir.FailLicence = false;
        clock.Advance(TimeSpan.FromMinutes(5));
        await Sync().RunOnceAsync(CancellationToken.None);
        h = (await stores.Handoffs.GetAsync(TestApp.Alpha, "req2.r1"))!.Value;
        Assert.Null(h.LastSyncError);
        Assert.Equal(1, dir.Calls.Count(c => c.StartsWith("create:", StringComparison.Ordinal))); // the account was not created twice
        Assert.Single(writer.Orders);
    }

    [Fact]
    public async Task Pass_is_shown_once_to_pmo_or_reviewer_and_never_listed()
    {
        var app = TestApp.With(settings: new PilotSettings { EntraHandoffEnabled = true, EntraAutoAssign = true });
        await app.Stores.Handoffs.CreateAsync(Handoff() with { TapIssuedAt = Now });
        await app.Stores.Passes.CreateAsync(new JoinerPass
        {
            Id = "req2.r1", ProjectId = TestApp.Alpha, JoinerUpn = Joiner, Secret = "TAP-SECRET",
            ValidFrom = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(8), CreatedAt = DateTimeOffset.UtcNow, Ttl = 86400,
        });

        var list = await app.Call(TestApp.Alice, "GET", $"projects/{TestApp.Alpha}/requests/req2/handoffs");
        Assert.Equal(200, list.Status);
        Assert.DoesNotContain("TAP-SECRET", list.Json.GetRawText());
        Assert.True(list.Json.GetProperty("passes")[0].GetProperty("available").GetBoolean());

        Assert.Equal(403, (await app.Call(TestApp.Oscar, "POST", $"projects/{TestApp.Alpha}/handoffs/req2.r1/pass/reveal", new { })).Status); // Operator
        Assert.Equal(403, (await app.Call(TestApp.Mark, "POST", $"projects/{TestApp.Alpha}/handoffs/req2.r1/pass/reveal", new { })).Status);  // Maintainer
        Assert.Equal(404, (await app.Call(TestApp.Bob, "POST", $"projects/{TestApp.Alpha}/handoffs/req2.r1/pass/reveal", new { })).Status);   // other project: not visible

        var first = await app.Call(TestApp.Rita, "POST", $"projects/{TestApp.Alpha}/handoffs/req2.r1/pass/reveal", new { });
        Assert.Equal(200, first.Status);
        Assert.Equal("TAP-SECRET", first.Json.GetProperty("secret").GetString());

        var second = await app.Call(TestApp.Alice, "POST", $"projects/{TestApp.Alpha}/handoffs/req2.r1/pass/reveal", new { });
        Assert.Equal(409, second.Status);
        var stored = (await app.Stores.Passes.GetAsync(TestApp.Alpha, "req2.r1"))!.Value;
        Assert.Null(stored.Secret);
        Assert.Equal(TestApp.Rita, stored.RevealedBy);
    }

    [Fact]
    public async Task Published_mapping_without_package_gets_set_up_and_a_new_version_with_the_link()
    {
        var stores = InMemoryDataStores.Create();
        await stores.Mappings.CreateAsync(Mapping("A-MAP-NEW", "SAMPLE-GRP-ALPHA-DEV-NEW"));
        await stores.Mappings.CreateAsync(Mapping("A-MAP-DONE", "SAMPLE-GRP-X") with { AccessPackageId = Pkg, MyAccessLink = "https://myaccess.microsoft.com/#/access-packages/" + Pkg });
        await stores.Mappings.CreateAsync(Mapping("A-MAP-OLD", "SAMPLE-GRP-Y") with { EffectiveTo = new DateOnly(2026, 3, 31) });
        await stores.Mappings.CreateAsync(Mapping("A-MAP-DRAFT", "SAMPLE-GRP-Z") with { PublicationStatus = PublicationStatus.Draft });
        var prov = new FakeProvisioner();

        var published = await new PackageSync(stores, prov, Domain, "SAMPLE-GRP-", new FixedClock(Now), NullLogger<PackageSync>.Instance).RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, published);
        var spec = Assert.Single(prov.Specs);
        Assert.Equal(("SAMPLE-GRP-ALPHA-DEV-NEW", "SAMPLE Data Engineer – Dev [A-MAP-NEW]"), (spec.GroupName, spec.PackageName));
        var v2 = (await stores.Mappings.GetAsync(TestApp.Alpha, "A-MAP-NEW:v2"))!.Value;
        Assert.Equal((PublicationStatus.Published, "pkg-1", "pmo-worker"), (v2.PublicationStatus, v2.AccessPackageId, v2.Owner));
        Assert.Equal($"https://myaccess.microsoft.com/@{Domain}#/access-packages/pkg-1", v2.MyAccessLink);
    }

    [Fact]
    public async Task Group_outside_the_sample_prefix_is_refused_and_recorded_on_the_mapping()
    {
        var stores = InMemoryDataStores.Create();
        await stores.Mappings.CreateAsync(Mapping("A-MAP-BAD", "Global-Admins"));
        var prov = new FakeProvisioner();
        var clock = new FixedClock(Now);
        var sync = new PackageSync(stores, prov, Domain, "SAMPLE-GRP-", clock, NullLogger<PackageSync>.Instance);

        Assert.Equal(0, await sync.RunOnceAsync(CancellationToken.None));
        Assert.Empty(prov.Specs);
        Assert.Equal("GroupNameNotAllowed", (await stores.Mappings.GetAsync(TestApp.Alpha, "A-MAP-BAD:v1"))!.Value.EntraSetupError);

        // Not retried before the back-off.
        clock.Advance(TimeSpan.FromMinutes(1));
        await sync.RunOnceAsync(CancellationToken.None);
        Assert.Empty(prov.Specs);
    }

    private static RoleMapping Mapping(string id, string group) => new()
    {
        Id = $"{id}:v1", ProjectId = TestApp.Alpha, MappingId = id, Version = 1, BusinessRole = "SAMPLE Data Engineer", Environment = "Dev",
        GroupReference = group, EffectiveFrom = new DateOnly(2026, 1, 1), Owner = "sample-user-mark", PublicationStatus = PublicationStatus.Published,
        SourceReference = "SAMPLE-RBAC", IsSample = true,
    };

    private sealed class FakeDirectory : IJoinerDirectory
    {
        public Dictionary<string, string> Existing { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Calls { get; } = [];
        public bool FailLicence { get; set; }
        public Task<string?> FindUserAsync(string upn, CancellationToken ct) => Task.FromResult(Existing.GetValueOrDefault(upn));
        public Task<string> CreateUserAsync(NewJoinerAccount account, CancellationToken ct)
        {
            Calls.Add("create:" + account.UserPrincipalName);
            Existing[account.UserPrincipalName] = "oid-new";
            return Task.FromResult("oid-new");
        }
        public Task AssignLicenseAsync(string userId, CancellationToken ct)
        {
            if (FailLicence) throw new EntraWriteException("LicenseUnavailable");
            Calls.Add("licence:" + userId);
            return Task.CompletedTask;
        }
        public Task<TemporaryAccessPass> CreateTemporaryAccessPassAsync(string userId, DateTimeOffset start, int lifetimeMinutes, CancellationToken ct)
        {
            Calls.Add("tap:" + userId);
            return Task.FromResult(new TemporaryAccessPass("TAP-SECRET", start, start.AddMinutes(lifetimeMinutes)));
        }
    }

    private sealed class FakeWriter : IEntraAssignmentWriter
    {
        public List<AssignmentOrder> Orders { get; } = [];
        public Task<EntraRequest> RequestAssignmentAsync(AssignmentOrder order, CancellationToken ct)
        {
            Orders.Add(order);
            return Task.FromResult(new EntraRequest("req-app", "adminAdd", "submitted", null, Now, null, order.Start, order.End));
        }
    }

    private sealed class EmptyReader : IEntraGovernanceReader
    {
        public Task<IReadOnlyList<EntraRequest>> ListRequestsAsync(string accessPackageId, CancellationToken ct) => Task.FromResult<IReadOnlyList<EntraRequest>>([]);
        public Task<IReadOnlyList<EntraAssignment>> ListAssignmentsAsync(string accessPackageId, CancellationToken ct) => Task.FromResult<IReadOnlyList<EntraAssignment>>([]);
    }

    private sealed class FakeProvisioner : IPackageProvisioner
    {
        public List<PackageSpec> Specs { get; } = [];
        public Task<string> EnsureAsync(PackageSpec spec, CancellationToken ct)
        {
            Specs.Add(spec);
            return Task.FromResult("pkg-" + Specs.Count);
        }
    }
}
