using System.Text.Json;
using Json.Schema;
using PmoReview.Core.Domain;
using CoreJson = PmoReview.Core.Common.AppJson;

namespace PmoReview.Core.Review;

/// <summary>Server-side validation against <c>src/core/schema/review-result.v1.json</c>.</summary>
public static class ResultSchema
{
    private static readonly Lazy<JsonSchema> Schema = new(Load);

    public static string Text { get; } = ReadResource();

    public static IReadOnlyList<string> Validate(string json)
    {
        JsonElement element;
        try
        {
            element = JsonDocument.Parse(json).RootElement;
        }
        catch (JsonException)
        {
            return ["not valid JSON"];
        }
        var result = Schema.Value.Evaluate(element, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (result.IsValid) return [];
        return (result.Details ?? [])
            .Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}"))
            .DefaultIfEmpty("schema validation failed")
            .ToList();
    }

    /// <exception cref="ReviewFailedException">MalformedResult when the result breaks the contract.</exception>
    public static void EnsureValid(ReviewResult result)
    {
        var errors = Validate(CoreJson.Serialize(result));
        if (errors.Count > 0)
            throw new ReviewFailedException(ErrorCategory.MalformedResult, "Result failed schema validation: " + string.Join("; ", errors.Take(5)));
    }

    private static JsonSchema Load() => JsonSchema.FromText(Text);

    private static string ReadResource()
    {
        using var stream = typeof(ResultSchema).Assembly.GetManifestResourceStream("review-result.v1.json")
                           ?? throw new InvalidOperationException("Embedded schema missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
