using System.Text.Json.Serialization;

namespace PmoReview.Core.Domain;

// v1.1 feature pack (Gokul, 30 Oct 2026): uploads with versions, the people inventory, escalations, mapping quality
// issues, worker group resolutions, offboarding runs and the append-only audit trail. Every record belongs to one
// project (= one Entra tenant) and is partitioned by it; every update uses the store's ETag.

/// <summary>The two uploadable data sets (F4).</summary>
public enum DataKind { Mapping, Inventory }

public enum DataVersionState { Applied, RolledBack }

/// <summary>Row counts of one upload against the current data.</summary>
public sealed record DataCounts
{
    public int Added { get; init; }
    public int Updated { get; init; }
    public int Unchanged { get; init; }
    /// <summary>Rows missing from the file that this upload retired ("Retire missing rows" ticked).</summary>
    public int Retired { get; init; }
    /// <summary>Rows missing from the file and left as they are (not ticked).</summary>
    public int Missing { get; init; }
}

/// <summary>
/// One applied upload (F4 step 5), or one part of it. Part 0 is the version itself (who, when, file hash, counts, diff);
/// parts 1..n hold the rows as they were before this upload (JSON), so a rollback restores them exactly. Parts keep each
/// item well below the Cosmos 2 MB item limit (VERIFIED_FACTS F90). Containers: <c>mappingVersions</c>, <c>inventoryVersions</c>.
/// </summary>
public sealed record DataVersion : IDocument
{
    /// <summary><see cref="MakeId"/>.</summary>
    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    public required DataKind Kind { get; init; }
    public required int Version { get; init; }
    /// <summary>0 = the version record; 1..<see cref="Parts"/> = before-images.</summary>
    public int Part { get; init; }
    public int Parts { get; init; }
    public string? UploadedBy { get; init; }
    public DateTimeOffset UploadedAt { get; init; }
    public string? FileName { get; init; }
    /// <summary>SHA-256 of the uploaded file (or of the canonical JSON rows for the external API), hex.</summary>
    public string? FileHash { get; init; }
    public DataCounts Counts { get; init; } = new();
    public string? DiffSummary { get; init; }
    public bool RetireMissing { get; init; }
    public DataVersionState State { get; init; } = DataVersionState.Applied;
    /// <summary>Set when this version undid an earlier one (one-click rollback).</summary>
    public int? RollbackOf { get; init; }
    public string? RolledBackBy { get; init; }
    public DateTimeOffset? RolledBackAt { get; init; }
    /// <summary>Upload channel: "App" (Data uploads page) or "ExternalApi".</summary>
    public string Channel { get; init; } = "App";
    /// <summary>Parts only: rows as they were before this version (serialized documents); <see cref="Added"/> did not exist.</summary>
    public IReadOnlyList<string> Before { get; init; } = [];
    /// <summary>Parts only: ids of the rows this version added.</summary>
    public IReadOnlyList<string> Added { get; init; } = [];
    /// <summary>
    /// Head record only (<see cref="HeadId"/>; <see cref="Version"/> = the current version, 0 before the first upload): the
    /// version an apply or rollback is writing now, by whom and since when. Claimed with the head's ETag, so two uploads
    /// never write at once; a claim older than 10 minutes is a crashed apply and is undone from its parts.
    /// </summary>
    public int? Applying { get; init; }
    public string? ApplyingBy { get; init; }
    public DateTimeOffset? ApplyingSince { get; init; }
    public int SchemaVersion { get; init; } = 1;
    [JsonIgnore] public string PartitionKey => ProjectId;

    public static string MakeId(DataKind kind, int version, int part = 0) =>
        part == 0 ? $"{kind.ToString().ToLowerInvariant()}-v{version}" : $"{kind.ToString().ToLowerInvariant()}-v{version}-p{part}";

    public static string HeadId(DataKind kind) => $"{kind.ToString().ToLowerInvariant()}-head";
}

public enum PersonStatus { Active, Leaver }

