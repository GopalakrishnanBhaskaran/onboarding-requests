using PmoReview.Adapters;
using PmoReview.Adapters.Storage;
using PmoReview.Core.Common;
using PmoReview.Core.Domain;
using PmoReview.Core.Security;
using PmoReview.Core.Storage;
using PmoReview.Tests.Support;
using Xunit;

namespace PmoReview.Tests.Unit;

/// <summary>v1.1 Phase 1: containers, records, roles, settings and the SAMPLE data with the I1 columns.</summary>
public class DataModelV11Tests
{
    private static readonly DateTimeOffset At = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    private static readonly string[] V11Containers =
        ["mappingVersions", "peopleInventory", "inventoryVersions", "escalations", "mappingQualityIssues", "groupResolutions", "offboardingRuns", "auditEvents"];

    [Fact]
    public void Every_v11_container_is_partitioned_by_project_and_the_database_stays_within_25_containers()
    {
        foreach (var name in V11Containers)
            Assert.Contains(CosmosSchema.Containers, c => c.Name == name && c.PartitionKey == "/projectId");
        // Shared-throughput database: 400 RU/s covers the first 25 containers (F90); legacy v1 containers still count.
        Assert.True(CosmosSchema.Containers.Length + CosmosSchema.LegacyContainers.Length <= 25);
    }

    [Fact]
    public async Task Each_new_store_round_trips_with_etag_concurrency_and_partition_isolation()
    {
        var s = InMemoryDataStores.Create();
        await RoundTrip(s.MappingVersions, p => new DataVersion { Id = DataVersion.MakeId(DataKind.Mapping, 1), ProjectId = p, Kind = DataKind.Mapping, Version = 1, FileHash = "ab" },
            v => v with { DiffSummary = "changed" });
        await RoundTrip(s.InventoryVersions, p => new DataVersion { Id = DataVersion.MakeId(DataKind.Inventory, 1, 2), ProjectId = p, Kind = DataKind.Inventory, Version = 1, Part = 2, Before = ["{}"] },
            v => v with { Added = ["x"] });
        await RoundTrip(s.PeopleInventory, p => new Person
        {
            Id = Person.MakeId("Sample.Person@Sample.Invalid"), ProjectId = p, EnterpriseId = "sample.person@sample.invalid", DisplayName = "SAMPLE Person",
            EnterpriseEmail = "sample.person@sample.invalid", UpdatedBy = "test", UpdatedAt = At,
        }, v => v with { Status = PersonStatus.Leaver });
        await RoundTrip(s.Escalations, p => new Escalation
        {
            Id = Escalation.MakeId(EscalationKind.ProdWrite, "r1"), ProjectId = p, Kind = EscalationKind.ProdWrite, RequestId = "r1", RequestRevision = 2,
            User = "u", Group = "g", Level = "Write", Environment = "Prod", Reason = "above Read in Prod", RequestedBy = "alice", CreatedAt = At,
        }, v => v with { Status = EscalationStatus.Approved, DecidedBy = "petra", DecidedAt = At });
        await RoundTrip(s.MappingQualityIssues, p => new MappingQualityIssue
        {
            Id = MappingQualityIssue.MakeId("PowerBI_Readers", "PowerBi-Readers"), ProjectId = p, VariantName = "PowerBI_Readers", CanonicalName = "PowerBi-Readers",
            SheetRow = 7, Reason = "near-duplicate", FirstSeenAt = At, LastSeenAt = At,
        }, v => v with { Status = QualityIssueStatus.Resolved });
        await RoundTrip(s.GroupResolutions, p => new GroupResolution { Id = GroupResolution.MakeId("powerbireaders"), ProjectId = p, GroupName = "PowerBi-Readers", ReadAt = At },
            v => v with { ObjectId = "00000000-0000-0000-0000-0000000000aa", Count = 1 });
        await RoundTrip(s.OffboardingRuns, p => new OffboardingRun
        {
            Id = "off-1", ProjectId = p, EnterpriseId = "sample.person@sample.invalid", Mode = "plan", Status = OffboardingStatus.Planning,
            CorrelationId = "c1", RequestedBy = "paul",
        }, v => v with { Status = OffboardingStatus.Planned });
        var auditId = AuditEvent.NewId(At);
        await RoundTrip(s.AuditEvents, p => new AuditEvent { Id = auditId, ProjectId = p, At = At, Actor = "dana", Action = "upload-apply", Target = "mapping v1" },
            v => v with { Details = "x" });
        Assert.NotEqual(auditId, AuditEvent.NewId(At)); // append-only: every event gets its own id
    }

