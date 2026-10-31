using System.Text;
using System.Text.Json;
using PmoReview.Core.Common;
using PmoReview.Core.Domain;
using PmoReview.Core.Storage;
using PmoReview.Core.Uploads;
using PmoReview.Core.Workflow;

namespace PmoReview.Core.Http;

public sealed record RollbackInput(int Version);

/// <summary>
/// F4 routes. The Data uploads page (signed-in Data Steward, SWA principal) and the external upload API (Entra app token
/// with <see cref="UploadTokenValidator.Role"/>, off unless <c>EXTERNAL_UPLOAD_API_ENABLED</c>) call the same
/// <see cref="UploadService"/> pipeline.
/// </summary>
public static class UploadEndpoints
{
    /// <summary>Multipart framing on top of the file itself.</summary>
    private const int Framing = 64 * 1024;

    public static ApiHost MapUploads(this ApiHost host, UploadService uploads, DataStores stores, UploadTokenValidator? tokens, WorkflowSettings settings)
    {
        var maxBody = settings.MaxUploadBytes + Framing;
        host.Map("GET", "projects/{projectId}/uploads", async c => new ApiResponse(200, await uploads.OverviewAsync(c.Caller, c["projectId"], c.Ct)));

        // ?mode=validate|apply&retireMissing=true|false&baseVersion=n&sheet=<name>, body multipart/form-data with the file.
        host.Map("POST", "projects/{projectId}/uploads/{kind}", async c =>
        {
            var input = FileInput(c, KindOf(c["kind"]));
            var (status, view) = await uploads.UploadAsync(c.Caller, c["projectId"], input with
            {
                BaseVersion = int.TryParse(c.QueryValue("baseVersion"), out var b) ? b : null,
            }, c.Ct);
            return new ApiResponse(status, view);
        }, maxBody, appToken: false);

        host.Map("POST", "projects/{projectId}/uploads/{kind}/rollback", async c =>
            new ApiResponse(200, await uploads.RollbackAsync(c.Caller, c["projectId"], KindOf(c["kind"]), c.Body<RollbackInput>().Version, c.Request.CorrelationId, c.Ct)));

        // The external API: answers 404 while switched off, so it looks absent. Identity only from the validated token (R7).
        host.Map("POST", "v1/uploads/{kind}", async c =>
        {
            if (!settings.ExternalUploadApiEnabled || tokens is null) throw AppException.NotFound("Route");
            var app = await tokens.ValidateAsync(c.Request.Header("Authorization"), c.Ct);
            var kind = KindOf(c["kind"]);
            // Tenant-scoped: the token's tenant names the project (one project = one tenant). Only an explicit tenant id counts.
            var projects = (await stores.Projects.ListAllAsync(c.Ct)).Select(p => p.Value)
                .Where(p => string.Equals(p.TenantId?.Trim(), app.TenantId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (projects.Count != 1)
                throw new AppException(ErrorKind.Forbidden, "tenant_not_registered",
                    projects.Count == 0 ? "No project in this app belongs to the token's tenant." : "More than one project names the token's tenant; PMO must fix the project list.");
            var input = IsJson(c) ? JsonInput(c, kind) : FileInput(c, kind);
            var (status, result) = await uploads.ExternalAsync(projects[0], app.AppId, input, c.Request.Header("Idempotency-Key"), c.Ct);
            return new ApiResponse(status, result);
        }, maxBody, appToken: true);
        return host;
    }

    private static DataKind KindOf(string kind) => kind switch
    {
        "mapping" => DataKind.Mapping,
        "inventory" => DataKind.Inventory,
        _ => throw AppException.NotFound("Upload kind"),
    };

    private static string Mode(RouteContext c) => c.QueryValue("mode") is { } m && m is UploadService.ModeValidate or UploadService.ModeApply
        ? m : throw AppException.Invalid("Query parameter mode must be validate or apply.");

    private static bool Flag(RouteContext c, string name) => string.Equals(c.QueryValue(name), "true", StringComparison.OrdinalIgnoreCase);

    private static bool IsJson(RouteContext c) =>
        c.Request.Header("Content-Type")?.TrimStart().StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true;

    private static UploadInput FileInput(RouteContext c, DataKind kind)
    {
        var input = new UploadInput
        {
            Kind = kind, Mode = Mode(c), RetireMissing = Flag(c, "retireMissing"), Sheet = c.QueryValue("sheet"), Actor = "?", CorrelationId = c.Request.CorrelationId,
        };
        try
        {
            var file = Multipart.ReadFile(c.Request.Bytes, c.Request.Header("Content-Type"))
                       ?? throw new UploadFileException("InvalidFile", "Send the file as multipart/form-data in the field \"file\".");
            return input with { Content = file.Content, FileName = file.FileName };
        }
        catch (UploadFileException e)
        {
            return input with { FileError = new UploadError(0, "", e.Code, e.Message) };
        }
    }

    /// <summary>JSON body <c>{ "rows": [ { "Column": value, … } ], "retireMissing": false }</c>: values as text, never interpreted.</summary>
    private static UploadInput JsonInput(RouteContext c, DataKind kind)
    {
        var input = new UploadInput { Kind = kind, Mode = Mode(c), RetireMissing = Flag(c, "retireMissing"), Actor = "?", CorrelationId = c.Request.CorrelationId };
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(c.Request.Bytes));
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
                throw new UploadFileException("InvalidFile", "The JSON body must be { \"rows\": [ … ] }.");
            if (doc.RootElement.TryGetProperty("retireMissing", out var retire) && retire.ValueKind is JsonValueKind.True) input = input with { RetireMissing = true };
            var list = new List<IReadOnlyDictionary<string, string?>>();
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) throw new UploadFileException("InvalidFile", "Every entry of \"rows\" must be an object.");
                var cells = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in row.EnumerateObject())
                    cells[p.Name] = p.Value.ValueKind switch
                    {
                        JsonValueKind.String => p.Value.GetString(),
                        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => p.Value.GetRawText(),
                        JsonValueKind.Null => null,
                        _ => throw new UploadFileException("InvalidFile", $"\"{p.Name}\" must be a text, number, true/false or null."),
                    };
                list.Add(cells);
            }
            return input with { JsonRows = list, FileName = "rows.json" };
        }
        catch (JsonException)
        {
            return input with { FileError = new UploadError(0, "", "InvalidFile", "The body is not valid JSON.") };
        }
        catch (UploadFileException e)
        {
            return input with { FileError = new UploadError(0, "", e.Code, e.Message) };
        }
    }
}