/// <summary>
/// One person of the people inventory (I2, uploaded by the DataMaintainer). The only source of the TAP recipient (F2):
/// <see cref="EnterpriseEmail"/>, after a cross-check with the Entra user. <see cref="AlternateEmail"/> is never used for a TAP.
/// </summary>
public sealed record Person : IDocument
{
    /// <summary><see cref="MakeId"/> of the Enterprise ID (storage-safe; Cosmos ids should avoid '@', F90).</summary>
    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    /// <summary>The person's UPN in the tenant.</summary>
    public required string EnterpriseId { get; init; }
    public string? EnterpriseEmail { get; init; }
    public required string DisplayName { get; init; }
    public string? EmployeeId { get; init; }
    public string? Domain { get; init; }
    public string? ManagerEnterpriseId { get; init; }
    public PersonStatus Status { get; init; } = PersonStatus.Active;
    /// <summary>Optional. NEVER used as a TAP recipient.</summary>
    public string? AlternateEmail { get; init; }
    /// <summary>Retired by an upload with "Retire missing rows" (kept, never deleted).</summary>
    public bool Retired { get; init; }
    public int Version { get; init; } = 1;
    /// <summary>The inventory version that last changed this person.</summary>
    public int InventoryVersion { get; init; }
    /// <summary>Row in the uploaded file (for messages).</summary>
    public int? SheetRow { get; init; }
    public required string UpdatedBy { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public bool IsSample { get; init; }
    public int SchemaVersion { get; init; } = 1;
    [JsonIgnore] public string PartitionKey => ProjectId;

    /// <summary>The upload key (I2): the Enterprise ID, trimmed and lower case.</summary>
    public static string KeyOf(string enterpriseId) => enterpriseId.Trim().ToLowerInvariant();

    public static string MakeId(string enterpriseId) => $"person-{Workflow.Ids.Hash(KeyOf(enterpriseId))}";
}

public enum EscalationKind
{
    /// <summary>F3: a level above Read in Prod. Only a ProdApprover decides.</summary>
    ProdWrite,
    /// <summary>F1: a mapping group name that does not resolve to an Entra object id (never granted).</summary>
    UnresolvedGroup,
    /// <summary>F2: the TAP recipient could not be verified; nothing was sent.</summary>
    TapRecipientMismatch,
    /// <summary>F5: verification found something left after offboarding.</summary>
    OffboardingLeftover,
}

public enum EscalationStatus { Open, Approved, Rejected }

/// <summary>
/// A decision a person must take because the system will not (F1, F2, F3, F5). <see cref="Id"/> is the idempotency key,
/// so the same situation raises one escalation. A ProdWrite escalation is bound to <see cref="RequestRevision"/> and
/// <see cref="ScopeFingerprint"/>: a decision on an older revision or another scope does not count.
/// </summary>
public sealed record Escalation : IDocument
{
    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    public required EscalationKind Kind { get; init; }
    public string? RequestId { get; init; }
    public int? RequestRevision { get; init; }
    /// <summary>What the approvers saw (group, level, Azure roles, environment); the decision counts only for this.</summary>
    public string? ScopeFingerprint { get; init; }
    /// <summary>The person the access is for (Enterprise ID or domain-specific ID).</summary>
    public string? User { get; init; }
    public string? Group { get; init; }
    public string? Level { get; init; }
    public string? Environment { get; init; }
    public required string Reason { get; init; }
    public required string RequestedBy { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public EscalationStatus Status { get; init; } = EscalationStatus.Open;
    public string? DecidedBy { get; init; }
    public string? DecidedByName { get; init; }
    public DateTimeOffset? DecidedAt { get; init; }
    public string? Remarks { get; init; }
    public string? CorrelationId { get; init; }
    public bool IsSample { get; init; }
    public int SchemaVersion { get; init; } = 1;
    [JsonIgnore] public string PartitionKey => ProjectId;

    public static string MakeId(EscalationKind kind, string key) => $"esc-{Workflow.Ids.Hash($"{kind}|{key}")}";
}

public enum QualityIssueStatus { Open, Resolved }

/// <summary>F1: a near-duplicate group name on the mapping sheet. The variant is hidden, never merged silently.</summary>
public sealed record MappingQualityIssue : IDocument
{
    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    public required string VariantName { get; init; }
    public required string CanonicalName { get; init; }
    public int SheetRow { get; init; }
    public required string Reason { get; init; }
    /// <summary>The mapping version the issue was found in.</summary>
    public string? MappingVersion { get; init; }
    public QualityIssueStatus Status { get; init; } = QualityIssueStatus.Open;
    public required DateTimeOffset FirstSeenAt { get; init; }
    public required DateTimeOffset LastSeenAt { get; init; }
    public bool IsSample { get; init; }
    public int SchemaVersion { get; init; } = 1;
    [JsonIgnore] public string PartitionKey => ProjectId;

