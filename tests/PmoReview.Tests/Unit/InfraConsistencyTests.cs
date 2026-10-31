using System.Text.Json;
using System.Text.RegularExpressions;
using PmoReview.Adapters.Storage;
using PmoReview.Core.Domain;
using Xunit;

namespace PmoReview.Tests.Unit;

/// <summary>Keeps infra/ and tools/ in step with the code, the zero-cost rules (CLAUDE.md 1-2) and "the agent never creates or assigns".</summary>
public partial class InfraConsistencyTests
{
    private static string Repo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PmoReview.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static string Read(params string[] path) => File.ReadAllText(Path.Combine([Repo(), .. path]));

    private static string Pilot() => Read("infra", "modules", "pilot.bicep");

    [Fact]
    public void The_flow_stated_on_the_website_is_the_flow_the_server_mails_and_step_7_never_waits_for_the_sign_in()
    {
        var web = Read("src", "web", "src", "lib", "flow.ts");
        foreach (var step in PmoReview.Core.Workflow.StatusText.FlowOverview) Assert.Contains($"'{step}'", web);
        Assert.DoesNotContain("sign", PmoReview.Core.Workflow.StatusText.FlowOverview[6], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("requester", PmoReview.Core.Workflow.StatusText.FlowOverview[6]);
    }
    private static string Worker() => Read("infra", "modules", "worker.bicep");
    private static string MainBicep() => Read("infra", "main.bicep");

    [Fact]
    public void Bicep_containers_match_code_schema_and_keep_legacy_containers()
    {
        var declared = ContainerLine().Matches(Pilot()).ToDictionary(
            m => m.Groups["name"].Value,
            m => (Pk: m.Groups["pk"].Value, Keys: string.Join(";", PathGroup().Matches(m.Groups["keys"].Value).Select(k => string.Join(",", Quoted().Matches(k.Value).Select(q => q.Groups[1].Value))))));
        var expected = CosmosSchema.Containers.Concat(CosmosSchema.LegacyContainers).ToList();
        Assert.Equal(expected.Count, declared.Count);
        foreach (var (name, pk, uniqueKeys) in expected)
        {
            Assert.True(declared.TryGetValue(name, out var d), $"container {name} missing from Bicep");
            Assert.Equal(pk, d.Pk);
            Assert.Equal(string.Join(";", uniqueKeys.Select(k => string.Join(",", k))), d.Keys);
        }
        foreach (var v2 in new[] { "mappingSheet", "responseDocument", "notifications", "issues" })
            Assert.Contains(CosmosSchema.Containers, c => c.Name == v2);
    }

    [Fact]
    public void Bicep_uses_only_free_settings_and_allowed_types()
    {
        var bicep = MainBicep() + Pilot();
        Assert.Contains("name: 'Free'", bicep);
        Assert.Contains("enableFreeTier: true", bicep);
        Assert.Contains("throughput: 1000", bicep);
        Assert.Contains("totalThroughputLimit: 1000", bicep);
        Assert.DoesNotContain("autoscaleSettings", bicep, StringComparison.OrdinalIgnoreCase);
        string[] allowed =
        [
            "Microsoft.Resources/resourceGroups", "Microsoft.Authorization/policyAssignments", "Microsoft.Consumption/budgets",
            "Microsoft.Web/staticSites", "Microsoft.DocumentDB/databaseAccounts",
            "Microsoft.DocumentDB/databaseAccounts/sqlDatabases", "Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers",
        ];
        var types = ResourceType().Matches(bicep).Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.NotEmpty(types);
        Assert.All(types, t => Assert.Contains(t, allowed));
    }

    [Fact]
    public void Worker_is_least_privilege_identity_only_and_off_by_default()
    {
        var main = MainBicep();
        var bicep = Worker();
        Assert.Contains("param enableHandoff bool = false", main);
        Assert.Contains("param deployWorker bool = false", main);
        Assert.Contains("module worker 'modules/worker.bicep' = if (enableHandoff && deployWorker)", main);

        string[] allowed =
        [
            "Microsoft.DocumentDB/databaseAccounts", "Microsoft.Storage/storageAccounts", "Microsoft.Storage/storageAccounts/blobServices",
            "Microsoft.Storage/storageAccounts/blobServices/containers", "Microsoft.Web/serverfarms", "Microsoft.Web/sites",
            "Microsoft.Web/sites/basicPublishingCredentialsPolicies", "Microsoft.Authorization/roleAssignments",
            "Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments",
        ];
        Assert.All(ResourceType().Matches(bicep).Select(m => m.Groups[1].Value).Distinct(), t => Assert.Contains(t, allowed));
        foreach (var never in new[] { "Microsoft.Insights", "Microsoft.OperationalInsights", "Microsoft.KeyVault", "Microsoft.CognitiveServices", "Microsoft.Network", "Microsoft.ServiceBus", "APPLICATIONINSIGHTS" })
            Assert.DoesNotContain(never, bicep, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("allowSharedKeyAccess: false", bicep);
        Assert.Contains("type: 'SystemAssigned'", bicep);
        Assert.Contains("tier: 'FlexConsumption'", bicep);
        Assert.DoesNotContain("listKeys", bicep);
        Assert.Contains("allow: false", bicep);

        // Cosmos data roles: exactly the containers the agent uses; read-only on projects; never userAccess.
        var writable = Regex.Match(bicep, @"var writableContainers = \[(?<list>[^\]]*)\]").Groups["list"].Value;
        Assert.Equal(["requests", "reviewRuns", "mappingSheet", "responseDocument", "notifications", "issues",
                "peopleInventory", "escalations", "mappingQualityIssues", "groupResolutions", "offboardingRuns", "auditEvents"],
            Quoted().Matches(writable).Select(m => m.Groups[1].Value).ToArray());
        Assert.Contains("/colls/projects'", bicep);
        Assert.Contains("cosmosBuiltInDataReader", bicep);
        Assert.DoesNotContain("/colls/userAccess", bicep);
        Assert.DoesNotContain("/colls/handoffs", bicep);
        Assert.DoesNotContain("/colls/joinerPasses", bicep);
    }

    [Fact]
    public void Retired_create_and_assign_switches_are_forced_off_and_refused_by_the_worker()
    {
        var bicep = Worker();
        foreach (var s in new[] { "ENTRA_AUTO_ASSIGN", "ENTRA_PROVISION_JOINERS", "ENTRA_AUTO_CREATE_PACKAGES" })
            Assert.Contains($"{{ name: '{s}', value: 'false' }}", bicep);
        Assert.Contains("{ name: 'WORKFLOW_SCHEDULE', value: '0 */5 * * * *' }", bicep);
        var worker = Read("src", "worker", "PmoReview.Worker", "Program.cs");
        Assert.Contains("is retired (Phase 2 access packages)", worker);
        Assert.DoesNotContain("GraphEntraAssignmentWriter", worker);
        Assert.DoesNotContain("JoinerProvisioning", worker);
        var src = Directory.GetFiles(Path.Combine(Repo(), "src"), "*.cs", SearchOption.AllDirectories);
        Assert.DoesNotContain(src, f => Regex.IsMatch(File.ReadAllText(f), @"\b(assignmentRequests|assignLicense|accessPackages|resourceRoleScopes)\b"));
        // Agent TAP (Gokul, 22 Oct 2026; off until approved): the one TAP create call lives in the Graph writer; the v1.1 F5
        // offboarding TAP delete (Gokul, 30 Oct 2026; off until approved) in the offboarding writer. Nowhere else.
        Assert.Equal(["GraphDirectoryWriter.cs", "GraphOffboardingWriter.cs"],
            src.Where(f => Regex.IsMatch(File.ReadAllText(f), @"\btemporaryAccessPassMethods\b")).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void Graph_permissions_granted_by_the_deploy_script_are_read_only_and_revocation_needs_approval()
    {
        var deploy = Read("tools", "deploy_free.sh");
        var grant = Regex.Match(deploy, @"GRAPH_READ_ROLES=\((?<r>[^)]*)\)").Groups["r"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["User.Read.All", "GroupMember.Read.All", "UserAuthenticationMethod.Read.All", "AuditLog.Read.All"], grant);
        Assert.All(grant, g => Assert.DoesNotContain("Write", g));
        var retired = Regex.Match(deploy, @"GRAPH_RETIRED_ROLES=\((?<r>[^)]*)\)").Groups["r"].Value;
        foreach (var r in new[] { "EntitlementManagement.ReadWrite.All", "User.ReadWrite.All", "LicenseAssignment.ReadWrite.All", "UserAuthenticationMethod.ReadWrite.All", "Group.ReadWrite.All" })
            Assert.Contains(r, retired);
        Assert.Contains("Would remove: $perm (dry run; pass --approved-by after explicit approval)", deploy);
        // The importer flags rows privileged with the review's own word list, or every request for them is returned.
        var importer = Read("tools", "import_workbook.py");
        var words = Regex.Match(importer, @"PRIVILEGED_WORDS = \[(?<w>[^\]]*)\]").Groups["w"].Value.Split(',').Select(w => w.Trim().Trim('"')).ToArray();
        Assert.Equal(new PmoReview.Core.Review.ReviewPolicy().PrivilegedWords, words);
        // Registration by sign-in name: the tools write the same user id prefix the API reads.
        Assert.Contains($"SIGN_IN_PREFIX = \"{PmoReview.Core.Security.AccessService.SignInPrefix}\"", Read("tools", "import_workbook.py"));
        // `-o tsv` prints a list's elements on separate lines: a "[0].[a, b]" query fed one value per line into a delete (live 22 Oct 2026).
        Assert.DoesNotMatch(@"--query ""\[0\]\.\[", deploy);
        Assert.DoesNotContain("setup_entra", deploy);
        Assert.False(File.Exists(Path.Combine(Repo(), "tools", "setup_entra_package.ps1")));
    }

    [Fact]
    public void Only_the_two_v2_1_writers_write_with_create_calls_and_only_the_worker_holds_them()
    {
        var adapters = Path.Combine(Repo(), "src", "adapters", "PmoReview.Adapters");
        Assert.Equal(["GraphDirectoryReader.cs", "GraphDirectoryWriter.cs", "GraphOffboardingWriter.cs"], Directory.GetFiles(Path.Combine(adapters, "Entra"), "*.cs").Select(Path.GetFileName).Order());
        Assert.Equal(["ArmRoleAssignmentReader.cs", "ArmRoleAssignmentRemover.cs", "ArmRoleAssignmentWriter.cs"], Directory.GetFiles(Path.Combine(adapters, "Rbac"), "*.cs").Select(Path.GetFileName).Order());
        foreach (var reader in new[] { Path.Combine(adapters, "Entra", "GraphDirectoryReader.cs"), Path.Combine(adapters, "Rbac", "ArmRoleAssignmentReader.cs") })
            foreach (var m in new[] { "HttpMethod.Patch", "HttpMethod.Delete", "HttpMethod.Put" })
                Assert.DoesNotContain(m, File.ReadAllText(reader));

        // Graph: POST to the four create URLs only. The one authentication-method call is the agent TAP's create (no phone,
        // password reset or other method; no update or delete); nothing touches licences or packages.
        var graph = File.ReadAllText(Path.Combine(adapters, "Entra", "GraphDirectoryWriter.cs"));
        Assert.Single(Regex.Matches(graph, @"HttpMethod\.\w+"));
        Assert.Contains("HttpMethod.Post", graph);
        Assert.Contains("WriteRefused", graph);
        Assert.Equal(["/authentication/temporaryAccessPassMethods"], Regex.Matches(graph, @"""/authentication/\w+""").Select(m => m.Value.Trim('"')));
        foreach (var banned in new[] { "assignLicense", "accessPackage", "resetPassword", "phoneMethods", "passwordMethods", "/methods/" })
            Assert.DoesNotContain(banned, graph, StringComparison.OrdinalIgnoreCase);

        // Azure RBAC: one PUT of a role assignment; no delete.
        var arm = File.ReadAllText(Path.Combine(adapters, "Rbac", "ArmRoleAssignmentWriter.cs"));
        Assert.Single(Regex.Matches(arm, @"HttpMethod\.\w+"));
        Assert.Contains("HttpMethod.Put", arm);
        Assert.Contains("WriteRefused", arm);

        // F4: the external upload API's key reader only reads Entra's public OpenID metadata (GET, no credential).
        var keys = File.ReadAllText(Path.Combine(adapters, "Uploads", "OpenIdSigningKeys.cs"));
        Assert.DoesNotMatch(@"HttpMethod\.|PostAsync|PutAsync|DeleteAsync|SendAsync|TokenCredential|Authorization", keys);
        Assert.Contains("GetStringAsync", keys);

        // v1.1 F5 offboarding (Gokul's written approval, 30 Oct 2026): exactly revoke sessions (POST), delete a TAP method,
        // remove a membership (always /$ref, F73), disable (PATCH accountEnabled=false only), soft delete; any other URL refused.
        var off = File.ReadAllText(Path.Combine(adapters, "Entra", "GraphOffboardingWriter.cs"));
        Assert.Equal(["HttpMethod.Delete", "HttpMethod.Patch", "HttpMethod.Post"], Regex.Matches(off, @"HttpMethod\.\w+").Select(m => m.Value).Distinct().Order());
        foreach (var expected in new[] { "revokeSignInSessions", "temporaryAccessPassMethods", "/$ref", "{\\\"accountEnabled\\\":false}", "WriteRefused" })
            Assert.Contains(expected, off);
        foreach (var banned in new[] { "assignLicense", "accessPackage", "resetPassword", "passwordProfile", "/methods/" })
            Assert.DoesNotContain(banned, off, StringComparison.OrdinalIgnoreCase);
        var remover = File.ReadAllText(Path.Combine(adapters, "Rbac", "ArmRoleAssignmentRemover.cs"));
        Assert.Equal(["HttpMethod.Delete", "HttpMethod.Get"], Regex.Matches(remover, @"HttpMethod\.\w+").Select(m => m.Value).Distinct().Order());
        Assert.Contains("\"User\"", remover); // users only: never a group or an application
        Assert.Contains("WriteRefused", remover);

        // The website's API never gets a writer: only the worker composes them, and only when switched on.
        Assert.DoesNotContain("Writer", Read("src", "api", "PmoReview.Api", "Program.cs"));
        var worker = Read("src", "worker", "PmoReview.Worker", "Program.cs");
        Assert.Contains("settings.Workflow.AgentProvisioning ? new GraphDirectoryWriter", worker);
        Assert.Contains("settings.Workflow.AgentRoleAssignment", worker);
        Assert.Contains("settings.Workflow.AgentOffboarding ? new GraphOffboardingWriter", worker);
        Assert.Contains("settings.Workflow.AgentOffboarding ? new ArmRoleAssignmentRemover", worker);
        var bicep = MainBicep() + Worker();
        Assert.Contains("param agentOffboarding bool = false", bicep);
        Assert.Contains("{ name: 'AGENT_OFFBOARDING', value: agentOffboarding ? 'true' : 'false' }", bicep);
        Assert.Contains("param offboardDeleteUser bool = false", bicep);
    }

    /// <summary>
    /// The worker's app settings in the template replace all of them on every apply: each setting the worker reads and
    /// another command sets live (tap … --sender, the mail limits, the offboarding switches) is in the template and is
    /// carried from the live worker by `deploy_free.sh params`, so an apply never drops it silently.
    /// </summary>
    [Fact]
    public void An_apply_keeps_the_worker_settings_set_by_other_commands()
    {
        var worker = Worker();
        var deploy = Read("tools", "deploy_free.sh");
        foreach (var (setting, param) in new[] { ("TAP_MAIL_SENDER", "tapMailSender"), ("MAIL_MAX_PER_MINUTE", "mailMaxPerMinute"),
                     ("MAIL_MAX_PER_HOUR", "mailMaxPerHour"), ("AGENT_OFFBOARDING", "agentOffboarding"), ("OFFBOARD_DELETE_USER", "offboardDeleteUser"),
                     ("OFFBOARD_SUBSCRIPTIONS", "offboardSubscriptions"), ("AGENT_TAP", "agentTap"), ("AGENT_PROVISIONING", "agentProvisioning") })
        {
            Assert.Contains($"{{ name: '{setting}', value: {param}", worker);
            Assert.Contains($"\"{param}\": v(live.get(\"{setting}\"", deploy);
        }
        Assert.Contains("removed by the apply:", deploy); // whatif names any live setting the template would drop
    }

    [Fact]
    public void Provisioning_is_off_by_default_and_switched_on_only_with_approval()
    {
        var bicep = MainBicep() + Worker();
        Assert.Contains("param agentProvisioning bool = false", bicep);
        Assert.Contains("param agentRoleAssignment bool = false", bicep);
        Assert.Contains("{ name: 'AGENT_PROVISIONING', value: agentProvisioning ? 'true' : 'false' }", bicep);
        var deploy = Read("tools", "deploy_free.sh");
        var grant = Regex.Match(deploy, @"GRAPH_PROVISION_ROLES=\((?<r>[^)]*)\)").Groups["r"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["User.Create", "Group.Create", "GroupMember.ReadWrite.All"], grant); // least privileged (F56-F58)
        Assert.Contains("Dry run: nothing changed.", deploy);
        Assert.Contains("--condition-version 2.0", deploy);
        Assert.Contains("StringEqualsIgnoreCase $types", deploy);
        Assert.Contains("RBAC_ADMIN_ROLE_ID=f58310d9-a9f6-439a-9e8d-f62e7b41a168", deploy);
    }

    /// <summary>
    /// v1.1 F5 option 3, Entra only (Gokul, 30 Oct 2026): `offboarding … on` grants only the two Graph permissions the writes
    /// need (F71, F74), never a broad one or an Azure right, is a dry run without approval, names the subscriptions, and the
    /// website follows the worker's switch on every app deploy.
    /// </summary>
    [Fact]
    public void Offboarding_is_switched_on_only_with_approval_and_only_with_the_two_entra_permissions()
    {
        var deploy = Read("tools", "deploy_free.sh");
        var fn = Regex.Match(deploy, @"agent_offboarding\(\) \{.*?\n\}", RegexOptions.Singleline).Value;
        var grant = Regex.Match(deploy, @"GRAPH_OFFBOARD_NEW=\((?<r>[^)]*)\)").Groups["r"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["User.RevokeSessions.All", "User.EnableDisableAccount.All"], grant);
        Assert.Contains("Dry run: nothing changed.", fn);
        Assert.Contains("if [[ -z \"${APPROVED_BY:-}\" ]]", fn);
        Assert.Contains("subs=\"${subs:-$SUB}\"", fn); // never empty: an empty list may mean no subscription is seen
        foreach (var never in new[] { "Directory.Read.All", "User.ReadWrite.All", "az role assignment create", "RoleManagement" })
            Assert.DoesNotContain(never, fn);
        // Deleting (Gokul, 30 Oct 2026) only with --delete-user: then and only then User.ReadWrite.All (F75) and the setting on;
        // "off" and "delete-off" switch it off without approval.
        var delete = Regex.Match(deploy, @"GRAPH_OFFBOARD_DELETE=\((?<r>[^)]*)\)").Groups["r"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["User.ReadWrite.All"], delete);
        Assert.Contains("if [[ \"$delete\" == \"yes\" ]]; then del=true; perms+=(\"${GRAPH_OFFBOARD_DELETE[@]}\")", fn);
        Assert.Contains("del=false;", fn);
        Assert.Contains("\"OFFBOARD_DELETE_USER=$del\"", fn);
        Assert.Contains("AGENT_OFFBOARDING=false OFFBOARD_DELETE_USER=false", fn);
        Assert.Contains("--settings OFFBOARD_DELETE_USER=false", fn);
        Assert.True(fn.IndexOf("if [[ -z \"${APPROVED_BY:-}\" ]]", StringComparison.Ordinal) < fn.IndexOf("appRoleAssignments", StringComparison.Ordinal));
        Assert.Contains("--delete-user) OFF_DELETE=yes; shift ;;", deploy);
        Assert.Contains("agent_offboarding \"$MODE\" \"$OFF_SUBS\" \"$OFF_DELETE\"", deploy);
        Assert.Contains("\"AGENT_OFFBOARDING=${offb:-false}\"", deploy);
    }

    [Fact]
    public void Privileged_rows_are_off_by_default_and_privileged_administrator_roles_never_reach_the_agents_allow_list()
    {
        var bicep = MainBicep() + Worker();
        Assert.Contains("param agentPrivileged bool = false", bicep);
        Assert.Contains("{ name: 'AGENT_PRIVILEGED', value: agentPrivileged ? 'true' : 'false' }", bicep);
        var deploy = Read("tools", "deploy_free.sh");
        var never = Regex.Match(deploy, "NEVER_BY_AGENT=\"(?<ids>[^\"]*)\"").Groups["ids"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(BuiltInRoles.NeverByAgent.Values.Order(), never.Order()); // same list as the code (F68)
        Assert.Contains("check_role_ids \"$roles\" || return 1", deploy); // provisioning refuses them in the RBAC condition
        Assert.Contains("agent_privileged \"$MODE\"", deploy);
        Assert.Contains("\"AGENT_PRIVILEGED=${priv:-false}\"", deploy); // the website follows the worker

        // --from-sheet: the Python catalogue and never-list are the code's (tools/sheet_roles.py).
        Assert.Contains("\"$ROOT/tools/sheet_roles.py\" \"$FROM_SHEET\" \"$scope\"", deploy);
        var py = Read("tools", "sheet_roles.py");
        Dictionary<string, string> Dict(string name) => Regex.Matches(Regex.Match(py, name + @" = \{(?<b>[^}]*)\}").Groups["b"].Value, "\"(?<k>[^\"]+)\": \"(?<v>[0-9a-f-]{36})\"")
            .ToDictionary(m => m.Groups["k"].Value, m => m.Groups["v"].Value);
        Assert.Equal(BuiltInRoles.Ids.ToDictionary(p => p.Key.ToLowerInvariant(), p => p.Value).OrderBy(p => p.Key), Dict("CATALOGUE").OrderBy(p => p.Key));
        Assert.Equal(BuiltInRoles.NeverByAgent.ToDictionary(p => p.Key.ToLowerInvariant(), p => p.Value).OrderBy(p => p.Key), Dict("NEVER").OrderBy(p => p.Key));
    }

    [Fact]
    public void Budget_is_the_approved_cap_with_staged_alerts()
    {
        var bicep = MainBicep() + Pilot();
        Assert.Contains("param budgetAmount int = 2000", bicep);
        foreach (var t in new[] { "threshold: 50", "threshold: 80", "threshold: 100" }) Assert.Contains(t, bicep);
    }

    [Fact]
    public void Worker_target_framework_matches_flex_runtime()
    {
        var csproj = Read("src", "worker", "PmoReview.Worker", "PmoReview.Worker.csproj");
        var tfm = Regex.Match(csproj, "<TargetFramework>net(?<v>[0-9.]+)</TargetFramework>").Groups["v"].Value;
        Assert.Contains($"param workerRuntimeVersion string = '{tfm}'", Worker());
        Assert.Contains($"param workerRuntimeVersion string = '{tfm}'", MainBicep());
    }

    /// <summary>
    /// The SWA managed API runtime and the API project must agree (VERIFIED_FACTS F21), and the libraries the API uses
    /// must build for it. The API is back on dotnet-isolated:9.0 (31 Oct 2026: the live 10.0 deploy did not start).
    /// </summary>
    [Fact]
    public void Api_target_framework_matches_swa_api_runtime()
    {
        var config = JsonDocument.Parse(Read("staticwebapp.config.json"));
        var runtime = config.RootElement.GetProperty("platform").GetProperty("apiRuntime").GetString()!;
        var tfm = Regex.Match(Read("src", "api", "PmoReview.Api", "PmoReview.Api.csproj"), "<TargetFramework>net(?<v>[0-9.]+)</TargetFramework>").Groups["v"].Value;
        Assert.Equal($"dotnet-isolated:{tfm}", runtime);
        Assert.Contains(tfm, new[] { "9.0", "10.0" });
        var cli = JsonDocument.Parse(Read("src", "web", "package.json")).RootElement.GetProperty("devDependencies").GetProperty("@azure/static-web-apps-cli").GetString()!;
        Assert.True(Version.Parse(cli) >= new Version(2, 1, 0), $"SWA CLI {cli} rejects dotnet-isolated:10.0 (F21)");
        if (tfm != "10.0")
            foreach (var library in new[] { Read("src", "core", "PmoReview.Core", "PmoReview.Core.csproj"), Read("src", "adapters", "PmoReview.Adapters", "PmoReview.Adapters.csproj") })
                Assert.Contains($"net{tfm};", library);
    }

    /// <summary>Handoff task 3: sign-out must land on a public page, or the 401 override signs the user straight back in.</summary>
    [Fact]
    public void Sign_out_lands_on_a_public_page_before_the_authenticated_catch_all()
    {
        var config = JsonDocument.Parse(Read("staticwebapp.config.json")).RootElement;
        var routes = config.GetProperty("routes").EnumerateArray().ToList();
        var logout = routes.Single(r => r.GetProperty("route").GetString() == "/logout");
        Assert.Equal("/.auth/logout?post_logout_redirect_uri=/signed-out.html", logout.GetProperty("redirect").GetString());
        var page = routes.FindIndex(r => r.GetProperty("route").GetString() == "/signed-out.html");
        var catchAll = routes.FindIndex(r => r.GetProperty("route").GetString() == "/*");
        Assert.True(page >= 0 && page < catchAll);
        Assert.Equal(["anonymous"], routes[page].GetProperty("allowedRoles").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.Contains("/signed-out.html", config.GetProperty("navigationFallback").GetProperty("exclude").EnumerateArray().Select(x => x.GetString()));
        var html = Read("src", "web", "public", "signed-out.html");
        Assert.Contains("href=\"/login\"", html);
        Assert.DoesNotContain("<script", html);
    }

    [GeneratedRegex(@"\{ name: '(?<name>\w+)', pk: '(?<pk>[^']+)', uniqueKeys: \[(?<keys>.*?)\](, defaultTtl: -?\d+)? \}( //.*)?$", RegexOptions.Multiline)]
    private static partial Regex ContainerLine();

    [GeneratedRegex(@"paths: \[[^\]]*\]")]
    private static partial Regex PathGroup();

    [GeneratedRegex(@"'([^']+)'")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"^resource \w+ '([^@']+)@", RegexOptions.Multiline)]
    private static partial Regex ResourceType();

    /// <summary>
    /// v1.1 done-criterion 7: every verified fact cites a learn.microsoft.com page, and every fact id that PERMISSIONS.md,
    /// HOSTING.md and EXTERNAL_UPLOAD_API.md cite exists (so each fact there traces to a Learn page).
    /// </summary>
    [Fact]
    public void Every_verified_fact_cites_learn_and_the_v11_documents_cite_only_existing_facts()
    {
        var facts = Regex.Matches(Read("docs", "VERIFIED_FACTS.md"), @"^\| ?(F\d+) \|.*$", RegexOptions.Multiline)
            .ToDictionary(m => m.Groups[1].Value, m => m.Value);
        Assert.True(facts.Count >= 103, $"{facts.Count} facts");
        Assert.All(facts, f => Assert.True(f.Value.Contains("https://learn.microsoft.com/", StringComparison.Ordinal), $"{f.Key} cites no learn.microsoft.com page"));
        foreach (var doc in new[] { "PERMISSIONS.md", "HOSTING.md", "EXTERNAL_UPLOAD_API.md" })
        {
            // F1–F5 in these documents name the v1.1 features, not facts: only F6 and above count as fact citations.
            var cited = Regex.Matches(Read("docs", doc), @"\bF(\d{1,3})\b").Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
                .Where(n => n > 5).Distinct().Select(n => $"F{n}").ToList();
            Assert.True(cited.Count >= 5, $"{doc} cites only {cited.Count} facts");
            Assert.All(cited, id => Assert.True(facts.ContainsKey(id), $"{doc} cites {id}, which VERIFIED_FACTS.md does not have"));
            // Claim level: every line that names a Graph permission, an ARM action or a Microsoft HTTP call cites a fact on
            // the same line (this app's own /api routes are not platform facts). Code blocks are examples, not claims.
            var inCode = false;
            foreach (var (line, n) in Read("docs", doc).Split('\n').Select((l, i) => (l, i + 1)))
            {
                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) { inCode = !inCode; continue; }
                if (inCode || !PlatformClaim().IsMatch(line.Replace("/api/", "", StringComparison.Ordinal))) continue;
                Assert.True(FactCitation().IsMatch(line), $"{doc}:{n} names a permission or API but cites no fact: {line[..Math.Min(line.Length, 120)]}");
            }
        }
    }

    [GeneratedRegex(@"`[A-Z][A-Za-z]+(?:\.[A-Za-z-]+)*\.(?:All|Send)`|`Microsoft\.[A-Za-z]+/[^`]+`|\b(?:GET|POST|PUT|PATCH|DELETE) (?:/|https://|\{)")]
    private static partial Regex PlatformClaim();

    [GeneratedRegex(@"\bF(?:[6-9]|\d{2,3})\b|learn\.microsoft\.com")]
    private static partial Regex FactCitation();
}
