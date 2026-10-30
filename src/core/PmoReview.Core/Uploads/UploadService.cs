using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PmoReview.Core.Common;
using PmoReview.Core.Domain;
using PmoReview.Core.Security;
using PmoReview.Core.Storage;
using PmoReview.Core.Workflow;

namespace PmoReview.Core.Uploads;

/// <summary>F4 counts (output contract).</summary>
public sealed record UploadCounts(int Added, int Updated, int Unchanged, int Retired);

/// <summary>F4 output contract of the upload API (validate or apply): exactly these fields.</summary>
public sealed record UploadResult(string UploadId, string Mode, string Status, string? Version, UploadCounts Counts, IReadOnlyList<UploadError> Errors)
{
    public const string Valid = "Valid", Invalid = "Invalid", Applied = "Applied";
}

/// <summary>One line of the diff the page shows: Added | Updated | Unchanged | Missing (left as it is) | Retired.</summary>
public sealed record UploadDiffRow(int Row, string Change, string Label, IReadOnlyList<string> Fields);

/// <summary>What the Data uploads page gets: the contract, the version it was checked against and the diff.</summary>
public sealed record UploadView(UploadResult Upload, int BaseVersion, int Missing, IReadOnlyList<UploadDiffRow> Diff, string? FileName);

public sealed record DataVersionView(string Version, int Number, DataVersionState State, string? UploadedBy, DateTimeOffset UploadedAt, string? FileName,
    string? FileHash, DataCounts Counts, string? DiffSummary, bool RetireMissing, string Channel, int? RollbackOf, string? RolledBackBy, DateTimeOffset? RolledBackAt);

public sealed record UploadHistory(DataKind Kind, int Current, IReadOnlyList<string> Columns, IReadOnlyList<DataVersionView> Versions);

public sealed record UploadsOverview(UploadHistory Mapping, UploadHistory Inventory, int MaxUploadBytes, bool ExternalApiEnabled);

/// <summary>An upload as either channel delivers it: a file (multipart) or JSON rows (external API only).</summary>
public sealed record UploadInput
{
    public required DataKind Kind { get; init; }
    /// <summary>"validate" or "apply".</summary>
    public required string Mode { get; init; }
    public byte[] Content { get; init; } = [];
    public string? FileName { get; init; }
    public string? Sheet { get; init; }
    public IReadOnlyList<IReadOnlyDictionary<string, string?>>? JsonRows { get; init; }
    /// <summary>The body could not be read as a file (e.g. multipart without a file part): reported as an Invalid result.</summary>
    public UploadError? FileError { get; init; }
    public bool RetireMissing { get; init; }
    /// <summary>The version the page validated against (apply refuses with 409 when the data moved on). Null = the current one.</summary>
    public int? BaseVersion { get; init; }
    public string Channel { get; init; } = "App";
    public required string Actor { get; init; }
    public string? ActorName { get; init; }
    public string? CorrelationId { get; init; }
}

/// <summary>
/// F4 (v1.1): validate → diff → apply (upsert by key, never delete) → version → one-click rollback, for the PMO mapping
/// sheet (I1) and the people inventory (I2). The Data uploads page and the external API use this same code. Any error
/// blocks the whole file (no partial apply): rows are written only after every row validated, and a failure part-way
/// is undone from the before-images. Two uploads never write at once (the head record is claimed with its ETag).
/// </summary>
public sealed class UploadService(DataStores stores, IClock clock, WorkflowSettings settings)
{
    public const string ModeValidate = "validate", ModeApply = "apply";
    /// <summary>Diff lines returned to the page (the counts are always complete).</summary>
    public const int MaxDiffRows = 500;
    private static readonly TimeSpan StaleClaim = TimeSpan.FromMinutes(10);
    /// <summary>Before-images per part (each part stays far below the Cosmos 2 MB item limit, F90).</summary>
    private const int MaxPartChars = 1_000_000;

    public int MaxUploadBytes => settings.MaxUploadBytes;

    // ---- page (signed-in DataMaintainer) ---------------------------------------------------------------------------

    public async Task<UploadsOverview> OverviewAsync(CallerContext caller, string projectId, CancellationToken ct)
    {
        caller.Require(projectId, Permission.ViewUploads);
        return new UploadsOverview(await HistoryAsync(projectId, DataKind.Mapping, ct), await HistoryAsync(projectId, DataKind.Inventory, ct),
            settings.MaxUploadBytes, settings.ExternalUploadApiEnabled);
    }

    public async Task<(int Status, UploadView View)> UploadAsync(CallerContext caller, string projectId, UploadInput input, CancellationToken ct)
    {
        caller.Require(projectId, Permission.UploadData);
        var project = await ProjectAsync(projectId, ct);
        return await RunAsync(project, input with { Channel = "App", Actor = caller.UserId, ActorName = caller.DisplayName }, $"upl-{Guid.NewGuid():N}", ct);
    }