    public static string MakeId(string variantName, string canonicalName) => $"mqi-{Workflow.Ids.Hash($"{variantName.Trim().ToLowerInvariant()}|{canonicalName.Trim().ToLowerInvariant()}")}";
}

/// <summary>
/// What the worker last read in Entra for one group name of the mapping sheet (the API has no Entra access). Found =
/// exactly one security group with that display name; <see cref="Count"/> &gt; 1 is ambiguous and never granted.
/// </summary>
public sealed record GroupResolution : IDocument
{
    /// <summary><see cref="MakeId"/> of the normalised name.</summary>
    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    public required string GroupName { get; init; }
    public string? ObjectId { get; init; }
    public int Count { get; init; }
    public required DateTimeOffset ReadAt { get; init; }
    public int SchemaVersion { get; init; } = 1;
    [JsonIgnore] public string PartitionKey => ProjectId;
    [JsonIgnore] public bool Resolved => Count == 1 && !string.IsNullOrEmpty(ObjectId);

    public static string MakeId(string normalisedName) => $"grp-{Workflow.Ids.Hash(normalisedName)}";
}

/// <summary>Offboarding run status. The four contract values, plus Planning / Executing while the worker works (decision 2).</summary>
public enum OffboardingStatus { Planning, Planned, Executing, Completed, PartiallyCompleted, Failed, Cancelled }

public enum OffboardingStepResult { Planned, Done, Skipped, Failed }

/// <summary>One step of an offboarding run (F5 output contract).</summary>
public sealed record OffboardingStep
{
    public required string Name { get; init; }
    public required string Target { get; init; }
    public required string Action { get; init; }
    public required OffboardingStepResult Result { get; init; }
    public string Detail { get; init; } = "";
}

/// <summary>
/// F5 offboarding: plan (dry run) → confirm → execute → verify → audit. <see cref="PlanHash"/> pins the plan that was
/// confirmed: execute runs only that plan. In a production tenant the confirmer must not be the requester.
/// </summary>
public sealed record OffboardingRun : IDocument
{
    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    public required string EnterpriseId { get; init; }
    public string? ObjectId { get; init; }
    public string? DisplayName { get; init; }
    /// <summary>"plan" or "execute" (the last mode requested).</summary>
    public required string Mode { get; init; }
    public required OffboardingStatus Status { get; init; }
    public IReadOnlyList<OffboardingStep> Steps { get; init; } = [];
    public IReadOnlyList<string> Leftovers { get; init; } = [];
    public IReadOnlyList<string> NotCovered { get; init; } = [];
    public bool PmoNotified { get; init; }
    public required string CorrelationId { get; init; }
    public required string RequestedBy { get; init; }
    public string? RequestedByName { get; init; }
    public DateTimeOffset RequestedAt { get; init; }
    public string? ConfirmedBy { get; init; }
    public string? ConfirmedByName { get; init; }
    public DateTimeOffset? ConfirmedAt { get; init; }
    public string? PlanHash { get; init; }
    public DateTimeOffset? PlannedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    /// <summary>Transient failures per step (step index); a step is retried on the next worker run, at most 3 times.</summary>
    public IReadOnlyDictionary<string, int> Attempts { get; init; } = new Dictionary<string, int>();
    public DateTimeOffset UpdatedAt { get; init; }
    public bool IsSample { get; init; }
    public int SchemaVersion { get; init; } = 1;
    [JsonIgnore] public string PartitionKey => ProjectId;
}

/// <summary>
/// Append-only audit trail of v1.1 actions (uploads, rollbacks, escalation decisions, TAP mails, offboarding). Details are
/// masked; a TAP mail stores only userId, the masked recipient, the time, the expiry and the correlation id.
/// </summary>
public sealed record AuditEvent : IDocument
{
    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    public required DateTimeOffset At { get; init; }
    public required string Actor { get; init; }
    public required string Action { get; init; }
    public required string Target { get; init; }
    public string? CorrelationId { get; init; }
    public string? Details { get; init; }
    /// <summary>Upload idempotency: the stored response replayed for the same Idempotency-Key.</summary>
    public string? Result { get; init; }
    public bool IsSample { get; init; }
    public int SchemaVersion { get; init; } = 1;
    [JsonIgnore] public string PartitionKey => ProjectId;

    public static string NewId(DateTimeOffset at) => $"aud-{at:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
}
