using System.Text.Json.Nodes;
using PmoReview.Adapters;
using PmoReview.Adapters.Review;
using PmoReview.Core.Common;
using PmoReview.Core.Review;
using PmoReview.Tests.Support;
using Xunit;

namespace PmoReview.Tests.Contract;

public class ResultContractTests
{
    private static async Task<JsonObject> ValidResult() =>
        JsonNode.Parse(AppJson.Serialize((await EngineHarness.Run(EngineHarness.Complete())).Result))!.AsObject();

    [Fact]
    public async Task Valid_result_passes()
    {
        Assert.Empty(ResultSchema.Validate((await ValidResult()).ToJsonString()));
    }

    [Theory]
    [InlineData("readiness", "\"Approved\"")]
    [InlineData("humanReviewRequired", "false")]
    [InlineData("schemaVersion", "\"2.0\"")]
    [InlineData("approved", "true")]
    [InlineData("grantAccess", "{\"group\":\"x\"}")]
    public async Task Tampered_result_fails(string property, string json)
    {
        var r = await ValidResult();
        r[property] = JsonNode.Parse(json);
        Assert.NotEmpty(ResultSchema.Validate(r.ToJsonString()));
    }

    [Fact]
    public async Task Missing_required_property_fails()
    {
        var r = await ValidResult();
        r.Remove("findings");
        Assert.NotEmpty(ResultSchema.Validate(r.ToJsonString()));
    }

    /// <summary>Same fixtures as tools/validate_result_schema.py, so C# and Python validators agree.</summary>
    [Fact]
    public void Shared_fixtures_agree_with_python_validator()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "fixtures");
        Assert.Empty(ResultSchema.Validate(File.ReadAllText(Path.Combine(dir, "valid-result.json"))));
        var invalid = Directory.GetFiles(dir, "invalid-*.json");
        Assert.NotEmpty(invalid);
        Assert.All(invalid, f => Assert.NotEmpty(ResultSchema.Validate(File.ReadAllText(f))));
    }

    [Fact]
    public void Malformed_json_fails()
    {
        Assert.NotEmpty(ResultSchema.Validate("{not json"));
    }

    [Fact]
    public void Paid_reviewer_is_refused_without_explicit_flag()
    {
        var settings = new PilotSettings
        {
            ReviewModelProvider = "AzureOpenAI",
            AllowPaidServices = false,
            AzureOpenAIEndpoint = new Uri("https://sample-aoai.invalid/"),
            AzureOpenAIDeployment = "sample",
        };
        var e = Assert.Throws<InvalidOperationException>(() => ReviewModelFactory.Create(settings));
        Assert.Contains("ALLOW_PAID_SERVICES", e.Message);
    }

    [Fact]
    public void Paid_reviewer_builds_when_explicitly_allowed()
    {
        var model = ReviewModelFactory.Create(new PilotSettings
        {
            ReviewModelProvider = "AzureOpenAI",
            AllowPaidServices = true,
            AzureOpenAIEndpoint = new Uri("https://sample-aoai.invalid/"),
            AzureOpenAIDeployment = "sample",
        });
        Assert.Equal("AzureOpenAI", model.Info.Provider);
    }

    [Fact]
    public void Default_and_unknown_providers()
    {
        Assert.IsType<RuleOnlyReviewModel>(ReviewModelFactory.Create(new PilotSettings()));
        Assert.Throws<InvalidOperationException>(() => ReviewModelFactory.Create(new PilotSettings { ReviewModelProvider = "Other" }));
    }
}
