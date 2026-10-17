using PmoReview.Core.Common;
using PmoReview.Core.Domain;
using PmoReview.Core.Services;
using PmoReview.Core.Workflow;
using PmoReview.Tests.Support;
using Xunit;

namespace PmoReview.Tests.Unit;

/// <summary>Stage 1: incomplete requests are rejected listing exactly what is missing; the domain ID is derived.</summary>
public class IntakeTests
{
    [Fact]
    public async Task Incomplete_request_is_rejected_with_exactly_what_is_missing_and_nothing_is_saved()
    {
        var app = TestApp.Create();
        var r = await app.Submit(new { enterpriseId = "sample.e1001", firstName = "Sample", resource = "SAMPLE Databricks Engineering" });
        Assert.Equal(400, r.Status);
        Assert.Equal("incomplete_request", r.ErrorCode);
        var missing = r.Json.GetProperty("error").GetProperty("details").EnumerateArray().Select(d => d.GetString()!).ToArray();
        Assert.Equal(["Last name", "Access level", "Justification", "Owner"], missing);
        Assert.Contains("Missing: Last name, Access level, Justification, Owner.", r.Json.GetProperty("error").GetProperty("message").GetString());
        Assert.Empty(await app.Stores.Requests.ListAsync(TestApp.Alpha));
    }

    [Fact]
    public async Task Whitespace_only_fields_count_as_missing()
    {
        var app = TestApp.Create();
        var r = await app.Submit(TestApp.Intake(justification: "   "));
        Assert.Equal(["Justification"], r.Json.GetProperty("error").GetProperty("details").EnumerateArray().Select(d => d.GetString()!).ToArray());
    }

    [Fact]
    public void Format_is_fixed_safely_and_reported()
    {
        var (clean, fixes) = RequestService.Validate(new RequestInput
        {
            EnterpriseId = " Sample.E1001 ", FirstName = "  sample  ", LastName = "JOINER", Resource = "SAMPLE   Databricks  Engineering",
            AccessLevel = "User", Justification = "SAMPLE: builds pipelines in the dev workspace for the team.", Owner = "Owner.Name@Sample.Invalid",
        });
        Assert.Equal("sample.e1001", clean.EnterpriseId);
        Assert.Equal("Sample", clean.FirstName);
        Assert.Equal("Joiner", clean.LastName);
        Assert.Equal("SAMPLE Databricks Engineering", clean.Resource);
        Assert.Equal("owner.name@sample.invalid", clean.Owner);
        Assert.Equal(["Enterprise ID", "First name", "Last name", "Resource", "Owner"], fixes);
    }

    [Fact]
    public void Mixed_case_names_are_kept()
    {
        var (clean, _) = RequestService.Validate(new RequestInput
        {
            EnterpriseId = "e1", FirstName = "McKenzie", LastName = "de Vries", Resource = "R", AccessLevel = "User",
            Justification = "SAMPLE: x", Owner = "o@sample.invalid",
        });
        Assert.Equal("McKenzie", clean.FirstName);
        Assert.Equal("de Vries", clean.LastName);
    }

    [Theory]
    [InlineData("bad id!", "Sample", "o@sample.invalid", "Enterprise ID")]
    [InlineData("e1001", "Sample<script>", "o@sample.invalid", "First and last name")]
    [InlineData("e1001", "Sample", "not-a-sign-in", "Owner must be")]
    public void Invalid_formats_are_rejected(string enterpriseId, string first, string owner, string message)
    {
        var e = Assert.Throws<AppException>(() => RequestService.Validate(new RequestInput
        {
            EnterpriseId = enterpriseId, FirstName = first, LastName = "Joiner", Resource = "R", AccessLevel = "User", Justification = "SAMPLE: x", Owner = owner,
        }));
        Assert.Contains(message, e.Message);
    }