    private static async Task RoundTrip<T>(IDocumentStore<T> store, Func<string, T> make, Func<T, T> change) where T : class, IDocument
    {
        var a = await store.CreateAsync(make("SAMPLE-ALPHA"));
        await store.CreateAsync(make("SAMPLE-BETA"));
        Assert.Single(await store.ListAsync("SAMPLE-ALPHA"));
        Assert.Equal(AppJson.Serialize(a.Value), AppJson.Serialize((await store.GetAsync("SAMPLE-ALPHA", a.Value.Id))!.Value));
        var changed = await store.ReplaceAsync(change(a.Value), a.ETag);
        await Assert.ThrowsAsync<ConcurrencyException>(() => store.ReplaceAsync(change(a.Value), a.ETag)); // stale ETag
        await Assert.ThrowsAsync<DocumentConflictException>(() => store.CreateAsync(make("SAMPLE-ALPHA")));
        Assert.NotEqual(a.ETag, changed.ETag);
    }

    [Fact]
    public void Ids_are_deterministic_storage_safe_and_distinct_per_kind()
    {
        Assert.Equal(Person.MakeId(" Sample.Person@Sample.Invalid "), Person.MakeId("sample.person@sample.invalid"));
        Assert.DoesNotContain('@', Person.MakeId("a@b.c"));
        Assert.Equal(Escalation.MakeId(EscalationKind.ProdWrite, "r1"), Escalation.MakeId(EscalationKind.ProdWrite, "r1"));
        Assert.NotEqual(Escalation.MakeId(EscalationKind.ProdWrite, "r1"), Escalation.MakeId(EscalationKind.UnresolvedGroup, "r1"));
        Assert.Equal("mapping-v3", DataVersion.MakeId(DataKind.Mapping, 3));
        Assert.Equal("inventory-v3-p1", DataVersion.MakeId(DataKind.Inventory, 3, 1));
        Assert.Equal(MappingQualityIssue.MakeId("A", "B"), MappingQualityIssue.MakeId(" a ", "b"));
    }

    [Fact]
    public void ProdApprover_only_decides_prod_escalations_and_DataMaintainer_only_uploads()
    {
        var all = Enum.GetValues<AppRole>();
        foreach (var role in all)
        {
            Assert.Equal(role == AppRole.ProdApprover, AccessPolicy.Allows([role], Permission.DecideProdEscalation));
            Assert.Equal(role == AppRole.DataMaintainer, AccessPolicy.Allows([role], Permission.UploadData));
            Assert.Equal(role is AppRole.PMO or AppRole.Operator, AccessPolicy.Allows([role], Permission.Offboard));
        }
        // Neither new role approves a request stage or works a team task.
        foreach (var p in new[] { Permission.ApproveAsOwner, Permission.ApproveAsSecurity, Permission.WorkIdTeamTasks, Permission.WorkRoleTeamTasks, Permission.EditMappingSheet })
        {
            Assert.False(AccessPolicy.Allows([AppRole.ProdApprover], p));
            Assert.False(AccessPolicy.Allows([AppRole.DataMaintainer], p));
        }
    }

    [Fact]
    public void Legacy_Maintainer_rows_gain_no_upload_rights()
    {
        foreach (var p in Enum.GetValues<Permission>())
            Assert.False(AccessPolicy.Allows([AppRole.Maintainer], p));
    }

