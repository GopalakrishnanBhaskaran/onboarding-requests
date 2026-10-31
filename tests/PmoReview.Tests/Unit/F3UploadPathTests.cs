using System.Text;
using System.Text.Json;
using PmoReview.Core.Domain;
using PmoReview.Core.Workflow;
using PmoReview.Tests.Support;
using Xunit;
using static PmoReview.Tests.Unit.F4UploadTests;

namespace PmoReview.Tests.Unit;

/// <summary>
/// v1.1 F3 through the F4 paths: a mapping row uploaded on the Data uploads page or through the external API only describes
/// access. A Prod row above Read is shown as "Needs human approval", and a request for it is granted only after an Approved
/// ProdWrite escalation — whatever columns the file adds (an upload cannot approve anything).
/// </summary>
public class F3UploadPathTests
{
    private const string Group = "sample-azr-pes-rg-data-prd-ph-weu-upload-writer";
    private const string Petra = "sample-user-petra"; // ProdApprover (SAMPLE-PEST)
    private const string Reason = "SAMPLE: provides application support for the PEST platform and fixes failed production loads (re-drops, file corrections).";

    private static async Task<(TestApp App, string Id)> UploadedAndRequested(bool external)
    {
        var (app, tokens) = await App(provisioning: true);
        app.ResolveGroups = true;
        var group = app.Directory.AddGroup(Group);
        var row = Row(Group, "Write", env: "Prod", domain: "Platform", type: "PH", oid: group.ObjectId);
        if (external)
        {
            // Extra columns try to pre-approve: they are not part of the contract and are ignored.
            var json = F4UploadTests.Json(row);
            json["EscalationStatus"] = "Approved";
            json["ProdApproved"] = true;
            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { rows = new[] { json } }));
            var r = await app.SendRaw("POST", "v1/uploads/mapping?mode=apply", Headers(tokens.Token(app.Clock.UtcNow), "SAMPLE-prod-1"), body);
            Assert.True(r.Status == 200, r.Body);
        }
        else
        {
            var file = UploadFiles.Csv([[.. UploadFiles.MappingHeader, "Approved"], [.. row, "yes"]]);
            var r = await app.Upload(Dana, TestApp.Pest, "mapping", file, "mapping.csv", "apply");
            Assert.True(r.Status == 200, r.Body);
        }
        await app.RunWorker(); // the worker resolves the group name in Entra (F1)

        // F1: the resolved list shows it as needing a human decision, never as automatic.
        var resolve = await app.Call(TestApp.Alice, "GET", $"resources/resolve?projectId={TestApp.Pest}&environment=Prod&resource=PH");
        var access = Assert.Single(resolve.Json.GetProperty("resolvedAccess").EnumerateArray(), a => a.GetProperty("groupName").GetString() == Group);
        Assert.Equal(("NeedsHumanApproval", "Write"), (access.GetProperty("status").GetString(), access.GetProperty("accessLevel").GetString()));

        var id = await app.SubmitId(TestApp.Intake(enterpriseId: "jane.doe@company.example", resource: "Platform", accessLevel: "Write", environment: "Prod",
            justification: Reason, first: "Jane", last: "Doe"), TestApp.Alice, TestApp.Pest);
        await app.RunWorker();
        var reviewed = (await app.Request(id, TestApp.Pest)).Flow!;
        Assert.True(reviewed.Status == RequestStatus.AwaitingOwner, $"{reviewed.Status}: {reviewed.LastReason}");
        Assert.Equal(200, (await app.Approve(id, TestApp.Olivia, project: TestApp.Pest)).Status);
        Assert.Equal(200, (await app.Approve(id, TestApp.Sam, project: TestApp.Pest)).Status);
        return (app, id);
    }

    private static async Task AssertNothingGrantedUntilApprovedAsync(TestApp app, string id)
    {
        for (var i = 0; i < 3; i++) await app.RunWorker();
        var flow = (await app.Request(id, TestApp.Pest)).Flow!;
        Assert.Equal((RequestStatus.AwaitingProdApproval, WaitingOn.ProdApprover), (flow.Status, flow.WaitingOn));
        Assert.Empty(app.Writer.Calls);
        Assert.Empty(app.RoleWriter.Calls);
        Assert.Null(flow.IdTeamTask);
        var e = Assert.Single(await app.Stores.Escalations.ListAsync(TestApp.Pest), x => x.Value.RequestId == id).Value;
        Assert.Equal((EscalationKind.ProdWrite, EscalationStatus.Open, Group), (e.Kind, e.Status, e.Group));

        // Only the ProdApprover's decision moves it on — then through the normal audited path.
        var decided = await app.Call(Petra, "POST", $"projects/{TestApp.Pest}/escalations/{e.Id}/decision", new { decision = "Approved", remarks = "SAMPLE ok", requestRevision = 1 });
        Assert.True(decided.Status == 200, decided.Body);
        await app.RunWorker();
        Assert.Contains($"add-member jane.doe@sample.invalid {Group}", app.Writer.Calls);
    }

    [Fact]
    public async Task F3_Prod_write_via_upload_is_never_granted_without_an_approved_escalation()
    {
        var (app, id) = await UploadedAndRequested(external: false);
        await AssertNothingGrantedUntilApprovedAsync(app, id);
    }

    [Fact]
    public async Task F3_Prod_write_via_external_api_is_never_granted_without_an_approved_escalation()
    {
        var (app, id) = await UploadedAndRequested(external: true);
        await AssertNothingGrantedUntilApprovedAsync(app, id);
    }
}