    [Fact]
    public async Task Domain_specific_id_is_derived_from_the_enterprise_id_and_project_domain()
    {
        var app = TestApp.Create();
        var r = await app.Submit(TestApp.Intake(enterpriseId: "Sample.E2002"));
        Assert.Equal("sample.e2002@sample.invalid", r.Request.GetProperty("domainId").GetString());
        Assert.Equal("sample.e2002@contoso.invalid", DomainIds.Derive("Sample.E2002", "@Contoso.invalid"));
        Assert.Equal("sample.e2002", DomainIds.Derive("Sample.E2002", null));
    }

    [Fact]
    public async Task Identity_and_workflow_fields_in_the_body_are_ignored()
    {
        var app = TestApp.Create();
        var r = await app.Submit(new
        {
            enterpriseId = "sample.e1001", firstName = "Sample", lastName = "Joiner", resource = "SAMPLE Databricks Engineering", accessLevel = "User",
            justification = "SAMPLE: joins the data engineering team to build and run pipelines.", owner = "sample-user-olivia@sample.invalid",
            createdBy = TestApp.Olivia, domainId = "someone.else@sample.invalid", flow = new { status = "Completed" }, revision = 99,
        });
        Assert.Equal(201, r.Status);
        Assert.Equal(TestApp.Alice, r.Request.GetProperty("createdBy").GetString());
        Assert.Equal(TestApp.DefaultUser, r.Request.GetProperty("domainId").GetString());
        Assert.Equal(1, r.Request.GetProperty("revision").GetInt32());
        Assert.NotEqual("Completed", r.StatusName);
    }

    [Theory]
    [InlineData(TestApp.Olivia)]
    [InlineData(TestApp.Sam)]
    [InlineData(TestApp.Paul)]
    public async Task Only_requesters_submit(string user)
    {
        var app = TestApp.Create();
        Assert.Equal(403, (await app.Submit(user: user)).Status);
    }

    [Fact]
    public async Task Request_can_be_changed_only_while_returned()
    {
        var app = TestApp.Create();
        var id = await app.To(RequestStatus.AwaitingOwner);
        var view = await app.View(id);
        var r = await app.Call(TestApp.Alice, "PUT", $"projects/{TestApp.Alpha}/requests/{id}", TestApp.Intake(), view.Json.GetProperty("request").GetProperty("etag").GetString());
        Assert.Equal(409, r.Status);
        Assert.Equal("not_returned", r.ErrorCode);
        Assert.Equal(428, (await app.Call(TestApp.Alice, "PUT", $"projects/{TestApp.Alpha}/requests/{id}", TestApp.Intake())).Status);
    }

    [Fact]
    public async Task Stale_etag_on_resubmit_is_rejected()
    {
        var app = TestApp.Create();
        var id = await app.SubmitId(TestApp.Intake(resource: "SAMPLE Legacy Tool"));
        var r = await app.Call(TestApp.Alice, "PUT", $"projects/{TestApp.Alpha}/requests/{id}", TestApp.Intake(), "\"stale\"");
        Assert.Equal(412, r.Status);
    }

    [Fact]
    public async Task Secret_in_justification_is_masked_before_it_is_stored()
    {
        var app = TestApp.Create();
        var id = await app.SubmitId(TestApp.Intake(justification: "SAMPLE: builds pipelines for the team. password: Hunter2-Sample"));
        Assert.DoesNotContain("Hunter2-Sample", (await app.Request(id)).Justification);
        Assert.Contains(SecretMasker.Mask, (await app.Request(id)).Justification);
    }

    [Fact]
    public void Snapshot_hash_is_stable_and_changes_with_content()
    {
        var s = EngineHarness.Complete();
        Assert.Equal(SnapshotBuilder.Hash(s), SnapshotBuilder.Hash(s with { CapturedAt = s.CapturedAt.AddDays(1) }));
        Assert.NotEqual(SnapshotBuilder.Hash(s), SnapshotBuilder.Hash(s with { AccessLevel = "Admin" }));
        Assert.NotEqual(SnapshotBuilder.Hash(s), SnapshotBuilder.Hash(s with { Guest = true }));
        Assert.True(SnapshotBuilder.Verify(s, SnapshotBuilder.Hash(s)));
    }
}
