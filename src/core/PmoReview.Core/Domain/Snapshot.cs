using System.Globalization;
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
        RequestType = request.RequestType,
        Role = request.Role,
        Environment = request.Environment,
        Manager = request.Manager,
        Justification = request.Justification,
        StartDate = request.StartDate,
        EndDate = request.EndDate,
        CapturedAt = capturedAt,
    };

    /// <summary>
    /// SHA-256 (lowercase hex) over canonical JSON: fixed key order, ISO dates, no whitespace.
    /// <see cref="RequestSnapshot.CapturedAt"/> is excluded so equal content gives equal hashes.
    /// </summary>
    public static string Hash(RequestSnapshot s)
    {
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("endDate", Date(s.EndDate));
            w.WriteString("environment", s.Environment);
            w.WriteString("justification", s.Justification);
            w.WriteString("manager", s.Manager);
            w.WriteString("projectId", s.ProjectId);
            w.WriteString("requestId", s.RequestId);
            w.WriteString("requestType", s.RequestType);
            w.WriteNumber("revision", s.Revision);
            w.WriteString("role", s.Role);
            w.WriteString("startDate", Date(s.StartDate));
            w.WriteEndObject();
        }
        return Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }

    public static bool Verify(RequestSnapshot s, string hash) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(s)), Encoding.ASCII.GetBytes(hash));

    private static string? Date(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
