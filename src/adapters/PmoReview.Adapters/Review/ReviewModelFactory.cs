using Microsoft.Extensions.Logging;
using PmoReview.Core.Review;

namespace PmoReview.Adapters.Review;

/// <summary>Chooses the reviewer from settings. Paid providers fail at startup unless explicitly allowed.</summary>
public static class ReviewModelFactory
{
    public static IReviewModel Create(PilotSettings settings, Func<HttpClient>? httpClient = null, ILogger? logger = null) =>
        settings.ReviewModelProvider switch
        {
            RuleOnlyReviewModel.ProviderName => new RuleOnlyReviewModel(),
            AzureOpenAIReviewModel.ProviderName => new AzureOpenAIReviewModel(Chat(settings, httpClient, logger)),
            _ => throw new InvalidOperationException($"Unknown REVIEW_MODEL_PROVIDER '{settings.ReviewModelProvider}'."),
        };

    /// <summary>Azure OpenAI in our tenant, managed identity. Refused unless paid services are allowed.</summary>
    public static AzureOpenAIChat Chat(PilotSettings settings, Func<HttpClient>? httpClient = null, ILogger? logger = null)
    {
        if (!settings.AllowPaidServices)
            throw new InvalidOperationException("Azure OpenAI requires ALLOW_PAID_SERVICES=true. The zero-cost pilot uses RuleOnly.");
        if (settings.AzureOpenAIEndpoint is null || string.IsNullOrWhiteSpace(settings.AzureOpenAIDeployment))
            throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT and AZURE_OPENAI_DEPLOYMENT are required for AzureOpenAI.");
        return AzureOpenAIChat.WithManagedIdentity(
            httpClient?.Invoke() ?? new HttpClient { Timeout = TimeSpan.FromSeconds(40) },
            new AzureOpenAIOptions
            {
                Endpoint = settings.AzureOpenAIEndpoint,
                Deployment = settings.AzureOpenAIDeployment,
                ApiVersion = settings.AzureOpenAIApiVersion ?? "2024-10-21",
            },
            logger: logger);
    }
}
