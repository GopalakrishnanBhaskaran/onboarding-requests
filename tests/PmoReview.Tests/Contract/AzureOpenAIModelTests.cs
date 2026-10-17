using System.Text.Json;
using PmoReview.Adapters.Review;
using PmoReview.Core.Domain;
using PmoReview.Core.Review;
using PmoReview.Tests.Support;
using Xunit;

namespace PmoReview.Tests.Contract;

/// <summary>Azure OpenAI (disabled in the pilot) against a mocked endpoint: structured JSON, temperature 0, masking, 429 backoff, token usage.</summary>
public class AzureOpenAIModelTests
{
    private const string Pass = """{"summary":"Consistent with the access level.","recommendation":"Pass","findings":[],"limitations":[]}""";

    [Fact]
    public async Task Request_uses_structured_output_temperature_zero_and_the_v2_prompt()
    {
        var handler = new ScriptedHandler(() => ScriptedHandler.Final(Pass));
        var o = await EngineHarness.Run(EngineHarness.Complete(), ScriptedHandler.Model(handler));
        var body = JsonDocument.Parse(handler.Requests.Single().Body).RootElement;
        Assert.Equal(0, body.GetProperty("temperature").GetInt32());
        Assert.Equal("json_schema", body.GetProperty("response_format").GetProperty("type").GetString());
        Assert.True(body.GetProperty("response_format").GetProperty("json_schema").GetProperty("strict").GetBoolean());
        Assert.False(body.TryGetProperty("tools", out _));
        Assert.Equal("Bearer sample-token", handler.Requests.Single().Request.Headers.Authorization!.ToString());
        Assert.Equal(ReviewOutcome.Pass, o.Result.Outcome);
        Assert.Equal(CheckStatus.Pass, o.Check(CheckCodes.AiReview).Status);
        Assert.Contains("AI notes: Consistent", o.Result.Summary);
        Assert.Equal(new TokenUsage(120, 30, 1), o.Usage);
    }

    [Fact]
    public async Task Secrets_and_passes_are_masked_before_the_prompt()
    {
        var handler = new ScriptedHandler(() => ScriptedHandler.Final(Pass));
        await EngineHarness.Run(EngineHarness.Complete(s => s with { Justification = s.Justification + " temporary access pass: Kq8-Zt3-Pw9" }), ScriptedHandler.Model(handler));
        Assert.DoesNotContain("Kq8-Zt3-Pw9", handler.Requests.Single().Body);
        Assert.Contains("request_data_UNTRUSTED", handler.Requests.Single().Body);
    }

    [Fact]
    public async Task Throttling_backs_off_and_retries_then_records_calls()
    {
        var handler = new ScriptedHandler(() => ScriptedHandler.Throttled(), () => ScriptedHandler.Throttled(), () => ScriptedHandler.Final(Pass));
        var o = await EngineHarness.Run(EngineHarness.Complete(), ScriptedHandler.Model(handler));
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(3, o.Usage!.Calls);
    }

    [Fact]
    public async Task Persistent_throttling_fails_the_review_as_a_dependency_problem()
    {
        var handler = new ScriptedHandler { Repeat = () => ScriptedHandler.Throttled() };
        var e = await Assert.ThrowsAsync<ReviewFailedException>(() => EngineHarness.Run(EngineHarness.Complete(), ScriptedHandler.Model(handler), new ReviewLimits { MaxThrottleRetries = 2 }));
        Assert.Equal(ErrorCategory.DependencyUnavailable, e.Category);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Theory]
    [InlineData("""{"summary":"x"}""")]
    [InlineData("not json")]
    public async Task Output_off_schema_is_a_malformed_result(string content)
    {
        var e = await Assert.ThrowsAsync<ReviewFailedException>(() => EngineHarness.Run(EngineHarness.Complete(), ScriptedHandler.Model(new ScriptedHandler(() => ScriptedHandler.Final(content)))));
        Assert.Equal(ErrorCategory.MalformedResult, e.Category);
    }