    public async Task<DataVersionView> RollbackAsync(CallerContext caller, string projectId, DataKind kind, int version, string? cid, CancellationToken ct)
    {
        caller.Require(projectId, Permission.UploadData);
        var project = await ProjectAsync(projectId, ct);
        return kind == DataKind.Mapping
            ? await RollbackAsync(project, kind, version, Mapping, caller.UserId, caller.DisplayName, cid, ct)
            : await RollbackAsync(project, kind, version, People, caller.UserId, caller.DisplayName, cid, ct);
    }

    // ---- external API (app token, tenant-scoped; same code) ---------------------------------------------------------

    /// <summary>
    /// The external upload API. <paramref name="idempotencyKey"/> is required: the same key with the same body replays the
    /// first answer (nothing runs twice); the same key with another body is refused (409).
    /// </summary>
    public async Task<(int Status, UploadResult Result)> ExternalAsync(Project project, string appId, UploadInput input, string? idempotencyKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128 || idempotencyKey.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':')))
            throw AppException.Invalid("Header Idempotency-Key is required (1–128 characters: letters, digits, - _ . :).");
        var id = $"idem-{Ids.Hash($"{project.ProjectId}|{appId.Trim().ToLowerInvariant()}|{input.Kind}|{input.Mode}|{idempotencyKey}")}"; // per calling app (finding 16)
        var bodyHash = Sha256(input.JsonRows is null ? input.Content : Encoding.UTF8.GetBytes(AppJson.Serialize(input.JsonRows)))
                       + $"|{input.RetireMissing}|{input.Sheet}|{input.FileName}";
        if (await stores.AuditEvents.GetAsync(project.ProjectId, id, ct) is { } seen)
        {
            if (seen.Value.Details != bodyHash)
                throw AppException.Conflict("idempotency_key_reused", "This Idempotency-Key was used for another request. Use a new key for new content.");
            var stored = AppJson.Deserialize<StoredResult>(seen.Value.Result!)!;
            return (stored.Status, stored.Result);
        }
        var (status, view) = await RunAsync(project, input with { Channel = "ExternalApi", Actor = $"app:{appId}", ActorName = $"app {appId}" },
            $"upl-{Ids.Hash(id)[..24]}", ct);
        try
        {
            await stores.AuditEvents.CreateAsync(new AuditEvent
            {
                Id = id, ProjectId = project.ProjectId, At = clock.UtcNow, Actor = $"app:{appId}", Action = "upload-idempotency",
                Target = $"{input.Kind} {input.Mode}", CorrelationId = input.CorrelationId, Details = bodyHash,
                Result = AppJson.Serialize(new StoredResult(status, view.Upload)), IsSample = project.IsSample,
            }, ct);
        }
        catch (DocumentConflictException)
        {
            // The same key ran twice at the same moment: the head claim let only one apply write; both answers are kept as they are.
        }
        return (status, view.Upload);
    }

    private sealed record StoredResult(int Status, UploadResult Result);

    // ---- the one pipeline -------------------------------------------------------------------------------------------

    private async Task<(int Status, UploadView View)> RunAsync(Project project, UploadInput input, string uploadId, CancellationToken ct)
    {
        if (input.Mode is not (ModeValidate or ModeApply)) throw AppException.Invalid("mode must be validate or apply.");
        var head = await HeadAsync(project.ProjectId, input.Kind, ct);
        UploadView Invalid(IReadOnlyList<UploadError> errors) =>
            new(new UploadResult(uploadId, input.Mode, UploadResult.Invalid, null, new UploadCounts(0, 0, 0, 0), errors), head.Value.Version, 0, [], input.FileName);
        int Status(UploadView v) => v.Upload.Status == UploadResult.Invalid && input.Mode == ModeApply ? 422 : 200;

        if (input.FileError is { } fileError) return Done(Invalid([fileError]));
        if (input.JsonRows is null && input.Content.Length > settings.MaxUploadBytes)
            return Done(Invalid([new UploadError(0, "", "FileTooLarge", $"The file is larger than {settings.MaxUploadBytes / 1024:N0} KB (MAX_UPLOAD_BYTES).")]));
        Table table;
        try
        {
            table = input.JsonRows is { } json ? FromJson(json) : TabularFile.Read(input.Content, input.FileName, input.Sheet);
        }
        catch (UploadFileException e)
        {
            return Done(Invalid([new UploadError(0, "", e.Code, e.Message)]));
        }

        var view = input.Kind == DataKind.Mapping
            ? await PlanAndRunAsync(project, input, uploadId, head, Mapping, UploadSchema.Mapping(table, project, TenantOf(project)), ct)
            : await PlanAndRunAsync(project, input, uploadId, head, People, UploadSchema.Inventory(table), ct);
        return Done(view);

        (int, UploadView) Done(UploadView v) => (Status(v), v);
    }

    private async Task<UploadView> PlanAndRunAsync<TDoc, TRow>(Project project, UploadInput input, string uploadId, Versioned<DataVersion> head,
        Kind<TDoc, TRow> kind, (List<TRow> Rows, List<UploadError> Errors) parsed, CancellationToken ct) where TDoc : class, IDocument
    {
        if (parsed.Errors.Count > 0)
            return new UploadView(new UploadResult(uploadId, input.Mode, UploadResult.Invalid, null, new UploadCounts(0, 0, 0, 0), Sorted(parsed.Errors)),
                head.Value.Version, 0, [], input.FileName);
        var current = await kind.Store(stores).ListAsync(project.ProjectId, ct);
        var plan = Plan(project, kind, parsed.Rows, current, input.RetireMissing, head.Value.Version + 1, input.Actor);
        var counts = Count(plan);
        var diff = plan.Where(p => p.Change != "Unchanged").Take(MaxDiffRows).Select(p => new UploadDiffRow(p.Row, p.Change, p.Label, p.Fields)).ToList();
        var missing = plan.Count(p => p.Change == "Missing");
        if (input.Mode == ModeValidate)
            return new UploadView(new UploadResult(uploadId, ModeValidate, UploadResult.Valid, null, counts, []), head.Value.Version, missing, diff, input.FileName);

        if (input.BaseVersion is { } baseVersion && baseVersion != head.Value.Version)
            throw AppException.Conflict("stale_upload", $"The {Label(input.Kind)} changed since you validated it (version v{baseVersion} → v{head.Value.Version}). Validate the file again.");
        if (plan.All(p => p.Change is "Unchanged" or "Missing"))
            return new UploadView(new UploadResult(uploadId, ModeApply, UploadResult.Applied, Tag(head.Value.Version), counts, []), head.Value.Version, missing, diff, input.FileName);

        // Claim the head (ETag): only one upload writes at a time. Then plan again on fresh data: what is written is what
        // the current data needs, never a stale diff.
        var number = head.Value.Version + 1;
        var claimed = await ClaimAsync(project, input.Kind, head, number, input.Actor, kind, ct);
        try
        {
            current = await kind.Store(stores).ListAsync(project.ProjectId, ct);
            plan = Plan(project, kind, parsed.Rows, current, input.RetireMissing, number, input.Actor);
            counts = Count(plan);
            var hash = input.JsonRows is null ? Sha256(input.Content) : Sha256(Encoding.UTF8.GetBytes(AppJson.Serialize(input.JsonRows)));
            await WriteAsync(project, input.Kind, number, plan, kind, ct);
            var summary = $"{counts.Added} added, {counts.Updated} updated, {counts.Unchanged} unchanged, {counts.Retired} retired, {missing - counts.Retired} missing left as they are";
            var record = new DataVersion
            {
                Id = DataVersion.MakeId(input.Kind, number), ProjectId = project.ProjectId, Kind = input.Kind, Version = number, Parts = Parts(plan).Count,
                UploadedBy = input.ActorName ?? input.Actor, UploadedAt = clock.UtcNow, FileName = input.FileName, FileHash = hash,
                Counts = new DataCounts { Added = counts.Added, Updated = counts.Updated, Unchanged = counts.Unchanged, Retired = counts.Retired, Missing = missing - counts.Retired },
                DiffSummary = summary, RetireMissing = input.RetireMissing, Channel = input.Channel,
            };
            await SaveAsync(project.ProjectId, input.Kind, record, ct);
            await ReleaseAsync(project.ProjectId, input.Kind, claimed, number, ct);
            await AuditAsync(project, input.Actor, "upload-applied", $"{input.Kind} {Tag(number)}",
                $"{input.Channel}; file {input.FileName ?? "JSON rows"}; sha256 {hash}; {summary}", input.CorrelationId, ct);
            return new UploadView(new UploadResult(uploadId, ModeApply, UploadResult.Applied, Tag(number), counts, []), number, missing,
                plan.Where(p => p.Change != "Unchanged").Take(MaxDiffRows).Select(p => new UploadDiffRow(p.Row, p.Change, p.Label, p.Fields)).ToList(), input.FileName);
        }
        catch
        {
            await ReleaseAsync(project.ProjectId, input.Kind, claimed, null, CancellationToken.None);
            throw;
        }
    }

    // ---- plan ---------------------------------------------------------------------------------------------------------

    /// <summary>One planned change: what the diff shows, and (for writes) the document before and after.</summary>
    private sealed record Item<TDoc>(string Change, int Row, string Label, IReadOnlyList<string> Fields, Versioned<TDoc>? Before, TDoc? After) where TDoc : class;

    private List<Item<TDoc>> Plan<TDoc, TRow>(Project project, Kind<TDoc, TRow> kind, List<TRow> rows, IReadOnlyList<Versioned<TDoc>> current, bool retireMissing,
        int number, string actor) where TDoc : class, IDocument
    {
        var tenant = TenantOf(project);
        var now = clock.UtcNow;
        var byKey = new Dictionary<string, Versioned<TDoc>>(StringComparer.Ordinal);
        foreach (var v in current.OrderBy(v => kind.IsRetired(v.Value)))
            byKey.TryAdd(kind.KeyOfDoc(v.Value, tenant), v);
        var items = new List<Item<TDoc>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var key = kind.KeyOfRow(row);
            seen.Add(key);
            if (!byKey.TryGetValue(key, out var existing))
            {
                items.Add(new Item<TDoc>("Added", kind.RowNumber(row), kind.LabelOfRow(row), [], null, kind.Create(project, row, key, number, actor, now)));
                continue;
            }
            var updated = kind.Update(existing.Value, row, number, actor, now);
            var fields = kind.Changed(existing.Value, updated);
            items.Add(fields.Count == 0
                ? new Item<TDoc>("Unchanged", kind.RowNumber(row), kind.LabelOfRow(row), [], existing, null)
                : new Item<TDoc>("Updated", kind.RowNumber(row), kind.LabelOfRow(row), fields, existing, updated));
        }
        foreach (var v in current.Where(v => !kind.IsRetired(v.Value) && !seen.Contains(kind.KeyOfDoc(v.Value, tenant))))
            items.Add(retireMissing
                ? new Item<TDoc>("Retired", kind.SheetRowOf(v.Value), kind.LabelOfDoc(v.Value), ["Retired"], v, kind.Retire(v.Value, number, actor, now))
                : new Item<TDoc>("Missing", kind.SheetRowOf(v.Value), kind.LabelOfDoc(v.Value), [], v, null));
        return items;
    }

    private static UploadCounts Count<TDoc>(List<Item<TDoc>> plan) where TDoc : class =>
        new(plan.Count(p => p.Change == "Added"), plan.Count(p => p.Change == "Updated"), plan.Count(p => p.Change == "Unchanged"), plan.Count(p => p.Change == "Retired"));

    // ---- write, undo, rollback ----------------------------------------------------------------------------------------

    /// <summary>Before-images in parts (Before = documents as they were, Added = ids created by this version).</summary>
    private static List<(List<string> Before, List<string> Added)> Parts<TDoc>(List<Item<TDoc>> plan) where TDoc : class, IDocument
    {
        var parts = new List<(List<string> Before, List<string> Added)>();
        (List<string> Before, List<string> Added) part = ([], []);
        var size = 0;
        foreach (var p in plan.Where(p => p.After is not null))
        {
            var text = p.Before is null ? p.After!.Id : AppJson.Serialize(p.Before.Value);
            if (size + text.Length > MaxPartChars && size > 0) { parts.Add(part); part = ([], []); size = 0; }
            if (p.Before is null) part.Added.Add(text); else part.Before.Add(text);
            size += text.Length;
        }
        if (size > 0) parts.Add(part);
        return parts;
    }

    private async Task WriteAsync<TDoc, TRow>(Project project, DataKind dataKind, int number, List<Item<TDoc>> plan, Kind<TDoc, TRow> kind, CancellationToken ct)
        where TDoc : class, IDocument
    {
        var parts = Parts(plan);
        for (var i = 0; i < parts.Count; i++)
            await SaveAsync(project.ProjectId, dataKind, new DataVersion
            {
                Id = DataVersion.MakeId(dataKind, number, i + 1), ProjectId = project.ProjectId, Kind = dataKind, Version = number, Part = i + 1,
                Parts = parts.Count, UploadedAt = clock.UtcNow, Before = parts[i].Before, Added = parts[i].Added,
            }, ct);
        var written = new List<Item<TDoc>>();
        try
        {
            foreach (var p in plan.Where(p => p.After is not null))
            {
                if (p.Before is null) await kind.Store(stores).CreateAsync(p.After!, ct);
                else await kind.Store(stores).ReplaceAsync(p.After!, p.Before.ETag, ct);
                written.Add(p);
            }
        }
        catch (Exception e) when (e is ConcurrencyException or DocumentConflictException)
        {
            await UndoAsync(project.ProjectId, kind, written, number, CancellationToken.None);
            throw AppException.Conflict("upload_conflict", "Rows changed while the upload was applied, so nothing was kept. Validate the file again.");
        }
        catch
        {
            await UndoAsync(project.ProjectId, kind, written, number, CancellationToken.None);
            throw;
        }
    }

    /// <summary>No partial apply: puts back what this attempt wrote (created rows are retired; the store keeps every row).</summary>
    private async Task UndoAsync<TDoc, TRow>(string projectId, Kind<TDoc, TRow> kind, List<Item<TDoc>> written, int number, CancellationToken ct) where TDoc : class, IDocument
    {
        foreach (var p in Enumerable.Reverse(written))
        {
            var now = await kind.Store(stores).GetAsync(projectId, p.After!.Id, ct);
            if (now is null) continue;
            var restored = p.Before is null ? kind.Retire(now.Value, number, "upload undo", clock.UtcNow) : kind.WithVersion(p.Before.Value, kind.VersionOf(now.Value) + 1);
            await kind.Store(stores).ReplaceAsync(restored, now.ETag, ct);
        }
    }

    private async Task<DataVersionView> RollbackAsync<TDoc, TRow>(Project project, DataKind dataKind, int version, Kind<TDoc, TRow> kind, string actor, string? actorName,
        string? cid, CancellationToken ct) where TDoc : class, IDocument
    {
        var head = await HeadAsync(project.ProjectId, dataKind, ct);
        if (version != head.Value.Version || version == 0)
            throw AppException.Conflict("not_latest", $"Only the current version (v{head.Value.Version}) can be rolled back.");
        var target = await GetVersionAsync(project.ProjectId, dataKind, version, 0, ct) ?? throw AppException.NotFound("Version");
        var number = version + 1;
        var claimed = await ClaimAsync(project, dataKind, head, number, actor, kind, ct);
        try
        {
            var (before, added) = await BeforeImagesAsync(project.ProjectId, dataKind, version, target.Value.Parts, ct);
            var plan = new List<Item<TDoc>>();
            var changed = new List<string>();
            foreach (var json in before)
            {
                var old = JsonSerializer.Deserialize<TDoc>(json, AppJson.Options)!;
                var now = await kind.Store(stores).GetAsync(project.ProjectId, old.Id, ct);
                if (now is null || kind.VersionOf(now.Value) != kind.VersionOf(old) + 1) { changed.Add(kind.LabelOfDoc(old)); continue; }
                var restored = kind.Stamp(kind.WithVersion(old, kind.VersionOf(now.Value) + 1), number, actor, clock.UtcNow);
                plan.Add(new Item<TDoc>("Updated", kind.SheetRowOf(old), kind.LabelOfDoc(old), kind.Changed(now.Value, restored), now, restored));
            }
            foreach (var id in added)
            {
                var now = await kind.Store(stores).GetAsync(project.ProjectId, id, ct);
                if (now is null || kind.VersionOf(now.Value) != 1) { changed.Add(now is null ? id : kind.LabelOfDoc(now.Value)); continue; }
                plan.Add(new Item<TDoc>("Retired", kind.SheetRowOf(now.Value), kind.LabelOfDoc(now.Value), ["Retired"], now, kind.Retire(now.Value, number, actor, clock.UtcNow)));
            }
            if (changed.Count > 0)
                throw AppException.Conflict("changed_since_upload",
                    $"{changed.Count} row(s) changed after v{version} was applied, so the rollback would overwrite them: {string.Join("; ", changed.Take(5))}. Upload a corrected file instead.");
            await WriteAsync(project, dataKind, number, plan, kind, ct);
            var record = new DataVersion
            {
                Id = DataVersion.MakeId(dataKind, number), ProjectId = project.ProjectId, Kind = dataKind, Version = number, Parts = Parts(plan).Count,
                UploadedBy = actorName ?? actor, UploadedAt = clock.UtcNow, FileName = target.Value.FileName, FileHash = target.Value.FileHash, RollbackOf = version,
                Counts = new DataCounts { Updated = plan.Count(p => p.Change == "Updated"), Retired = plan.Count(p => p.Change == "Retired") },
                DiffSummary = $"Rollback of v{version}: {plan.Count(p => p.Change == "Updated")} row(s) restored, {plan.Count(p => p.Change == "Retired")} added row(s) retired",
                Channel = "App",
            };
            await SaveAsync(project.ProjectId, dataKind, record, ct);
            await stores.VersionsOf(dataKind).ReplaceAsync(target.Value with { State = DataVersionState.RolledBack, RolledBackBy = actorName ?? actor, RolledBackAt = clock.UtcNow }, target.ETag, ct);
            await ReleaseAsync(project.ProjectId, dataKind, claimed, number, ct);
            await AuditAsync(project, actor, "upload-rolled-back", $"{dataKind} {Tag(version)}", record.DiffSummary, cid, ct);
            return View(record);
        }
        catch
        {
            await ReleaseAsync(project.ProjectId, dataKind, claimed, null, CancellationToken.None);
            throw;
        }
    }

    private async Task<(List<string> Before, List<string> Added)> BeforeImagesAsync(string projectId, DataKind kind, int version, int parts, CancellationToken ct)
    {
        var before = new List<string>();
        var added = new List<string>();
        for (var i = 1; i <= parts; i++)
        {
            var p = await GetVersionAsync(projectId, kind, version, i, ct);
            if (p is null) continue;
            before.AddRange(p.Value.Before);
            added.AddRange(p.Value.Added);
        }
        return (before, added);
    }

    // ---- head claim -----------------------------------------------------------------------------------------------------

    private async Task<Versioned<DataVersion>> HeadAsync(string projectId, DataKind kind, CancellationToken ct)
    {
        var store = stores.VersionsOf(kind);
        if (await store.GetAsync(projectId, DataVersion.HeadId(kind), ct) is { } head) return head;
        try
        {
            return await store.CreateAsync(new DataVersion { Id = DataVersion.HeadId(kind), ProjectId = projectId, Kind = kind, Version = 0, UploadedAt = clock.UtcNow }, ct);
        }
        catch (DocumentConflictException)
        {
            return (await store.GetAsync(projectId, DataVersion.HeadId(kind), ct))!;
        }
    }

    /// <summary>Claims the head for <paramref name="number"/>; a crashed claim (older than 10 minutes) is undone first.</summary>
    private async Task<Versioned<DataVersion>> ClaimAsync<TDoc, TRow>(Project project, DataKind dataKind, Versioned<DataVersion> head, int number, string actor,
        Kind<TDoc, TRow> kind, CancellationToken ct) where TDoc : class, IDocument
    {
        if (head.Value.Applying is { } busy)
        {
            if (clock.UtcNow - (head.Value.ApplyingSince ?? DateTimeOffset.MinValue) < StaleClaim)
                throw AppException.Conflict("upload_in_progress", $"Another upload of the {Label(dataKind)} is being applied (v{busy}). Try again in a minute.");
            await RecoverAsync(project.ProjectId, dataKind, busy, kind, ct);
            head = await HeadAsync(project.ProjectId, dataKind, ct);
            if (head.Value.Version != number - 1)
                throw AppException.Conflict("stale_upload", $"The {Label(dataKind)} is now at v{head.Value.Version}. Validate the file again.");
        }
        try
        {
            return await stores.VersionsOf(dataKind).ReplaceAsync(head.Value with { Applying = number, ApplyingBy = actor, ApplyingSince = clock.UtcNow }, head.ETag, ct);
        }
        catch (ConcurrencyException)
        {
            throw AppException.Conflict("upload_in_progress", $"Another upload of the {Label(dataKind)} started at the same time. Validate the file again.");
        }
    }

    private async Task ReleaseAsync(string projectId, DataKind kind, Versioned<DataVersion> claimed, int? newVersion, CancellationToken ct)
    {
        var store = stores.VersionsOf(kind);
        var head = await store.GetAsync(projectId, DataVersion.HeadId(kind), ct);
        if (head is null || head.Value.Applying != claimed.Value.Applying) return;
        await store.ReplaceAsync(head.Value with { Version = newVersion ?? head.Value.Version, Applying = null, ApplyingBy = null, ApplyingSince = null, UploadedAt = clock.UtcNow }, head.ETag, ct);
    }

    /// <summary>A claim left by a crashed apply: put back the rows it wrote (from its parts), then free the head.</summary>
    private async Task RecoverAsync<TDoc, TRow>(string projectId, DataKind dataKind, int version, Kind<TDoc, TRow> kind, CancellationToken ct) where TDoc : class, IDocument
    {
        if (await GetVersionAsync(projectId, dataKind, version, 0, ct) is null)
        {
            for (var i = 1; await GetVersionAsync(projectId, dataKind, version, i, ct) is { } part; i++)
            {
                foreach (var json in part.Value.Before)
                {
                    var old = JsonSerializer.Deserialize<TDoc>(json, AppJson.Options)!;
                    var now = await kind.Store(stores).GetAsync(projectId, old.Id, ct);
                    if (now is not null && kind.VersionOf(now.Value) == kind.VersionOf(old) + 1)
                        await kind.Store(stores).ReplaceAsync(kind.WithVersion(old, kind.VersionOf(now.Value) + 1), now.ETag, ct);
                }
                foreach (var id in part.Value.Added)
                    if (await kind.Store(stores).GetAsync(projectId, id, ct) is { } now && kind.VersionOf(now.Value) == 1)
                        await kind.Store(stores).ReplaceAsync(kind.Retire(now.Value, version, "upload recovery", clock.UtcNow), now.ETag, ct);
            }
        }
        var head = (await stores.VersionsOf(dataKind).GetAsync(projectId, DataVersion.HeadId(dataKind), ct))!;
        var finished = await GetVersionAsync(projectId, dataKind, version, 0, ct) is not null;
        await stores.VersionsOf(dataKind).ReplaceAsync(head.Value with { Version = finished ? version : head.Value.Version, Applying = null, ApplyingBy = null, ApplyingSince = null }, head.ETag, ct);
    }

    // ---- storage helpers -------------------------------------------------------------------------------------------------

    private Task<Versioned<DataVersion>?> GetVersionAsync(string projectId, DataKind kind, int version, int part, CancellationToken ct) =>
        stores.VersionsOf(kind).GetAsync(projectId, DataVersion.MakeId(kind, version, part), ct);

    /// <summary>Create, or replace a leftover of a crashed apply with the same id.</summary>
    private async Task SaveAsync(string projectId, DataKind kind, DataVersion v, CancellationToken ct)
    {
        var store = stores.VersionsOf(kind);
        try
        {
            await store.CreateAsync(v, ct);
        }
        catch (DocumentConflictException)
        {
            var old = (await store.GetAsync(projectId, v.Id, ct))!;
            await store.ReplaceAsync(v, old.ETag, ct);
        }
    }

    private async Task<UploadHistory> HistoryAsync(string projectId, DataKind kind, CancellationToken ct)
    {
        var all = (await stores.VersionsOf(kind).ListAsync(projectId, ct)).Select(v => v.Value).ToList();
        var head = all.FirstOrDefault(v => v.Id == DataVersion.HeadId(kind));
        var versions = all.Where(v => v.Part == 0 && v.Id != DataVersion.HeadId(kind)).OrderByDescending(v => v.Version).Select(View).ToList();
        return new UploadHistory(kind, head?.Version ?? 0, UploadSchema.Columns(kind), versions);
    }

    private static DataVersionView View(DataVersion v) => new(Tag(v.Version), v.Version, v.State, v.UploadedBy, v.UploadedAt, v.FileName, v.FileHash, v.Counts,
        v.DiffSummary, v.RetireMissing, v.Channel, v.RollbackOf, v.RolledBackBy, v.RolledBackAt);

    private async Task AuditAsync(Project project, string actor, string action, string target, string details, string? cid, CancellationToken ct) =>
        await stores.AuditEvents.CreateAsync(new AuditEvent
        {
            Id = AuditEvent.NewId(clock.UtcNow), ProjectId = project.ProjectId, At = clock.UtcNow, Actor = actor, Action = action, Target = target,
            CorrelationId = cid, Details = SecretMasker.Apply(details), IsSample = project.IsSample,
        }, ct);

    private async Task<Project> ProjectAsync(string projectId, CancellationToken ct) =>
        (await stores.Projects.GetAsync(projectId, projectId, ct))?.Value ?? throw AppException.NotFound("Project");

    private string? TenantOf(Project p) => p.TenantId ?? settings.AgentTenantId;

    private static string Tag(int version) => $"v{version}";
    private static string Label(DataKind kind) => kind == DataKind.Mapping ? "PMO mapping sheet" : "people inventory";
    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static IReadOnlyList<UploadError> Sorted(List<UploadError> errors) => errors.OrderBy(e => e.Row).ThenBy(e => e.Column, StringComparer.Ordinal).ToList();

    /// <summary>JSON rows of the external API as a table (each object = one row; values as text, never interpreted).</summary>
    private static Table FromJson(IReadOnlyList<IReadOnlyDictionary<string, string?>> rows)
    {
        if (rows.Count == 0) throw new UploadFileException("NoRows", "\"rows\" is empty.");
        if (rows.Count > TabularFile.MaxRows) throw new UploadFileException("TooManyRows", $"More than {TabularFile.MaxRows:N0} rows.");
        var headers = rows.SelectMany(r => r.Keys).Select(k => k.Trim()).Where(k => k.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (headers.Count > TabularFile.MaxColumns) throw new UploadFileException("InvalidFile", $"More than {TabularFile.MaxColumns} columns.");
        var table = rows.Select((r, i) => new TableRow(i + 1,
            r.Where(kv => kv.Key.Trim().Length > 0).GroupBy(kv => kv.Key.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => (g.First().Value ?? "") is var v && v.Length > TabularFile.MaxCellChars ? v[..(TabularFile.MaxCellChars + 1)] : g.First().Value ?? "",
                    StringComparer.OrdinalIgnoreCase),
            new HashSet<string>())).ToList();
        return new Table(headers, table, FromWorkbook: false);
    }

    // ---- the two data sets -----------------------------------------------------------------------------------------------

    /// <summary>How one data set is keyed, created, updated, compared and retired.</summary>
    private sealed record Kind<TDoc, TRow>(
        Func<DataStores, IDocumentStore<TDoc>> Store,
        Func<TRow, string> KeyOfRow,
        Func<TDoc, string?, string> KeyOfDoc,
        Func<TRow, int> RowNumber,
        Func<TRow, string> LabelOfRow,
        Func<TDoc, string> LabelOfDoc,
        Func<TDoc, int> SheetRowOf,
        Func<TDoc, bool> IsRetired,
        Func<TDoc, int> VersionOf,
        Func<TDoc, int, TDoc> WithVersion,
        Func<Project, TRow, string, int, string, DateTimeOffset, TDoc> Create,
        Func<TDoc, TRow, int, string, DateTimeOffset, TDoc> Update,
        Func<TDoc, int, string, DateTimeOffset, TDoc> Retire,
        Func<TDoc, int, string, DateTimeOffset, TDoc> Stamp,
        Func<TDoc, TDoc, IReadOnlyList<string>> Changed) where TDoc : class, IDocument;

    /// <summary>
    /// I1 → mapping rows. The upload sets the I1 fields only: Azure roles, red/amber, privileged, notes and the role stay as
    /// they are on existing rows (decision 8). A new row gets no Azure role (the role assignment team or PMO adds them).
    /// </summary>
    private static readonly Kind<MappingRow, MappingUpload> Mapping = new(
        s => s.MappingSheet,
        r => r.Key,
        (d, tenant) => UploadSchema.KeyOf(d, tenant),
        r => r.Row,
        r => $"{r.EntraGroupName} — {r.AccessLevel}, {r.Environment}, {r.Domain} ({r.ResourceGroupType})",
        d => $"{d.EntraGroup} — {d.AccessLevel}, {d.Environment ?? "every environment"}, {d.Domain ?? d.Resource}",
        d => d.SheetRow ?? 0,
        d => d.Retired,
        d => d.Version,
        (d, v) => d with { Version = v },
        (p, r, key, n, actor, now) =>
        {
            var id = $"U-{Ids.Hash(key)[..16]}";
            return new MappingRow
            {
                Id = id, ProjectId = p.ProjectId, RowId = id, Resource = r.Domain, AccessLevel = r.AccessLevel, EntraGroup = r.EntraGroupName, Role = r.AccessLevel,
                Environment = r.Environment, GroupObjectId = r.EntraGroupObjectId, TenantId = r.TenantId, Domain = r.Domain, ResourceGroupType = r.ResourceGroupType,
                Owner = r.Owner, EffectiveFrom = r.EffectiveFrom, EffectiveTo = r.EffectiveTo, MappingVersion = n, SheetRow = r.Row,
                SourceReference = $"PMO mapping sheet upload v{n}, row {r.Row}", Version = 1, UpdatedBy = actor, UpdatedAt = now, IsSample = p.IsSample,
            };
        },
        (d, r, n, actor, now) => d with
        {
            TenantId = r.TenantId, Domain = r.Domain, ResourceGroupType = r.ResourceGroupType, Environment = r.Environment, AccessLevel = r.AccessLevel,
            EntraGroup = r.EntraGroupName, GroupObjectId = r.EntraGroupObjectId ?? d.GroupObjectId, Owner = r.Owner, EffectiveFrom = r.EffectiveFrom,
            EffectiveTo = r.EffectiveTo, Retired = false, MappingVersion = n, SheetRow = r.Row, Version = d.Version + 1, UpdatedBy = actor, UpdatedAt = now,
        },
        (d, n, actor, now) => d with { Retired = true, MappingVersion = n, Version = d.Version + 1, UpdatedBy = actor, UpdatedAt = now },
        (d, n, actor, now) => d with { MappingVersion = n, UpdatedBy = actor, UpdatedAt = now },
        (a, b) => Diff(
            ("TenantId", a.TenantId, b.TenantId), ("Domain", a.Domain, b.Domain), ("ResourceGroupType", a.ResourceGroupType, b.ResourceGroupType),
            ("Environment", a.Environment, b.Environment), ("AccessLevel", a.AccessLevel, b.AccessLevel), ("EntraGroupName", a.EntraGroup, b.EntraGroup),
            ("EntraGroupObjectId", a.GroupObjectId, b.GroupObjectId), ("Owner", a.Owner, b.Owner), ("EffectiveFrom", a.EffectiveFrom?.ToString("O"), b.EffectiveFrom?.ToString("O")),
            ("EffectiveTo", a.EffectiveTo?.ToString("O"), b.EffectiveTo?.ToString("O")), ("Retired", a.Retired.ToString(), b.Retired.ToString())));

    /// <summary>I2 → people inventory (key: the Enterprise ID).</summary>
    private static readonly Kind<Person, PersonUpload> People = new(
        s => s.PeopleInventory,
        r => r.Key,
        (d, _) => Person.KeyOf(d.EnterpriseId),
        r => r.Row,
        r => $"{r.DisplayName} ({r.EnterpriseId})",
        d => $"{d.DisplayName} ({d.EnterpriseId})",
        d => d.SheetRow ?? 0,
        d => d.Retired,
        d => d.Version,
        (d, v) => d with { Version = v },
        (p, r, key, n, actor, now) => new Person
        {
            Id = Person.MakeId(r.EnterpriseId), ProjectId = p.ProjectId, EnterpriseId = r.EnterpriseId, EnterpriseEmail = r.EnterpriseEmail, DisplayName = r.DisplayName,
            EmployeeId = r.EmployeeId, Domain = r.Domain, ManagerEnterpriseId = r.ManagerEnterpriseId, Status = r.Status, AlternateEmail = r.AlternateEmail,
            InventoryVersion = n, SheetRow = r.Row, Version = 1, UpdatedBy = actor, UpdatedAt = now, IsSample = p.IsSample,
        },
        (d, r, n, actor, now) => d with
        {
            EnterpriseId = r.EnterpriseId, EnterpriseEmail = r.EnterpriseEmail, DisplayName = r.DisplayName, EmployeeId = r.EmployeeId, Domain = r.Domain,
            ManagerEnterpriseId = r.ManagerEnterpriseId, Status = r.Status, AlternateEmail = r.AlternateEmail, Retired = false, InventoryVersion = n, SheetRow = r.Row,
            Version = d.Version + 1, UpdatedBy = actor, UpdatedAt = now,
        },
        (d, n, actor, now) => d with { Retired = true, InventoryVersion = n, Version = d.Version + 1, UpdatedBy = actor, UpdatedAt = now },
        (d, n, actor, now) => d with { InventoryVersion = n, UpdatedBy = actor, UpdatedAt = now },
        (a, b) => Diff(
            ("EnterpriseId", a.EnterpriseId, b.EnterpriseId), ("EnterpriseEmail", a.EnterpriseEmail, b.EnterpriseEmail), ("DisplayName", a.DisplayName, b.DisplayName),
            ("EmployeeId", a.EmployeeId, b.EmployeeId), ("Domain", a.Domain, b.Domain), ("ManagerEnterpriseId", a.ManagerEnterpriseId, b.ManagerEnterpriseId),
            ("Status", a.Status.ToString(), b.Status.ToString()), ("AlternateEmail", a.AlternateEmail, b.AlternateEmail), ("Retired", a.Retired.ToString(), b.Retired.ToString())));

    private static IReadOnlyList<string> Diff(params (string Field, string? Before, string? After)[] fields) =>
        fields.Where(f => !string.Equals(f.Before ?? "", f.After ?? "", StringComparison.Ordinal)).Select(f => f.Field).ToList();
}
