using System.Diagnostics;
using System.Text.Json;
using PmoReview.Core.Domain;
using PmoReview.Core.Resources;
using PmoReview.Core.Workflow;
using PmoReview.Tests.Support;
using Xunit;

namespace PmoReview.Tests.Unit;

/// <summary>
/// v1.1 F1: the Resource* selector. Options only from the signed-in user's tenant sheet; de-duplication by object id or
/// normalised name; highest level shown; near-duplicates hidden with a mapping quality issue; unresolved groups never
/// granted; Prod above Read needs a human. Every edge case of the v1.1 prompt (section 8, F1) is a named test here.
/// </summary>
public class F1ResourceSelectorTests
{
    private const string Project = "SAMPLE-F1";
    private const string Other = "SAMPLE-F1-OTHER";
    private static readonly DateTimeOffset At = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static int _row;

    private static MappingRow Row(string group, string level, string? domain = "Finance", string type = "Domain", string? env = "Dev", string? objectId = null,
        string project = Project, string? id = null, params AzureRoleGrant[] roles)
    {
        var rowId = id ?? $"F1-{Interlocked.Increment(ref _row)}";
        return new MappingRow
        {
            Id = rowId, ProjectId = project, RowId = rowId, Resource = $"SAMPLE {domain ?? type}", AccessLevel = level, EntraGroup = group, Role = $"SAMPLE {level}",
            Environment = env, GroupObjectId = objectId, Domain = domain, ResourceGroupType = type, Owner = "sample-user-olivia@sample.invalid",
            EffectiveFrom = new DateOnly(2026, 10, 1), AzureRoles = roles, SourceReference = "SAMPLE", Version = 1, UpdatedBy = "test", UpdatedAt = At,
            IsSample = true, SheetRow = _row,
        };
    }

    private static AzureRoleGrant Reader => new() { RoleName = "Reader", RoleDefinitionId = "acdd72a7-3385-48ef-bd42-f606fba81ae7", Scope = TestApp.SampleAccessScope };
    private static AzureRoleGrant Contributor => new() { RoleName = "Storage Blob Data Contributor", RoleDefinitionId = "ba92f5b4-2d11-453d-a403-e96b0029c9fe", Scope = TestApp.SampleAccessScope };
    private static string Id() => Guid.NewGuid().ToString("D");

    /// <summary>A tenant project with Dev and Prod, Alice as requester; optional rows.</summary>
    private static async Task<TestApp> App(params MappingRow[] rows)
    {
        var app = TestApp.Create();
        foreach (var p in new[] { Project, Other })
        {
            await app.Stores.Projects.CreateAsync(new Project
            {
                Id = p, ProjectId = p, Name = $"SAMPLE {p}", TenantName = p, Environments = ["Dev", "Prod"], DomainIdSuffix = "@sample.invalid", IsSample = true,
                Contacts = new ProjectContacts { Security = ["sample.security@sample.invalid"], IdTeam = ["sample.id@sample.invalid"], RoleTeam = ["sample.role@sample.invalid"] },
            });
        }
        await app.Stores.UserAccess.CreateAsync(new UserAccess { Id = UserAccess.MakeId(TestApp.Alice, Project), UserId = TestApp.Alice, ProjectId = Project, Roles = [AppRole.Requester], IsSample = true });
        await app.Stores.UserAccess.CreateAsync(new UserAccess { Id = UserAccess.MakeId(TestApp.Bob, Other), UserId = TestApp.Bob, ProjectId = Other, Roles = [AppRole.Requester], IsSample = true });
        foreach (var r in rows) await app.Stores.MappingSheet.CreateAsync(r);
        return app;
    }

    private static string Query(string environment, params string[] resources) =>
        $"resources/resolve?projectId={Project}&environment={environment}" + string.Concat(resources.Select(r => $"&resource={Uri.EscapeDataString(r)}"));

    private static async Task<JsonElement> Resolve(TestApp app, string environment, params string[] resources)
    {
        var r = await app.Call(TestApp.Alice, "GET", Query(environment, resources));
        Assert.True(r.Status == 200, r.Body);
        return r.Json;
    }