    [Fact]
    public async Task Model_recommendation_to_fail_returns_the_request_with_its_reasons()
    {
        var handler = new ScriptedHandler(() => ScriptedHandler.Final("""
            {"summary":"Level too high.","recommendation":"Fail","findings":[{"code":"LEVEL_TOO_HIGH","field":"accessLevel","severity":"Blocking","message":"User access is more than reading reports needs."}],"limitations":[]}
            """));
        var o = await EngineHarness.Run(EngineHarness.Complete(), ScriptedHandler.Model(handler));
        Assert.Equal(ReviewOutcome.Fail, o.Result.Outcome);
        Assert.Contains("more than reading reports needs", o.Check(CheckCodes.AiReview).Detail);
        Assert.Contains(o.Result.ReturnReasons, r => r.StartsWith(CheckGroups.AiReview, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Assistant_drops_analysis_on_failure_and_masks_output()
    {
        var ok = new AzureOpenAIAssistant(ScriptedHandler.Chat(new ScriptedHandler(() => ScriptedHandler.Final("""{"analysis":"Owner waited longest. token: Ab12-Cd34"}"""))));
        var text = await ok.AnalyseAsync("daily-summary", "facts", CancellationToken.None);
        Assert.StartsWith("Owner waited longest.", text);
        Assert.DoesNotContain("Ab12-Cd34", text);
        var down = new AzureOpenAIAssistant(ScriptedHandler.Chat(new ScriptedHandler { Repeat = () => ScriptedHandler.Throttled() }));
        Assert.Null(await down.AnalyseAsync("daily-summary", "facts", CancellationToken.None));
    }

    [Fact]
    public async Task Response_schemas_follow_the_azure_openai_strict_mode_subset()
    {
        // Learn, structured outputs (checked 17 Oct 2026, F53): every property required, additionalProperties false on
        // every object, at most 5 levels of nesting, and these keywords are not supported.
        string[] unsupported = ["minLength", "maxLength", "pattern", "format", "minimum", "maximum", "multipleOf", "patternProperties",
            "unevaluatedProperties", "propertyNames", "minProperties", "maxProperties", "unevaluatedItems", "contains", "minContains",
            "maxContains", "minItems", "maxItems", "uniqueItems"];
        var review = new ScriptedHandler(() => ScriptedHandler.Final(Pass));
        await EngineHarness.Run(EngineHarness.Complete(), ScriptedHandler.Model(review));
        var assistant = new ScriptedHandler(() => ScriptedHandler.Final("""{"analysis":"ok"}"""));
        await new AzureOpenAIAssistant(ScriptedHandler.Chat(assistant)).AnalyseAsync("daily-summary", "facts", CancellationToken.None);

        foreach (var request in new[] { review.Requests.Single().Body, assistant.Requests.Single().Body })
        {
            var format = JsonDocument.Parse(request).RootElement.GetProperty("response_format").GetProperty("json_schema");
            Assert.True(format.GetProperty("strict").GetBoolean());
            Check(format.GetProperty("schema"), depth: 1);
        }

        void Check(JsonElement node, int depth)
        {
            foreach (var p in node.EnumerateObject()) Assert.DoesNotContain(p.Name, unsupported);
            if (node.TryGetProperty("properties", out var props))
            {
                Assert.True(depth <= 5, "nesting deeper than 5 levels");
                Assert.False(node.GetProperty("additionalProperties").GetBoolean());
                var required = node.GetProperty("required").EnumerateArray().Select(r => r.GetString()!).Order().ToList();
                Assert.Equal(props.EnumerateObject().Select(p => p.Name).Order().ToList(), required);
                foreach (var p in props.EnumerateObject()) Check(p.Value, depth + 1);
            }
            if (node.TryGetProperty("items", out var items)) Check(items, depth);
        }
    }

    [Fact]
    public void System_prompt_never_approves_creates_or_assigns()
    {
        Assert.Contains("never approve, create, assign or decide access", AzureOpenAIReviewModel.SystemPrompt);
        Assert.Contains("Never suggest approving, creating or assigning access", AzureOpenAIAssistant.SystemPrompt);
    }
}
