using PmoReview.Core.Domain;
using PmoReview.Tests.Support;
using Xunit;

namespace PmoReview.Tests.Unit;

public class RequestAndSnapshotTests
{
    [Fact]
    public async Task Update_requires_if_match()
    {
        var app = TestApp.Create();
        var c = await app.Call(TestApp.Alice, "POST", $"projects/{TestApp.Alpha}/requests", new { role = "a" });
        var id = c.Value.GetProperty("id").GetString();
        var r = await app.Call(TestApp.Alice, "PUT", $"projects/{TestApp.Alpha}/requests/{id}", new { role = "b" });
        Assert.Equal(428, r.Status);
    }

    [Fact]
    public async Task Stale_etag_is_412_and_fresh_etag_bumps_revision()
    {
        var app = TestApp.Create();
        var c = await app.Call(TestApp.Alice, "POST", $"projects/{TestApp.Alpha}/requests", new { role = "a" });
        var id = c.Value.GetProperty("id").GetString();

        var ok = await app.Call(TestApp.Alice, "PUT", $"projects/{TestApp.Alpha}/requests/{id}", new { role = "b" }, ifMatch: c.ETag);
        Assert.Equal(200, ok.Status);
        Assert.Equal(2, ok.Value.GetProperty("revision").GetInt32());

        var stale = await app.Call(TestApp.Alice, "PUT", $"projects/{TestApp.Alpha}/requests/{id}", new { role = "c" }, ifMatch: c.ETag);
        Assert.Equal(412, stale.Status);
    }

    [Fact]
    public async Task Oversized_text_and_body_are_rejected()
    {
        var app = TestApp.Create();
        var tooLong = await app.Call(TestApp.Alice, "POST", $"projects/{TestApp.Alpha}/requests", new { justification = new string('x', 2001) });
        Assert.Equal(400, tooLong.Status);

        var huge = await app.Call(TestApp.Alice, "POST", $"projects/{TestApp.Alpha}/requests", rawBody: new string(' ', 70 * 1024));
        Assert.Equal(413, huge.Status);
    }

    [Fact]
    public async Task Invalid_json_is_400_without_details()
    {
        var app = TestApp.Create();
        var r = await app.Call(TestApp.Alice, "POST", $"projects/{TestApp.Alpha}/requests", rawBody: "{\"startDate\": \"not-a-date\"}");
        Assert.Equal(400, r.Status);
        Assert.DoesNotContain("Exception", r.Body);
    }

    [Fact]
    public void Snapshot_hash_is_stable_and_content_sensitive()
    {
        var req = new AccessRequest
        {
            Id = "r1", ProjectId = "P", Role = "R", Environment = "Dev", StartDate = new DateOnly(2026, 11, 1),
            CreatedBy = "u", CreatedAt = DateTimeOffset.UnixEpoch, UpdatedBy = "u", UpdatedAt = DateTimeOffset.UnixEpoch, Revision = 3,
        };
        var a = SnapshotBuilder.Build(req, DateTimeOffset.UnixEpoch);
        var b = SnapshotBuilder.Build(req, DateTimeOffset.UnixEpoch.AddHours(5));
        var c = SnapshotBuilder.Build(req with { Role = "R2" }, DateTimeOffset.UnixEpoch);
        var d = SnapshotBuilder.Build(req with { Revision = 4 }, DateTimeOffset.UnixEpoch);

        var ha = SnapshotBuilder.Hash(a);
        Assert.Matches("^[0-9a-f]{64}$", ha);
        Assert.Equal(ha, SnapshotBuilder.Hash(b));
        Assert.NotEqual(ha, SnapshotBuilder.Hash(c));
        Assert.NotEqual(ha, SnapshotBuilder.Hash(d));
        Assert.True(SnapshotBuilder.Verify(a, ha));
        Assert.False(SnapshotBuilder.Verify(c, ha));
    }
}
