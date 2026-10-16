using System.Text;
using System.Text.Json;
using PmoReview.Core.Common;

namespace PmoReview.Core.Security;

/// <summary>Identity supplied by the Static Web Apps platform in <c>x-ms-client-principal</c>.</summary>
public sealed record ClientPrincipal(string IdentityProvider, string UserId, string UserDetails, IReadOnlyList<string> UserRoles);

public static class ClientPrincipalParser
{
    public const string HeaderName = "x-ms-client-principal";
    private const int MaxHeaderLength = 8 * 1024;

    /// <summary>
    /// Decodes the platform header. This is the ONLY identity source; user or project
    /// values in request bodies are never trusted.
    /// </summary>
    /// <exception cref="AppException">Unauthenticated when missing, malformed, wrong provider or not authenticated.</exception>
    public static ClientPrincipal Parse(string? headerValue, IReadOnlyCollection<string> allowedProviders)
    {
        if (string.IsNullOrWhiteSpace(headerValue) || headerValue.Length > MaxHeaderLength)
            throw AppException.Unauthenticated();

        Raw? raw;
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(headerValue));
            raw = JsonSerializer.Deserialize<Raw>(json, Json.Options);
        }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentException)
        {
            throw AppException.Unauthenticated();
        }

        if (raw is null
            || string.IsNullOrWhiteSpace(raw.UserId)
            || string.IsNullOrWhiteSpace(raw.IdentityProvider)
            || !allowedProviders.Contains(raw.IdentityProvider, StringComparer.OrdinalIgnoreCase)
            || raw.UserRoles is null
            || !raw.UserRoles.Contains("authenticated", StringComparer.OrdinalIgnoreCase))
        {
            throw AppException.Unauthenticated();
        }

        return new ClientPrincipal(raw.IdentityProvider, raw.UserId, raw.UserDetails ?? "", raw.UserRoles);
    }

    /// <summary>Builds a header value. For tests and local tooling only.</summary>
    public static string Encode(ClientPrincipal principal) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new Raw(principal.IdentityProvider, principal.UserId, principal.UserDetails, principal.UserRoles.ToList()), Json.Options)));

    private sealed record Raw(string? IdentityProvider, string? UserId, string? UserDetails, List<string>? UserRoles);
}