    private static List<string> Options(JsonElement j) => j.GetProperty("options").EnumerateArray().Select(x => x.GetString()!).ToList();
    private static List<JsonElement> Access(JsonElement j) => j.GetProperty("resolvedAccess").EnumerateArray().ToList();
    private static List<JsonElement> Issues(JsonElement j) => j.GetProperty("mappingQualityIssues").EnumerateArray().ToList();

    [Fact]
    public async Task F1_resolve_returns_exactly_the_output_contract()
    {
        var app = await App(Row("SAMPLE-FIN-READERS", "Read", objectId: Id(), roles: Reader));
        var j = await Resolve(app, "Dev", "Finance");
        Assert.Equal(["tenantId", "environment", "selectedResources", "resolvedAccess", "mappingQualityIssues", "mappingVersion"], j.EnumerateObject().Select(p => p.Name));
        var a = Assert.Single(Access(j));
        Assert.Equal(["groupObjectId", "groupName", "accessLevel", "sources", "status"], a.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Auto", a.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.String, j.GetProperty("mappingVersion").ValueKind);
        Assert.Equal("Dev", j.GetProperty("environment").GetString());
    }

    [Fact]
    public async Task F1_empty_sheet_offers_no_options_and_resolve_is_refused()
    {
        var app = await App();
        var o = await app.Call(TestApp.Alice, "GET", $"resources/options?projectId={Project}&environment=Dev");
        Assert.Equal(200, o.Status);
        Assert.Empty(Options(o.Json));
        var r = await app.Call(TestApp.Alice, "GET", Query("Dev", "Finance"));
        Assert.Equal(400, r.Status); // nothing to choose: submit stays blocked
    }

    [Fact]
    public async Task F1_options_are_every_domain_then_PH_ACQ_and_All()
    {
        var app = await App(Row("G-FIN", "Read", "Finance", objectId: Id()), Row("G-HR", "Read", "HR", objectId: Id()),
            Row("G-PH", "Read", "Platform", "PH", objectId: Id()), Row("G-ACQ", "Read", "Legacy", "ACQ", objectId: Id()));
        var o = (await app.Call(TestApp.Alice, "GET", $"resources/options?projectId={Project}&environment=Dev")).Json;
        Assert.Equal(["Finance", "HR", "PH", "ACQ", ResourceGroupTypes.All], Options(o));
    }

    [Fact]
    public async Task F1_tenant_with_no_PH_or_ACQ_hides_those_options()
    {
        var app = await App(Row("G-FIN", "Read", "Finance", objectId: Id()));
        var o = (await app.Call(TestApp.Alice, "GET", $"resources/options?projectId={Project}&environment=Dev")).Json;
        Assert.Equal(["Finance", ResourceGroupTypes.All], Options(o));
        Assert.Equal(400, (await app.Call(TestApp.Alice, "GET", Query("Dev", "PH"))).Status); // not an option here
    }

    [Fact]
    public async Task F1_same_group_via_two_domains_is_listed_once_with_both_sources()
    {
        var shared = Id();
        var app = await App(Row("SAMPLE-SHARED-READERS", "Read", "Finance", objectId: shared), Row("SAMPLE-SHARED-READERS", "Read", "HR", objectId: shared));
        var a = Assert.Single(Access(await Resolve(app, "Dev", "Finance", "HR")));
        Assert.Equal(shared, a.GetProperty("groupObjectId").GetString());
        Assert.Equal(["Finance", "HR"], a.GetProperty("sources").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task F1_same_group_at_two_levels_shows_the_highest_and_notes_the_lower_as_included()
    {
        var g = Id();
        var app = await App(Row("SAMPLE-FIN-DATA", "Read", objectId: g, roles: Reader), Row("SAMPLE-FIN-DATA", "Write", objectId: g, roles: Contributor));
        var j = await Resolve(app, "Dev", "Finance");
        var a = Assert.Single(Access(j));
        Assert.Equal("Write", a.GetProperty("accessLevel").GetString());
        Assert.Contains(Issues(j), n => n.GetProperty("reason").GetString()!.Contains("Read is included"));
    }

    [Fact]
    public async Task F1_PowerBI_misspelling_variants_are_not_merged_silently()
    {
        var canonical = Id();
        var app = await App(
            Row("PowerBi-Readers", "Read", objectId: canonical),
            Row("PowerBI_Readers", "Read"),
            Row("Power BI Readers", "Read"),
            Row("PwrBI-Readers", "Read"));
        var j = await Resolve(app, "Dev", "Finance");
        var shown = Assert.Single(Access(j));
        Assert.Equal("PowerBi-Readers", shown.GetProperty("groupName").GetString());
        Assert.Equal(canonical, shown.GetProperty("groupObjectId").GetString());
        var issues = Issues(j);
        foreach (var variant in new[] { "PowerBI_Readers", "Power BI Readers", "PwrBI-Readers" })
        {
            var n = Assert.Single(issues, i => i.GetProperty("variantName").GetString() == variant);
            Assert.Equal("PowerBi-Readers", n.GetProperty("canonicalName").GetString());
            Assert.True(n.GetProperty("sheetRow").GetInt32() > 0);
            Assert.Contains("Not merged", n.GetProperty("reason").GetString());
        }
    }

    [Fact]
    public async Task F1_near_duplicates_canonical_is_the_one_entra_resolves_after_a_worker_run()
    {
        var app = await App(Row("PwrBI-Readers", "Read"), Row("PowerBi-Readers", "Read"));
        var g = app.Directory.AddGroup("PowerBi-Readers");
        app.ResolveGroups = true;
        await app.RunWorker(); // the worker reads both names in Entra; only one resolves
        var j = await Resolve(app, "Dev", "Finance");
        var shown = Assert.Single(Access(j));
        Assert.Equal(("PowerBi-Readers", g.ObjectId), (shown.GetProperty("groupName").GetString(), shown.GetProperty("groupObjectId").GetString()));
        Assert.Contains(await app.Stores.MappingQualityIssues.ListAsync(Project), i => i.Value.VariantName == "PwrBI-Readers" && i.Value.CanonicalName == "PowerBi-Readers");
    }

    [Fact]
    public async Task F1_group_name_that_does_not_resolve_is_unresolved_escalated_and_never_requested()
    {
        var ok = Id();
        var app = await App(Row("SAMPLE-FIN-MISSING", "Read", roles: Reader), Row("SAMPLE-FIN-OK", "Read", objectId: ok, roles: Reader));
        app.Directory.AddGroup("SAMPLE-FIN-OK", objectId: ok);
        var j = await Resolve(app, "Dev", "Finance");
        var missing = Assert.Single(Access(j), a => a.GetProperty("groupName").GetString() == "SAMPLE-FIN-MISSING");
        Assert.Equal(("Unresolved", ""), (missing.GetProperty("status").GetString(), missing.GetProperty("groupObjectId").GetString()));

        app.ResolveGroups = true;
        await app.RunWorker();
        var esc = Assert.Single(await app.Stores.Escalations.ListAsync(Project), e => e.Value.Kind == EscalationKind.UnresolvedGroup);
        Assert.Equal(("SAMPLE-FIN-MISSING", EscalationStatus.Open), (esc.Value.Group, esc.Value.Status));
        await app.RunWorker();
        Assert.Single(await app.Stores.Escalations.ListAsync(Project)); // raised once

        // Asked for directly in a submit: refused, nothing is saved for it.
        var only = await app.Call(TestApp.Alice, "POST", $"projects/{Project}/requests/bundle", Bundle(["Finance"], ["SAMPLE-FIN-MISSING"]));
        Assert.Equal(400, only.Status);
        var both = await app.Call(TestApp.Alice, "POST", $"projects/{Project}/requests/bundle", Bundle(["Finance"]));
        Assert.Equal(201, both.Status);
        Assert.Single(both.Json.GetProperty("requests").EnumerateArray());
        Assert.Equal("SAMPLE-FIN-MISSING", Assert.Single(both.Json.GetProperty("skipped").EnumerateArray()).GetProperty("groupName").GetString());
    }

    [Fact]
    public async Task F1_a_group_still_to_be_created_is_unresolved()
    {
        var app = await App(Row("SAMPLE-FIN-NEW", "Read", roles: Reader) with { GroupState = GroupState.ToBeCreated });
        Assert.Equal("Unresolved", Assert.Single(Access(await Resolve(app, "Dev", "Finance"))).GetProperty("status").GetString());
    }

    [Fact]
    public async Task F1_user_from_tenant_A_asking_for_tenant_B_gets_not_found()
    {
        var app = await App(Row("G-FIN", "Read", objectId: Id()), Row("G-OTHER", "Read", "Secret", project: Other, objectId: Id()));
        Assert.Equal(404, (await app.Call(TestApp.Alice, "GET", $"resources/resolve?projectId={Other}&environment=Dev&resource=Secret")).Status);
        Assert.Equal(404, (await app.Call(TestApp.Alice, "GET", $"resources/options?projectId={Other}&environment=Dev")).Status);
        Assert.Equal(404, (await app.Call(TestApp.Bob, "GET", Query("Dev", "Finance"))).Status);
        // Tenant B's domain named in tenant A's selection: not an option of A, refused (never resolved from B's rows).
        Assert.Equal(400, (await app.Call(TestApp.Alice, "GET", Query("Dev", "Secret"))).Status);
        Assert.Equal(404, (await app.Call(TestApp.Alice, "POST", $"projects/{Other}/requests/bundle", Bundle(["Secret"]))).Status);
    }

    [Fact]
    public async Task F1_a_row_whose_I1_tenant_is_another_tenant_is_never_offered()
    {
        var app = await App(Row("G-FIN", "Read", objectId: Id()) with { TenantId = "00000000-0000-4000-8000-0000000000b2" });
        await app.Stores.Projects.ReplaceAsync((await app.Stores.Projects.GetAsync(Project, Project))!.Value with { TenantId = "00000000-0000-4000-8000-0000000000a1" },
            (await app.Stores.Projects.GetAsync(Project, Project))!.ETag);
        Assert.Empty(Options((await app.Call(TestApp.Alice, "GET", $"resources/options?projectId={Project}&environment=Dev")).Json));
    }

    [Fact]
    public async Task F1_All_then_untick_one_resolves_the_rest_only()
    {
        var app = await App(Row("SAMPLE-GRP-FINANCE-READERS", "Read", "Finance", objectId: Id()), Row("SAMPLE-GRP-PEOPLE-ANALYSTS", "Read", "HR", objectId: Id()),
            Row("SAMPLE-GRP-PLATFORM-SUPPORT", "Read", "Platform", "PH", objectId: Id()));
        var all = await Resolve(app, "Dev", ResourceGroupTypes.All);
        Assert.Equal(["Finance", "HR", "PH"], all.GetProperty("selectedResources").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(3, Access(all).Count);
        var explicitAll = await Resolve(app, "Dev", "Finance", "HR", "PH");
        Assert.Equal(all.GetProperty("resolvedAccess").GetRawText(), explicitAll.GetProperty("resolvedAccess").GetRawText());
        var untick = await Resolve(app, "Dev", "Finance", "PH"); // "All" cleared, HR unticked
        Assert.DoesNotContain(Access(untick), a => a.GetProperty("groupName").GetString() == "SAMPLE-GRP-PEOPLE-ANALYSTS");
        Assert.Equal(2, Access(untick).Count);
    }

    [Fact]
    public async Task F1_dropdown_and_checkbox_give_the_same_resolved_list()
    {
        var app = await App(Row("G-FIN-R", "Read", objectId: Id()), Row("G-FIN-W", "Write", objectId: Id(), roles: Contributor), Row("G-HR", "Read", "HR", objectId: Id()));
        var dropdown = await app.Call(TestApp.Alice, "GET", Query("Dev", "Finance"));
        var checkbox = await app.Call(TestApp.Alice, "GET", $"resources/resolve?projectId={Project}&environment=Dev&resources=Finance");
        Assert.Equal(dropdown.Json.GetProperty("resolvedAccess").GetRawText(), checkbox.Json.GetProperty("resolvedAccess").GetRawText());
    }

    [Fact]
    public async Task F1_unicode_and_whitespace_in_names_are_normalised()
    {
        var g = Id();
        var app = await App(
            Row("SAMPLE FIN READERS ", "Read", "Finance ", objectId: g),
            Row("  sample fin readers", "Read", " finance", objectId: g),
            Row("ＳＡＭＰＬＥ-FIN-WRITERS", "Write", "Finance", objectId: Id(), roles: Contributor)); // full-width letters
        var o = Options((await app.Call(TestApp.Alice, "GET", $"resources/options?projectId={Project}&environment=Dev")).Json);
        Assert.Equal(["Finance", ResourceGroupTypes.All], o); // one option, trimmed
        var j = await Resolve(app, "Dev", " FINANCE ");
        var readers = Assert.Single(Access(j), a => a.GetProperty("groupObjectId").GetString() == g);
        Assert.Equal("SAMPLE FIN READERS", readers.GetProperty("groupName").GetString()); // spaces collapsed, NBSP included
        Assert.True(Access(j).Any(a => a.GetProperty("groupName").GetString() == "SAMPLE-FIN-WRITERS"), j.GetRawText());
        Assert.Equal(ResourceResolver.Normalise("Ｐｏｗｅｒ BI_Readers"), ResourceResolver.Normalise("powerbi-readers"));
        Assert.Equal("powerbireaders", ResourceResolver.Normalise(" Power​ BI.Readers\t"));
    }

    [Fact]
    public async Task F1_1000_plus_rows_resolve_within_two_seconds()
    {
        // Realistic names (tenant-env-domain-role); one in five rows repeats a group through a second domain.
        var rows = Enumerable.Range(0, 1200).Select(i => Row($"azr-pes-rg-data-dev-dom{i % 60:00}-weu-role{i / 60:00}{(i % 5 == 0 ? "" : $"-team{i:0000}")}",
            i % 2 == 0 ? "Read" : "Write", $"Domain {i % 60:00}", objectId: Id(), roles: Reader)).ToArray();
        var app = await App(rows);
        var watch = Stopwatch.StartNew();
        var j = await Resolve(app, "Dev", ResourceGroupTypes.All);
        watch.Stop();
        Assert.Equal(1200, Access(j).Count); // every row is its own group (own object id): none merged or hidden
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task F1_prod_above_read_is_needs_human_approval_and_prod_read_is_auto()
    {
        var app = await App(Row("G-FIN-R-PRD", "Read", env: "Prod", objectId: Id(), roles: Reader),
            Row("G-FIN-W-PRD", "Write", env: "Prod", objectId: Id(), roles: Contributor),
            Row("G-FIN-MISLABELLED-PRD", "Read", env: "Prod", objectId: Id(), roles: Contributor));
        var a = Access(await Resolve(app, "Prod", "Finance")).ToDictionary(x => x.GetProperty("groupName").GetString()!, x => x.GetProperty("status").GetString());
        Assert.Equal("Auto", a["G-FIN-R-PRD"]);
        Assert.Equal("NeedsHumanApproval", a["G-FIN-W-PRD"]);
        Assert.Equal("NeedsHumanApproval", a["G-FIN-MISLABELLED-PRD"]); // the label never outranks the roles
    }

    [Fact]
    public async Task F1_retired_deprecated_red_and_expired_rows_are_not_offered()
    {
        var app = await App(Row("G-RET", "Read", "Retired", objectId: Id()) with { Retired = true },
            Row("G-DEP", "Read", "Deprecated", objectId: Id()) with { Deprecated = true },
            Row("G-RED", "Read", "Red", objectId: Id()) with { Flag = RowFlag.Red },
            Row("G-OLD", "Read", "Expired", objectId: Id()) with { EffectiveTo = new DateOnly(2026, 1, 1) },
            Row("G-FUT", "Read", "Future", objectId: Id()) with { EffectiveFrom = new DateOnly(2027, 1, 1) },
            Row("G-OK", "Read", "Finance", objectId: Id()));
        Assert.Equal(["Finance", ResourceGroupTypes.All], Options((await app.Call(TestApp.Alice, "GET", $"resources/options?projectId={Project}&environment=Dev")).Json));
    }

    private static object Bundle(string[] resources, string[]? groups = null, string environment = "Dev") => new
    {
        enterpriseId = "sample.f1@sample-corp.invalid", firstName = "Sample", lastName = "Joiner", environment,
        justification = "SAMPLE: joins the finance reporting team and needs to read the curated finance data for month-end reports.",
        owner = "sample-user-olivia@sample.invalid", guest = false, selectedResources = resources, groups,
        // Forged fields are ignored (identity, project, bundle, status come from the server).
        createdBy = "someone-else", projectId = Other, bundleId = "forged", status = "Completed",
    };

    [Fact]
    public async Task F1_bundle_submit_creates_one_request_per_group_linked_by_a_bundle_and_requires_a_resource()
    {
        var app = await App(Row("G-FIN-R", "Read", objectId: Id(), roles: Reader), Row("G-HR-R", "Read", "HR", objectId: Id(), roles: Reader));
        Assert.Equal(400, (await app.Call(TestApp.Alice, "POST", $"projects/{Project}/requests/bundle", Bundle([]))).Status); // Resource* is mandatory
        var r = await app.Call(TestApp.Alice, "POST", $"projects/{Project}/requests/bundle", Bundle(["Finance", "HR"]));
        Assert.True(r.Status == 201, r.Body);
        var bundle = r.Json.GetProperty("bundleId").GetString();
        Assert.NotEqual("forged", bundle);
        var ids = r.Json.GetProperty("requests").EnumerateArray().Select(x => x.GetProperty("requestId").GetString()!).ToList();
        Assert.Equal(2, ids.Count);
        foreach (var id in ids)
        {
            var req = (await app.Stores.Requests.GetAsync(Project, id))!.Value;
            Assert.Equal((bundle, TestApp.Alice, Project), (req.BundleId, req.CreatedBy, req.ProjectId));
            Assert.Equal(RequestStatus.AiReview, req.Flow!.Status); // each runs the whole audited flow
        }
        Assert.Empty(await app.Stores.Requests.ListAsync(Other));
    }

    [Fact]
    public async Task F1_bundle_with_a_bad_intake_field_saves_nothing()
    {
        var app = await App(Row("G-FIN-R", "Read", objectId: Id(), roles: Reader), Row("G-HR-R", "Read", "HR", objectId: Id(), roles: Reader));
        var bad = await app.Call(TestApp.Alice, "POST", $"projects/{Project}/requests/bundle", new { environment = "Dev", selectedResources = new[] { "Finance", "HR" } });
        Assert.Equal(400, bad.Status);
        Assert.Empty(await app.Stores.Requests.ListAsync(Project));
    }

    [Fact]
    public void F1_banded_edit_distance_matches_a_plain_levenshtein()
    {
        static int Reference(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (var j = 0; j <= b.Length; j++) d[0, j] = j;
            for (var i = 1; i <= a.Length; i++)
                for (var j = 1; j <= b.Length; j++)
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            return d[a.Length, b.Length];
        }
        var random = new Random(30102026);
        string Word() => new(Enumerable.Range(0, random.Next(0, 12)).Select(_ => "abcde"[random.Next(5)]).ToArray());
        for (var n = 0; n < 5000; n++)
        {
            var (a, b) = (Word(), Word());
            if (random.Next(3) == 0) b = "azrpesrgdata" + a + "weu"; // long shared prefix and suffix
            var expected = Reference(a, b);
            Assert.Equal(expected, ResourceResolver.EditDistance(a, b));
            Assert.Equal(Math.Min(expected, 3), ResourceResolver.EditDistance(a, b, 2));
            Assert.Equal(Math.Min(expected, 1), ResourceResolver.EditDistance(a, b, 0));
        }
    }

    [Fact]
    public void F1_resolver_edit_distance_and_options_are_pure_and_deterministic()
    {
        Assert.Equal(2, ResourceResolver.EditDistance("powerbireaders", "pwrbireaders"));
        Assert.True(ResourceResolver.NearDuplicate("PowerBi-Readers", "PwrBI-Readers"));
        Assert.False(ResourceResolver.NearDuplicate("support-reader", "support-writer"));
        Assert.Throws<UnknownResourceException>(() => ResourceResolver.Expand(["Nope"], ["Finance", ResourceGroupTypes.All]));
        Assert.Equal(["Finance"], ResourceResolver.Expand([" finance "], ["Finance", ResourceGroupTypes.All]));
    }
}
