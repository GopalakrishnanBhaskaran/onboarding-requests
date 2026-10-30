using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PmoReview.Adapters;
using PmoReview.Core.Domain;
using PmoReview.Core.Uploads;
using PmoReview.Core.Workflow;
using PmoReview.Tests.Support;
using Xunit;

namespace PmoReview.Tests.Unit;

/// <summary>
/// v1.1 F4: the Data uploads page (DataMaintainer) and the external upload API (app token, off by default) — validate,
/// diff, upsert by key, versions, rollback — with one pipeline. Each F4 edge case of the feature pack (B.8) is one test.
/// </summary>
public class F4UploadTests
{
    internal const string T = TestTokens.Tenant;
    internal const string Dana = "sample-user-dana"; // DataMaintainer (SAMPLE-ALPHA, SAMPLE-PEST)
    internal const string Owner = "sample-user-olivia@sample.invalid";
    internal static readonly DateOnly From = new(2026, 10, 1); // before the test clock (17 Oct 2026): offered at once

    internal static PilotSettings Settings(bool external = true, int maxUpload = 5 * 1024 * 1024, bool provisioning = false) => new()
    {
        Workflow = new WorkflowSettings
        {
            AgentTenantId = T, ExternalUploadApiEnabled = external, ExternalUploadAudience = TestTokens.Audience, MaxUploadBytes = maxUpload,
            AgentProvisioning = provisioning, AgentRoleAssignment = provisioning,
            RoleAssignAllowedRoleIds = [FakeRoleWriter.Reader], RoleAssignScopes = [TestApp.SampleAccessScope],
        },
    };

    /// <summary>SAMPLE-PEST bound to the test tenant (the agent's), so TenantId rows and app tokens can match it.</summary>
    internal static async Task<(TestApp App, TestTokens Tokens)> App(bool external = true, int maxUpload = 5 * 1024 * 1024, bool provisioning = false)
    {
        var tokens = new TestTokens();
        var app = TestApp.With(settings: Settings(external, maxUpload, provisioning), signingKeys: tokens);
        var p = (await app.Stores.Projects.GetAsync(TestApp.Pest, TestApp.Pest))!;
        await app.Stores.Projects.ReplaceAsync(p.Value with { TenantId = T }, p.ETag);
        return (app, tokens);
    }

    internal static object?[] Row(string group, string level = "Read", string env = "Dev", string domain = "Finance", string type = "Domain", string? oid = null,
        string owner = Owner, object? from = null, object? to = null, string tenant = T) =>
        [tenant, domain, type, env, level, group, oid, owner, from ?? From, to];

    internal static byte[] MappingCsv(params object?[][] rows) => UploadFiles.Csv([UploadFiles.MappingHeader, .. rows]);

    internal static object?[] PersonRow(string id, string? mail = null, string status = "Active", string name = "SAMPLE Person", string? alternate = null) =>
        [id, mail ?? id, name, "SAMPLE-1", "Finance", "sample-user-olivia@sample.invalid", status, alternate];

