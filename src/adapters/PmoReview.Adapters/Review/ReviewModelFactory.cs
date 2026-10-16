using PmoReview.Core.Review;

namespace PmoReview.Adapters.Review;

/// <summary>Chooses the reviewer from settings. Paid providers fail at startup unless explicitly allowed.</summary>
public static class ReviewModelFactory
{
    public static IReviewModel Create(PilotSettings settings, Func<HttpClient>? httpClient = null)
    {
        switch (settings.ReviewModelProvider)
        {
            case RuleOnlyReviewModel.ProviderName:
                return new RuleOnlyReviewModel();

            case AzureOpenAIReviewModel.ProviderName:
                if (!settings.AllowPaidServices)
                    throw new InvalidOperationException("REVIEW_MODEL_PROVIDER=AzureOpenAI requires ALLOW_PAID_SERVICES=true. The zero-cost pilot uses RuleOnly.");
                if (settings.AzureOpenAIEndpoint is null || string.IsNullOrWhiteSpace(settings.AzureOpenAIDeployment))
                    throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT and AZURE_OPENAI_DEPLOYMENT are required for AzureOpenAI.");
                return AzureOpenAIReviewModel.WithManagedIdentity(
                    httpClient?.Invoke() ?? new HttpClient { Timeout = TimeSpan.FromSeconds(40) },
                    new AzureOpenAIOptions
                    {
                        Endpoint = settings.AzureOpenAIEndpoint,
                        Deployment = settings.AzureOpenAIDeployment,
                        ApiVersion = settings.AzureOpenAIApiVersion ?? "2024-10-21",
                    });

            default:
                throw new InvalidOperationException($"Unknown REVIEW_MODEL_PROVIDER '{settings.ReviewModelProvider}'.");
        }
    }
}