    [Fact]
    public void V11_switches_default_off_and_bad_values_are_refused_or_clamped()
    {
        var s = PilotSettings.FromEnvironment(_ => null).Workflow;
        Assert.False(s.AgentOffboarding);
        Assert.False(s.OffboardDeleteUser);
        Assert.False(s.ExternalUploadApiEnabled);
        Assert.Null(s.TapMailSender);
        Assert.Equal(5 * 1024 * 1024, s.MaxUploadBytes);

        string? Env(string n) => n switch
        {
            "MAX_UPLOAD_BYTES" => "999999999", "TAP_MAIL_SENDER" => " Sender@Sample.Invalid ", "EXTERNAL_UPLOAD_AUDIENCE" => "not-a-guid",
            _ => null,
        };
        var t = PilotSettings.FromEnvironment(Env).Workflow;
        Assert.Equal(25 * 1024 * 1024, t.MaxUploadBytes); // below the 30 MB SWA request limit (F88)
        Assert.Equal("sender@sample.invalid", t.TapMailSender);
        Assert.Null(t.ExternalUploadAudience);
        Assert.Throws<InvalidOperationException>(() => PilotSettings.FromEnvironment(n => n == "TAP_MAIL_SENDER" ? "a@b.c;d@e.f" : null));
    }

    [Fact]
    public void Access_levels_rank_read_lowest_and_environments_know_uat()
    {
        Assert.Equal(0, AccessLevels.Rank("read"));
        Assert.True(AccessLevels.Rank("Write") > AccessLevels.Rank("Read"));
        Assert.Equal(4, AccessLevels.Rank(" ADMIN "));
        Assert.Equal(-1, AccessLevels.Rank("Data Engineer"));
        Assert.Equal("PH", ResourceGroupTypes.Canonical("ph"));
        Assert.Null(ResourceGroupTypes.Canonical("Platform"));
        Assert.True(Environments.Same("UAT", "uat"));
        Assert.False(Environments.IsProduction("UAT"));
        Assert.True(Environments.IsProduction("PRD"));
        // Any spelling that starts with prod / prd is Prod (review finding 14: fail closed); pre-production is not.
        Assert.All(["Prod-EU", "Prod2", "prd-weu", "Production", "LIVE"], e => Assert.True(Environments.IsProduction(e), e));
        Assert.All(["PreProd", "ppd", "Dev", "Test", "", null], e => Assert.False(Environments.IsProduction(e), e));
    }

    [Fact]
    public void Sample_mapping_rows_carry_the_I1_columns_and_match_their_project_tenant()
    {
        var data = SampleDataLoader.Read(Path.Combine(AppContext.BaseDirectory, "sample-data.json"));
        var tenants = data.Projects.ToDictionary(p => p.ProjectId, p => p.TenantId);
        Assert.All(data.MappingSheet, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Domain));
            Assert.NotNull(ResourceGroupTypes.Canonical(r.ResourceGroupType));
            Assert.False(string.IsNullOrWhiteSpace(r.Owner));
            Assert.NotNull(r.EffectiveFrom);
            Assert.Equal(tenants[r.ProjectId], r.TenantId);
            Assert.False(r.Retired);
        });
        // The sample covers each Resource* option type, and a tenant without PH / ACQ (SAMPLE-BETA).
        Assert.Contains(data.MappingSheet, r => r.ResourceGroupType == "PH");
        Assert.Contains(data.MappingSheet, r => r.ResourceGroupType == "ACQ");
        Assert.All(data.MappingSheet.Where(r => r.ProjectId == TestApp.Beta), r => Assert.Equal("Domain", r.ResourceGroupType));
        Assert.All(data.PeopleInventory, p => Assert.Equal(Person.MakeId(p.EnterpriseId), p.Id));
        Assert.Contains(data.Projects, p => p.IsProductionTenant == true);
        Assert.All(data.Projects, p => Assert.NotNull(p.IsProductionTenant)); // every SAMPLE tenant states it (unset counts as production)
    }

    [Fact]
    public async Task Sample_seed_loads_people_and_the_new_roles()
    {
        var app = TestApp.Create();
        Assert.NotEmpty(await app.Stores.PeopleInventory.ListAsync(TestApp.Alpha));
        var petra = await new AccessService(app.Stores).ResolveAsync(new ClientPrincipal("aad", "sample-user-petra", "sample-user-petra@sample.invalid", ["authenticated"]), default);
        Assert.True(petra.Has(TestApp.Pest, Permission.DecideProdEscalation));
        var dana = await new AccessService(app.Stores).ResolveAsync(new ClientPrincipal("aad", "sample-user-dana", "sample-user-dana@sample.invalid", ["authenticated"]), default);
        Assert.True(dana.Has(TestApp.Alpha, Permission.UploadData));
        Assert.False(dana.Has(TestApp.Beta, Permission.UploadData)); // no role in Beta
    }
}