    private static JsonElement Upload(Resp r) => r.Json.GetProperty("upload");
    private static string StatusOf(Resp r) => Upload(r).GetProperty("status").GetString()!;
    private static string[] Codes(JsonElement upload) => upload.GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("code").GetString()!).ToArray();
    private static (int, int, int, int) Counts(JsonElement upload)
    {
        var c = upload.GetProperty("counts");
        return (c.GetProperty("added").GetInt32(), c.GetProperty("updated").GetInt32(), c.GetProperty("unchanged").GetInt32(), c.GetProperty("retired").GetInt32());
    }

    private static async Task<List<MappingRow>> Uploaded(TestApp app) =>
        (await app.Stores.MappingSheet.ListAsync(TestApp.Pest)).Select(v => v.Value).Where(r => r.RowId.StartsWith("U-", StringComparison.Ordinal)).ToList();

    private static async Task<List<DataVersion>> Versions(TestApp app, DataKind kind = DataKind.Mapping) =>
        (await app.Stores.VersionsOf(kind).ListAsync(TestApp.Pest)).Select(v => v.Value).Where(v => v.Part == 0 && v.Id != DataVersion.HeadId(kind)).ToList();

    private static async Task<Resp> Apply(TestApp app, byte[] file, string name = "mapping.csv", bool retire = false, int? baseVersion = null, string kind = "mapping")
    {
        var r = await app.Upload(Dana, TestApp.Pest, kind, file, name, "apply", retire, baseVersion);
        return r;
    }

    // ---- B.8 F4 edge cases --------------------------------------------------------------------------------------------

    [Fact]
    public async Task F4_Missing_or_renamed_column_stops_the_file()
    {
        var (app, _) = await App();
        var header = UploadFiles.MappingHeader.Select(h => h == "EntraGroupName" ? "Entra Group Name" : h).Where(h => h != "Owner").ToArray();
        var file = UploadFiles.Csv([header, ["x", "", "", "", "", "", "", "", ""]]);
        var r = await app.Upload(Dana, TestApp.Pest, "mapping", file, "mapping.csv");
        Assert.Equal((200, "Invalid"), (r.Status, StatusOf(r)));
        var errors = Upload(r).GetProperty("errors").EnumerateArray().ToList();
        Assert.Equal(["MissingColumn", "MissingColumn"], errors.Select(e => e.GetProperty("code").GetString()));
        Assert.Contains(errors, e => e.GetProperty("message").GetString()!.Contains("the file has \"Entra Group Name\". Rename it to \"EntraGroupName\""));
        Assert.All(errors, e => Assert.Equal(0, e.GetProperty("row").GetInt32())); // no row was looked at

        var apply = await Apply(app, file);
        Assert.Equal(422, apply.Status);
        Assert.Empty(await Uploaded(app));
        Assert.Empty(await Versions(app));
    }

    [Fact]
    public async Task F4_Wrong_tenant_rows_block_the_whole_file()
    {
        var (app, _) = await App();
        var file = MappingCsv(Row("SAMPLE-FIN-READERS"), Row("SAMPLE-FIN-WRITERS", "Write", tenant: "00000000-0000-4000-8000-000000000001"), Row("SAMPLE-X", tenant: "not-a-guid"));
        var r = await Apply(app, file);
        Assert.Equal(422, r.Status);
        var upload = r.Json.GetProperty("upload");
        Assert.Equal(["WrongTenant", "InvalidGuid"], Codes(upload));
        Assert.Equal([3, 4], upload.GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("row").GetInt32()));
        Assert.Empty(await Uploaded(app)); // the valid first row was not applied either: no partial apply
    }

    [Fact]
    public async Task F4_Duplicate_rows_and_duplicate_keys_are_refused()
    {
        var (app, _) = await App();
        var r = await app.Upload(Dana, TestApp.Pest, "mapping", MappingCsv(
            Row("SAMPLE-FIN-READERS"), Row("SAMPLE-FIN-READERS"), Row("sample-fin-readers ", owner: "sample-user-otto@sample.invalid")), "mapping.csv");
        var errors = Upload(r).GetProperty("errors").EnumerateArray().Select(e => (e.GetProperty("row").GetInt32(), e.GetProperty("code").GetString())).ToList();
        Assert.Equal([(3, "DuplicateRow"), (4, "DuplicateKey")], errors);

        var people = UploadFiles.Csv([UploadFiles.InventoryHeader, PersonRow("sample.a@sample.invalid"), PersonRow("SAMPLE.A@sample.invalid", name: "Other")]);
        var p = await app.Upload(Dana, TestApp.Pest, "inventory", people, "people.csv");
        Assert.Equal(["DuplicateKey"], Codes(Upload(p)));
    }

    [Fact]
    public async Task F4_Macro_and_binary_files_are_refused()
    {
        var (app, _) = await App();
        var rows = new[] { UploadFiles.MappingHeader.Cast<object?>().ToArray(), Row("SAMPLE-FIN-READERS") };
        async Task<string> CodeOf(byte[] file, string name) => Codes(Upload(await app.Upload(Dana, TestApp.Pest, "mapping", file, name))).Single();

        Assert.Equal("MacroFile", await CodeOf(UploadFiles.Xlsx(rows), "mapping.xlsm"));
        Assert.Equal("MacroFile", await CodeOf(UploadFiles.Xlsx([("Sheet1", rows)], macro: true), "mapping.xlsx"));
        Assert.Equal("MacroFile", await CodeOf(UploadFiles.Xlsx([("Sheet1", rows)], vbaPart: true), "mapping.xlsx"));
        Assert.Equal("MacroFile", await CodeOf(UploadFiles.Xlsx(rows), "mapping.xlsb"));
        Assert.Equal("UnsupportedFormat", await CodeOf([0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1], "mapping.xls"));
        Assert.Equal("UnsupportedFormat", await CodeOf(UploadFiles.Xlsx(rows), "mapping.csv")); // a workbook renamed to .csv
        Assert.Equal("UnsupportedFormat", await CodeOf(Encoding.UTF8.GetBytes("a,b"), "mapping.txt"));
        Assert.Equal("InvalidFile", await CodeOf(Encoding.UTF8.GetBytes("not a zip"), "mapping.xlsx"));
        Assert.Empty(await Versions(app));
    }

    [Fact]
    public async Task F4_Empty_file_header_only_and_blank_lines_are_refused()
    {
        var (app, _) = await App();
        Assert.Equal(["EmptyFile"], Codes(Upload(await app.Upload(Dana, TestApp.Pest, "mapping", [], "mapping.csv"))));
        Assert.Equal(["EmptyFile"], Codes(Upload(await app.Upload(Dana, TestApp.Pest, "mapping", Encoding.UTF8.GetBytes("\r\n ,, \r\n\r\n"), "mapping.csv"))));
        Assert.Equal(["NoRows"], Codes(Upload(await app.Upload(Dana, TestApp.Pest, "mapping", MappingCsv(), "mapping.csv"))));
        Assert.Equal(["NoRows"], Codes(Upload(await app.Upload(Dana, TestApp.Pest, "inventory", UploadFiles.Csv([UploadFiles.InventoryHeader]), "people.csv"))));
    }

    [Fact]
    public async Task F4_Huge_files_too_many_rows_and_zip_bombs_are_refused()
    {
        var (app, _) = await App(maxUpload: 4096);
        var big = MappingCsv([.. Enumerable.Range(0, 80).Select(i => Row($"SAMPLE-GROUP-{i:000}"))]);
        Assert.True(big.Length > 4096);
        Assert.Equal(["FileTooLarge"], Codes(Upload(await app.Upload(Dana, TestApp.Pest, "mapping", big, "mapping.csv"))));
        // Over the route limit (file limit + multipart framing): refused before anything reads it.
        var r = await app.Upload(Dana, TestApp.Pest, "mapping", new byte[4096 + 70 * 1024], "mapping.csv");
        Assert.Equal(413, r.Status);

        var (roomy, _) = await App();
        var many = MappingCsv([.. Enumerable.Range(0, TabularFile.MaxRows + 1).Select(i => Row($"G{i}"))]);
        Assert.Equal(["TooManyRows"], Codes(Upload(await roomy.Upload(Dana, TestApp.Pest, "mapping", many, "mapping.csv"))));

        // 70 MB of zeros compresses to a few KB: refused from the package sizes, never unpacked.
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        await using (var s = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.SmallestSize).Open())
        {
            var zeros = new byte[1024 * 1024];
            for (var i = 0; i < 70; i++) await s.WriteAsync(zeros);
        }
        Assert.True(ms.Length < 1024 * 1024);
        Assert.Equal(["FileTooLarge"], Codes(Upload(await roomy.Upload(Dana, TestApp.Pest, "mapping", ms.ToArray(), "mapping.xlsx"))));
    }

    [Fact]
    public async Task F4_Reupload_of_an_identical_file_makes_no_new_version()
    {
        var (app, _) = await App();
        var file = MappingCsv(Row("SAMPLE-FIN-READERS"), Row("SAMPLE-FIN-WRITERS", "Write"));
        var first = await Apply(app, file);
        Assert.Equal((200, "Applied", "v1"), (first.Status, StatusOf(first), Upload(first).GetProperty("version").GetString()));
        Assert.Equal((2, 0, 0, 0), Counts(Upload(first)));

        var again = await Apply(app, file, baseVersion: 1);
        Assert.Equal((200, "Applied", "v1"), (again.Status, StatusOf(again), Upload(again).GetProperty("version").GetString()));
        Assert.Equal((0, 0, 2, 0), Counts(Upload(again)));
        Assert.Single(await Versions(app));
        Assert.All(await Uploaded(app), r => Assert.Equal(1, r.Version));
    }

    [Fact]
    public async Task F4_Retire_missing_rows_off_leaves_them_on_retires_them()
    {
        var (app, _) = await App();
        // The sheet already holds the SAMPLE PEST rows (imported before v1.1): they are missing from these files too.
        var before = (await app.Stores.MappingSheet.ListAsync(TestApp.Pest)).Count(v => !v.Value.Retired);
        Assert.Equal(200, (await Apply(app, MappingCsv(Row("SAMPLE-FIN-READERS"), Row("SAMPLE-FIN-WRITERS", "Write")))).Status);
        var only = MappingCsv(Row("SAMPLE-FIN-READERS"));

        var off = await Apply(app, only, baseVersion: 1);
        Assert.Equal((0, 0, 1, 0), Counts(Upload(off)));
        Assert.Equal(before + 1, off.Json.GetProperty("missing").GetInt32());
        Assert.Contains(off.Json.GetProperty("diff").EnumerateArray(), d => d.GetProperty("change").GetString() == "Missing" && d.GetProperty("label").GetString()!.StartsWith("SAMPLE-FIN-WRITERS"));
        Assert.Equal(before + 2, (await app.Stores.MappingSheet.ListAsync(TestApp.Pest)).Count(v => !v.Value.Retired)); // nothing retired
        Assert.Single(await Versions(app)); // nothing to write: no new version

        var on = await app.Upload(Dana, TestApp.Pest, "mapping", only, "mapping.csv", "apply", retireMissing: true, baseVersion: 1);
        Assert.Equal(("Applied", "v2"), (StatusOf(on), Upload(on).GetProperty("version").GetString()));
        Assert.Equal((0, 0, 1, before + 1), Counts(Upload(on)));
        Assert.Equal(1, (await app.Stores.MappingSheet.ListAsync(TestApp.Pest)).Count(v => !v.Value.Retired));
        var writers = Assert.Single(await Uploaded(app), r => r.EntraGroup == "SAMPLE-FIN-WRITERS");
        Assert.True(writers.Retired);
        // A retired row is never offered (F1) — and it is still there (never deleted).
        var resolve = await app.Call(TestApp.Alice, "GET", $"resources/resolve?projectId={TestApp.Pest}&environment=Dev&resource=Finance");
        Assert.DoesNotContain("SAMPLE-FIN-WRITERS", resolve.Body);
        Assert.Contains("SAMPLE-FIN-READERS", resolve.Body);
    }

    [Fact]
    public async Task F4_Concurrent_uploads_are_refused_by_the_version_and_the_head_etag()
    {
        var (app, _) = await App();
        var a = MappingCsv(Row("SAMPLE-FIN-READERS"));
        var b = MappingCsv(Row("SAMPLE-FIN-WRITERS", "Write"));
        // Two maintainers validate against v0; the first applies, the second is told to validate again.
        Assert.Equal(0, (await app.Upload(Dana, TestApp.Pest, "mapping", a, "a.csv")).Json.GetProperty("baseVersion").GetInt32());
        Assert.Equal(0, (await app.Upload(Dana, TestApp.Pest, "mapping", b, "b.csv")).Json.GetProperty("baseVersion").GetInt32());
        Assert.Equal(200, (await Apply(app, a, baseVersion: 0)).Status);
        var late = await Apply(app, b, baseVersion: 0);
        Assert.Equal((409, "stale_upload"), (late.Status, late.ErrorCode));

        // Another apply holds the head (claimed with its ETag a moment ago): refused, nothing written.
        var head = (await app.Stores.MappingVersions.GetAsync(TestApp.Pest, DataVersion.HeadId(DataKind.Mapping)))!;
        await app.Stores.MappingVersions.ReplaceAsync(head.Value with { Applying = 2, ApplyingBy = "someone", ApplyingSince = app.Clock.UtcNow }, head.ETag);
        var busy = await Apply(app, b, baseVersion: 1);
        Assert.Equal((409, "upload_in_progress"), (busy.Status, busy.ErrorCode));
        Assert.DoesNotContain(await Uploaded(app), r => r.EntraGroup == "SAMPLE-FIN-WRITERS");

        // That apply crashed (claim older than 10 minutes, a row half-written from its parts): undone, then this one runs.
        var readers = (await app.Stores.MappingSheet.ListAsync(TestApp.Pest)).Single(v => v.Value.EntraGroup == "SAMPLE-FIN-READERS");
        await app.Stores.MappingVersions.CreateAsync(new DataVersion
        {
            Id = DataVersion.MakeId(DataKind.Mapping, 2, 1), ProjectId = TestApp.Pest, Kind = DataKind.Mapping, Version = 2, Part = 1, Parts = 1,
            Before = [PmoReview.Core.Common.AppJson.Serialize(readers.Value)],
        });
        await app.Stores.MappingSheet.ReplaceAsync(readers.Value with { Owner = "crashed@sample.invalid", Version = readers.Value.Version + 1 }, readers.ETag);
        app.Clock.Advance(TimeSpan.FromMinutes(11));
        var after = await Apply(app, b, baseVersion: 1);
        Assert.Equal((200, "v2"), (after.Status, Upload(after).GetProperty("version").GetString()));
        Assert.Equal(Owner, (await Uploaded(app)).Single(r => r.EntraGroup == "SAMPLE-FIN-READERS").Owner);
        var finalHead = (await app.Stores.MappingVersions.GetAsync(TestApp.Pest, DataVersion.HeadId(DataKind.Mapping)))!.Value;
        Assert.Equal((2, (int?)null), (finalHead.Version, finalHead.Applying));
    }

    [Fact]
    public async Task F4_Rollback_restores_the_previous_version_in_one_click()
    {
        var (app, _) = await App();
        await Apply(app, MappingCsv(Row("SAMPLE-FIN-READERS"), Row("SAMPLE-FIN-WRITERS", "Write")));
        var v2 = await Apply(app, MappingCsv(Row("SAMPLE-FIN-READERS", owner: "sample-user-otto@sample.invalid"), Row("SAMPLE-FIN-WRITERS", "Write"), Row("SAMPLE-FIN-ADMINS", "Admin")), baseVersion: 1);
        Assert.Equal((1, 1, 1, 0), Counts(Upload(v2)));

        var older = await app.Call(Dana, "POST", $"projects/{TestApp.Pest}/uploads/mapping/rollback", new { version = 1 });
        Assert.Equal((409, "not_latest"), (older.Status, older.ErrorCode));
        Assert.Equal(403, (await app.Call(TestApp.Paul, "POST", $"projects/{TestApp.Pest}/uploads/mapping/rollback", new { version = 2 })).Status);

        var back = await app.Call(Dana, "POST", $"projects/{TestApp.Pest}/uploads/mapping/rollback", new { version = 2 });
        Assert.Equal(200, back.Status);
        Assert.Equal(("v3", 2), (back.Json.GetProperty("version").GetString(), back.Json.GetProperty("rollbackOf").GetInt32()));
        var rows = await Uploaded(app);
        Assert.Equal(Owner, rows.Single(r => r.EntraGroup == "SAMPLE-FIN-READERS").Owner);
        Assert.True(rows.Single(r => r.EntraGroup == "SAMPLE-FIN-ADMINS").Retired);
        Assert.False(rows.Single(r => r.EntraGroup == "SAMPLE-FIN-WRITERS").Retired);
        var versions = await Versions(app);
        Assert.Equal(DataVersionState.RolledBack, versions.Single(v => v.Version == 2).State);
        Assert.Contains((await app.Stores.AuditEvents.ListAsync(TestApp.Pest)).Select(a => a.Value), a => a.Action == "upload-rolled-back");
        var overview = await app.Call(Dana, "GET", $"projects/{TestApp.Pest}/uploads");
        Assert.Equal(3, overview.Json.GetProperty("mapping").GetProperty("current").GetInt32());

        // A row changed after the upload (PMO edits it in the app): rolling back would overwrite it, so it is refused.
        var v4 = await Apply(app, MappingCsv(Row("SAMPLE-FIN-READERS", owner: "sample-user-sam@sample.invalid")), baseVersion: 3);
        Assert.Equal("v4", Upload(v4).GetProperty("version").GetString());
        var edited = (await app.Stores.MappingSheet.ListAsync(TestApp.Pest)).Single(v => v.Value.EntraGroup == "SAMPLE-FIN-READERS");
        await app.Stores.MappingSheet.ReplaceAsync(edited.Value with { Notes = "PMO note", Version = edited.Value.Version + 1 }, edited.ETag);
        var refused = await app.Call(Dana, "POST", $"projects/{TestApp.Pest}/uploads/mapping/rollback", new { version = 4 });
        Assert.Equal((409, "changed_since_upload"), (refused.Status, refused.ErrorCode));
    }

    [Fact]
    public async Task F4_External_api_without_the_app_role_or_a_valid_token_is_refused()
    {
        var (app, tokens) = await App();
        var now = app.Clock.UtcNow;
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { rows = new[] { new Dictionary<string, object?> { ["TenantId"] = T } } }));
        Task<Resp> Call(string? token, string key = "k-1") => app.SendRaw("POST", "v1/uploads/mapping?mode=validate", Headers(token, key), body);

        Assert.Equal((403, "missing_app_role"), Pair(await Call(tokens.Token(now, roles: []))));
        Assert.Equal((403, "missing_app_role"), Pair(await Call(tokens.Token(now, roles: ["Uploads.Read"]))));
        Assert.Equal((401, "invalid_token"), Pair(await Call(null)));
        Assert.Equal((401, "invalid_token"), Pair(await Call("not-a-jwt")));
        Assert.Equal((401, "invalid_token"), Pair(await Call(tokens.Token(now, audience: "00000000-0000-4000-8000-00000000abcd"))));
        Assert.Equal((401, "invalid_token"), Pair(await Call(tokens.Token(now - TimeSpan.FromHours(2)))));       // expired
        Assert.Equal((401, "invalid_token"), Pair(await Call(tokens.Token(now, delegated: true))));                 // a user's token
        Assert.Equal((401, "invalid_token"), Pair(await Call(tokens.Token(now, ver: "1.0"))));                     // v2.0 only
        Assert.Equal((401, "invalid_token"), Pair(await Call(tokens.Token(now, issuer: "https://sts.windows.net/" + T + "/"))));
        Assert.Equal((401, "invalid_token"), Pair(await Call(tokens.Token(now, tenant: "22222222-3333-4444-8555-666666666666")))); // no key of that tenant
        using (var other = RSA.Create(2048))
            Assert.Equal((401, "invalid_token"), Pair(await Call(tokens.Token(now, signer: other))));               // forged signature
        Assert.Equal((401, "invalid_token"), Pair(await Call(tokens.Token(now, kid: "unknown-kid"))));

        // A valid token for a tenant no project names.
        var p = (await app.Stores.Projects.GetAsync(TestApp.Pest, TestApp.Pest))!;
        await app.Stores.Projects.ReplaceAsync(p.Value with { TenantId = null }, p.ETag);
        Assert.Equal((403, "tenant_not_registered"), Pair(await Call(tokens.Token(now))));

        // Switched off (the default): the route answers 404 even to a valid token.
        var (off, offTokens) = await App(external: false);
        var r = await off.SendRaw("POST", "v1/uploads/mapping?mode=validate", Headers(offTokens.Token(off.Clock.UtcNow), "k-1"), body);
        Assert.Equal(404, r.Status);
        Assert.False(PilotSettings.FromEnvironment(_ => null).Workflow.ExternalUploadApiEnabled);
    }

    [Fact]
    public async Task F4_Replayed_idempotency_key_returns_the_first_answer_and_never_applies_twice()
    {
        var (app, tokens) = await App();
        var token = tokens.Token(app.Clock.UtcNow);
        var rows = new[] { Json(Row("SAMPLE-FIN-READERS")), Json(Row("SAMPLE-FIN-WRITERS", "Write")) };
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { rows }));
        var first = await app.SendRaw("POST", "v1/uploads/mapping?mode=apply", Headers(token, "SAMPLE-key-1"), body);
        Assert.Equal(200, first.Status);
        Assert.Equal(("Applied", "v1"), (first.Json.GetProperty("status").GetString(), first.Json.GetProperty("version").GetString()));

        var replay = await app.SendRaw("POST", "v1/uploads/mapping?mode=apply", Headers(token, "SAMPLE-key-1"), body);
        Assert.Equal((200, first.Body), (replay.Status, replay.Body)); // the same answer, same uploadId
        Assert.Single(await Versions(app));
        Assert.Single((await app.Stores.AuditEvents.ListAsync(TestApp.Pest)).Select(a => a.Value), a => a.Action == "upload-applied");

        var changed = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { rows = new[] { Json(Row("SAMPLE-FIN-ADMINS", "Admin")) } }));
        var reused = await app.SendRaw("POST", "v1/uploads/mapping?mode=apply", Headers(token, "SAMPLE-key-1"), changed);
        Assert.Equal((409, "idempotency_key_reused"), Pair(reused));
        var missing = await app.SendRaw("POST", "v1/uploads/mapping?mode=apply", Headers(token, null), changed);
        Assert.Equal((400, "invalid_input"), Pair(missing));
        Assert.DoesNotContain(await Uploaded(app), r => r.EntraGroup == "SAMPLE-FIN-ADMINS");
    }

    // ---- the rest of F4 -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task F4_External_api_returns_exactly_the_contract_and_uses_the_same_code_as_the_page()
    {
        var (app, tokens) = await App();
        var rows = new[] { Row("SAMPLE-FIN-READERS"), Row("SAMPLE-FIN-WRITERS", "Write") };
        var page = await app.Upload(Dana, TestApp.Pest, "mapping", MappingCsv(rows), "mapping.csv");
        var json = await app.SendRaw("POST", "v1/uploads/mapping?mode=validate", Headers(tokens.Token(app.Clock.UtcNow), "k-json"),
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { rows = rows.Select(Json) })));
        var file = await app.SendRaw("POST", "v1/uploads/mapping?mode=validate",
            new Dictionary<string, string>(Headers(tokens.Token(app.Clock.UtcNow), "k-file")) { ["Content-Type"] = UploadFiles.MultipartType },
            UploadFiles.Multipart(UploadFiles.Xlsx(rows.Prepend(UploadFiles.MappingHeader.Cast<object?>().ToArray())), "mapping.xlsx"));

        Assert.Equal(["uploadId", "mode", "status", "version", "counts", "errors"], json.Json.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["added", "updated", "unchanged", "retired"], json.Json.GetProperty("counts").EnumerateObject().Select(p => p.Name));
        Assert.Equal(("validate", "Valid", JsonValueKind.Null), (json.Json.GetProperty("mode").GetString(), json.Json.GetProperty("status").GetString(), json.Json.GetProperty("version").ValueKind));
        Assert.Equal(Counts(Upload(page)), Counts(json.Json));
        Assert.Equal(Counts(Upload(page)), Counts(file.Json));

        var bad = await app.SendRaw("POST", "v1/uploads/mapping?mode=apply", Headers(tokens.Token(app.Clock.UtcNow), "k-bad"),
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { rows = new[] { Json(Row("SAMPLE-X", "Superuser")) } })));
        Assert.Equal((422, "Invalid"), (bad.Status, bad.Json.GetProperty("status").GetString()));
        var e = bad.Json.GetProperty("errors")[0];
        Assert.Equal(["row", "column", "code", "message"], e.EnumerateObject().Select(p => p.Name));
        Assert.Equal((1, "AccessLevel", "InvalidValue"), (e.GetProperty("row").GetInt32(), e.GetProperty("column").GetString(), e.GetProperty("code").GetString()));
    }

    [Fact]
    public async Task F4_Only_the_DataMaintainer_uploads_and_only_in_their_own_projects()
    {
        var (app, _) = await App();
        var file = MappingCsv(Row("SAMPLE-FIN-READERS"));
        Assert.Equal(403, (await app.Upload(TestApp.Paul, TestApp.Pest, "mapping", file, "mapping.csv")).Status);  // PMO: views, does not upload
        Assert.Equal(200, (await app.Call(TestApp.Paul, "GET", $"projects/{TestApp.Pest}/uploads")).Status);
        Assert.Equal(403, (await app.Upload(TestApp.Alice, TestApp.Pest, "mapping", file, "mapping.csv")).Status);
        Assert.Equal(403, (await app.Call(TestApp.Alice, "GET", $"projects/{TestApp.Pest}/uploads")).Status);
        Assert.Equal(404, (await app.Upload(TestApp.Bob, TestApp.Pest, "mapping", file, "mapping.csv")).Status);    // no role in PEST
        Assert.Equal(404, (await app.Upload(Dana, TestApp.Beta, "mapping", file, "mapping.csv")).Status);         // Dana has no role in BETA
        Assert.Equal(404, (await app.Upload(Dana, TestApp.Pest, "contacts", file, "mapping.csv")).Status);        // unknown kind
        Assert.Equal(401, (await app.SendRaw("POST", $"projects/{TestApp.Pest}/uploads/mapping?mode=validate", new Dictionary<string, string>(), file)).Status);
        Assert.Equal(200, (await app.Upload(Dana, TestApp.Pest, "mapping", file, "mapping.csv")).Status);
    }

    [Fact]
    public async Task F4_Xlsx_first_sheet_or_named_sheet_with_date_cells_and_numbers()
    {
        var (app, _) = await App();
        var header = UploadFiles.MappingHeader.Cast<object?>().ToArray();
        IReadOnlyList<IReadOnlyList<object?>> mapping = [header, Row("SAMPLE-FIN-READERS", from: new DateOnly(2026, 12, 24), to: "2027-06-30")];
        IReadOnlyList<IReadOnlyList<object?>> notes = [["Readme"], ["SAMPLE: ignored"]];
        var first = await app.Upload(Dana, TestApp.Pest, "mapping", UploadFiles.Xlsx([("Mapping", mapping), ("Notes", notes)]), "mapping.xlsx", "apply");
        Assert.Equal("Applied", StatusOf(first));
        var row = Assert.Single(await Uploaded(app));
        Assert.Equal((new DateOnly(2026, 12, 24), new DateOnly(2027, 6, 30), "Finance", "Domain", "Dev", "Read"),
            (row.EffectiveFrom, row.EffectiveTo, row.Domain, row.ResourceGroupType, row.Environment, row.AccessLevel));
        Assert.Equal((2, 1), (row.SheetRow, row.MappingVersion));

        var named = await app.Upload(Dana, TestApp.Pest, "mapping", UploadFiles.Xlsx([("Notes", notes), ("Mapping", mapping)]), "mapping.xlsx", sheet: "mapping");
        Assert.Equal("Valid", StatusOf(named));
        Assert.Equal(["MissingColumn"], Codes(Upload(await app.Upload(Dana, TestApp.Pest, "mapping", UploadFiles.Xlsx([("Notes", notes), ("Mapping", mapping)]), "mapping.xlsx"))).Distinct());
        Assert.Equal(["SheetNotFound"], Codes(Upload(await app.Upload(Dana, TestApp.Pest, "mapping", UploadFiles.Xlsx([("Mapping", mapping)]), "mapping.xlsx", sheet: "Other"))));
        Assert.Equal(["Date1904"], Codes(Upload(await app.Upload(Dana, TestApp.Pest, "mapping", UploadFiles.Xlsx([("Mapping", mapping)], date1904: true), "mapping.xlsx"))));

        // People: a numeric employee id stays as written, dates in another format are refused (never guessed).
        var people = UploadFiles.Xlsx([UploadFiles.InventoryHeader.Cast<object?>().ToArray(), ["sample.n@sample.invalid", "sample.n@sample.invalid", "SAMPLE N", 12345, "Finance", null, "Active", null]]);
        Assert.Equal("Applied", StatusOf(await app.Upload(Dana, TestApp.Pest, "inventory", people, "people.xlsx", "apply")));
        Assert.Equal("12345", (await app.Stores.PeopleInventory.GetAsync(TestApp.Pest, Person.MakeId("sample.n@sample.invalid")))!.Value.EmployeeId);
        var dates = await app.Upload(Dana, TestApp.Pest, "mapping", MappingCsv(Row("SAMPLE-FIN-X", from: "01/11/2026")), "mapping.csv");
        Assert.Equal(["InvalidDate"], Codes(Upload(dates)));
    }

    [Fact]
    public async Task F4_Csv_with_bom_quotes_semicolons_and_formula_like_values_is_read_as_data()
    {
        var (app, _) = await App();
        var bom = UploadFiles.Csv([UploadFiles.MappingHeader, Row("SAMPLE, quoted \"group\""), Row("=HYPERLINK(\"http://x.invalid\")", domain: "Sales")], bom: true);
        var r = await app.Upload(Dana, TestApp.Pest, "mapping", bom, "mapping.csv", "apply");
        Assert.Equal("Applied", StatusOf(r));
        var groups = (await Uploaded(app)).Select(x => x.EntraGroup).Order().ToList();
        Assert.Equal(["=HYPERLINK(\"http://x.invalid\")", "SAMPLE, quoted \"group\""], groups); // stored as text, never evaluated

        var semicolon = Encoding.UTF8.GetBytes(string.Join(";", UploadFiles.MappingHeader) + "\n" + string.Join(";", Row("SAMPLE-FIN-SEMI").Select(v => v is DateOnly d ? d.ToString("yyyy-MM-dd") : v)) + "\n");
        Assert.Equal("Valid", StatusOf(await app.Upload(Dana, TestApp.Pest, "mapping", semicolon, "mapping.csv")));
        Assert.Equal(["InvalidFile"], Codes(Upload(await app.Upload(Dana, TestApp.Pest, "mapping", [0x41, 0xFF, 0xFE, 0x0A], "mapping.csv"))));
    }

    [Fact]
    public async Task F4_An_upload_keeps_the_azure_roles_and_flags_of_existing_rows()
    {
        // Decision 8: the upload sets the I1 fields; Azure roles, red/amber, privileged and notes stay as they are.
        var (app, _) = await App();
        await Apply(app, MappingCsv(Row("SAMPLE-FIN-READERS")));
        var v = (await app.Stores.MappingSheet.ListAsync(TestApp.Pest)).Single(x => x.Value.EntraGroup == "SAMPLE-FIN-READERS");
        await app.Stores.MappingSheet.ReplaceAsync(v.Value with
        {
            AzureRoles = [new AzureRoleGrant { RoleName = "Reader", RoleDefinitionId = FakeRoleWriter.Reader, Scope = TestApp.SampleAccessScope }],
            Flag = RowFlag.Amber, Privileged = true, Notes = "SAMPLE note", Version = v.Value.Version + 1,
        }, v.ETag);

        var r = await Apply(app, MappingCsv(Row("SAMPLE-FIN-READERS", owner: "sample-user-otto@sample.invalid", to: "2027-01-31")), baseVersion: 1);
        var diff = Assert.Single(r.Json.GetProperty("diff").EnumerateArray(), d => d.GetProperty("change").GetString() == "Updated");
        Assert.Equal(["Owner", "EffectiveTo"], diff.GetProperty("fields").EnumerateArray().Select(f => f.GetString()));
        var row = (await Uploaded(app)).Single();
        Assert.Equal(("sample-user-otto@sample.invalid", RowFlag.Amber, true, "SAMPLE note", 1), (row.Owner, row.Flag, row.Privileged, row.Notes, row.AzureRoles.Count));
    }

    [Fact]
    public async Task F4_Near_duplicate_group_names_in_one_environment_are_refused_unless_both_have_object_ids()
    {
        var (app, _) = await App();
        var r = await app.Upload(Dana, TestApp.Pest, "mapping", MappingCsv(
            Row("PowerBi-Readers"), Row("PowerBI_Readers", "Write"), Row("Power BI Readers", env: "Prod"), Row("PwrBI-Readers", env: "PreProd")), "mapping.csv");
        var e = Assert.Single(Upload(r).GetProperty("errors").EnumerateArray());
        Assert.Equal((3, "NearDuplicateGroup"), (e.GetProperty("row").GetInt32(), e.GetProperty("code").GetString()));
        Assert.Contains("\"PowerBi-Readers\" (row 2", e.GetProperty("message").GetString());

        var ids = await app.Upload(Dana, TestApp.Pest, "mapping", MappingCsv(
            Row("PowerBi-Readers", oid: Guid.NewGuid().ToString()), Row("PowerBI_Readers", "Write", oid: Guid.NewGuid().ToString())), "mapping.csv");
        Assert.Equal("Valid", StatusOf(ids));
    }

    [Fact]
    public async Task F4_People_inventory_upload_validates_enterprise_ids_and_feeds_the_tap_recipient()
    {
        var (app, _) = await App();
        var bad = UploadFiles.Csv([UploadFiles.InventoryHeader,
            PersonRow("sample.g_corp.invalid#EXT#@sample.invalid", mail: "sample.g@sample.invalid"), PersonRow("no-at-sign", mail: "sample.n@sample.invalid"),
            PersonRow("sample.ok@sample.invalid", status: "Gone"),
            PersonRow("sample.m@sample.invalid", mail: "Jane <sample.m@sample.invalid>")]);
        var r = await app.Upload(Dana, TestApp.Pest, "inventory", bad, "people.csv", "apply");
        Assert.Equal(422, r.Status);
        Assert.Equal(["InvalidEnterpriseId", "InvalidEnterpriseId", "InvalidValue", "InvalidMail"], Codes(Upload(r)));

        var good = UploadFiles.Csv([UploadFiles.InventoryHeader, PersonRow("Sample.P@Sample.Invalid", mail: "", alternate: "sample.p@sample-personal.invalid")]);
        Assert.Equal("Applied", StatusOf(await app.Upload(Dana, TestApp.Pest, "inventory", good, "people.csv", "apply")));
        var person = (await app.Stores.PeopleInventory.GetAsync(TestApp.Pest, Person.MakeId("sample.p@sample.invalid")))!.Value;
        Assert.Equal(("sample.p@sample.invalid", null, PersonStatus.Active, 1), (person.EnterpriseId, person.EnterpriseEmail, person.Status, person.InventoryVersion));
        // F2 reads exactly this record: an empty EnterpriseEmail means no TAP mail (the alternate address never stands in).
        Assert.Contains("is empty", TapRecipient.FromInventory(person, "sample.p@sample.invalid").Reason);
    }

    // ---- helpers -------------------------------------------------------------------------------------------------------

    internal static Dictionary<string, string> Headers(string? token, string? key)
    {
        var h = new Dictionary<string, string> { ["Content-Type"] = "application/json" };
        if (token is not null) h["Authorization"] = $"Bearer {token}";
        if (key is not null) h["Idempotency-Key"] = key;
        return h;
    }

    internal static Dictionary<string, object?> Json(object?[] row) =>
        UploadFiles.MappingHeader.Zip(row).ToDictionary(p => p.First, p => p.Second is DateOnly d ? d.ToString("yyyy-MM-dd") : p.Second);

    private static (int, string) Pair(Resp r) => (r.Status, r.ErrorCode);
}
