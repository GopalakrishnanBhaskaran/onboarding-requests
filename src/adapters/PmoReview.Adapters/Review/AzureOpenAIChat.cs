using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PmoReview.Core.Domain;
using PmoReview.Core.Review;

namespace PmoReview.Adapters.Review;

public sealed record AzureOpenAIOptions
{
    /// <summary>Azure OpenAI resource inside our tenant.</summary>
    public required Uri Endpoint { get; init; }
    public required string Deployment { get; init; }
    /// <summary>Must support structured outputs (json_schema). Verify before promotion.</summary>
    public string ApiVersion { get; init; } = "2024-10-21";
}

/// <summary>
/// One chat completion against Azure OpenAI: managed identity (no keys; any secret config lives in Key Vault
/// references, never in code), structured JSON output with a strict schema, temperature 0, backoff on HTTP 429
/// (Retry-After honoured, capped), and token usage logged. Callers mask secrets and TAPs before building messages.
/// </summary>
public sealed class AzureOpenAIChat(HttpClient http, AzureOpenAIOptions options, Func<CancellationToken, ValueTask<string>> bearerToken, ILogger? logger = null)
{
    private readonly ILogger _log = logger ?? NullLogger.Instance;

    public AzureOpenAIOptions Options => options;

    /// <summary>Managed identity (Cognitive Services OpenAI User role). No keys.</summary>
    public static AzureOpenAIChat WithManagedIdentity(HttpClient http, AzureOpenAIOptions options, TokenCredential? credential = null, ILogger? logger = null)
    {
        var cred = credential ?? new DefaultAzureCredential();
        var context = new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]);
        return new AzureOpenAIChat(http, options, async ct => (await cred.GetTokenAsync(context, ct)).Token, logger);
    }

    public async Task<(string Content, TokenUsage Usage)> CompleteAsync(
        string purpose, string systemPrompt, string userContent, JsonObject responseFormat, int maxOutputTokens, int maxThrottleRetries, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = userContent },
            },
            ["temperature"] = 0,
            ["response_format"] = responseFormat.DeepClone(),
            ["max_completion_tokens"] = maxOutputTokens,
        };
        var url = new Uri(options.Endpoint,
            $"openai/deployments/{Uri.EscapeDataString(options.Deployment)}/chat/completions?api-version={Uri.EscapeDataString(options.ApiVersion)}");

        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await bearerToken(ct));
            using var res = await http.SendAsync(req, ct);
            if (res.StatusCode == HttpStatusCode.TooManyRequests)
            {
                if (attempt >= maxThrottleRetries)
                    throw new ReviewFailedException(ErrorCategory.DependencyUnavailable, $"Model endpoint throttled after {attempt + 1} attempts.");
                var wait = RetryAfter(res) ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                if (wait > TimeSpan.FromSeconds(10)) wait = TimeSpan.FromSeconds(10);
                _log.LogWarning("aoai throttled purpose={Purpose} attempt={Attempt} wait={WaitMs}ms", purpose, attempt + 1, (int)wait.TotalMilliseconds);
                await Task.Delay(wait, ct);
                continue;
            }
            if (!res.IsSuccessStatusCode)
                throw new ReviewFailedException(ErrorCategory.DependencyUnavailable, $"Model endpoint returned {(int)res.StatusCode}.");

            var json = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct)) as JsonObject;
            var message = json?["choices"]?[0]?["message"] as JsonObject
                          ?? throw new ReviewFailedException(ErrorCategory.MalformedResult, "Model response had no message.");
            if (message["refusal"] is JsonValue refusal && refusal.GetValueKind() == JsonValueKind.String)
                throw new ReviewFailedException(ErrorCategory.MalformedResult, "Model refused to answer.");
            var usage = new TokenUsage(
                json?["usage"]?["prompt_tokens"]?.GetValue<int>() ?? 0,
                json?["usage"]?["completion_tokens"]?.GetValue<int>() ?? 0,
                attempt + 1);
            _log.LogInformation("aoai usage purpose={Purpose} deployment={Deployment} prompt={Prompt} completion={Completion} calls={Calls}",
                purpose, options.Deployment, usage.PromptTokens, usage.CompletionTokens, usage.Calls);
            var content = message["content"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(content))
                throw new ReviewFailedException(ErrorCategory.MalformedResult, "Model returned no content.");
            return (content, usage);
        }
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage res)
    {
        if (res.Headers.TryGetValues("retry-after-ms", out var ms) && double.TryParse(ms.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var m))
            return TimeSpan.FromMilliseconds(m);
        return res.Headers.RetryAfter?.Delta;
    }
}
