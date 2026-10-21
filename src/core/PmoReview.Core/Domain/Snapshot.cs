using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PmoReview.Core.Domain;

public static class SnapshotBuilder
{
    public static RequestSnapshot Build(AccessRequest request, DateTimeOffset capturedAt) => new()
    {
        RequestId = request.Id,
        ProjectId = request.ProjectId,
        Revision = request.Revision,
        EnterpriseId = request.EnterpriseId,
        FirstName = request.FirstName,
        LastName = request.LastName,
        DomainId = request.DomainId,
        Resource = request.Resource,
        AccessLevel = request.AccessLevel,
        Environment = request.Environment,
        Justification = request.Justification,
        Owner = request.Owner,
        Guest = request.Guest,
        CapturedAt = capturedAt,
    };

    /// <summary>
    /// SHA-256 (lowercase hex) over canonical JSON: fixed key order, no whitespace.
    /// <see cref="RequestSnapshot.CapturedAt"/> is excluded so equal content gives equal hashes.
    /// </summary>
    public static string Hash(RequestSnapshot s)
    {
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("accessLevel", s.AccessLevel);
            w.WriteString("domainId", s.DomainId);
            w.WriteString("enterpriseId", s.EnterpriseId);
            // Written only when set, so snapshots taken before environments existed keep their hash.
            if (s.Environment is not null) w.WriteString("environment", s.Environment);
            w.WriteString("firstName", s.FirstName);
            w.WriteBoolean("guest", s.Guest);
            w.WriteString("justification", s.Justification);
            w.WriteString("lastName", s.LastName);
            w.WriteString("owner", s.Owner);
            w.WriteString("projectId", s.ProjectId);
            w.WriteString("requestId", s.RequestId);
            w.WriteString("resource", s.Resource);
            w.WriteNumber("revision", s.Revision);
            w.WriteEndObject();
        }
        return Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }

    public static bool Verify(RequestSnapshot s, string hash) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(s)), Encoding.ASCII.GetBytes(hash));
}
