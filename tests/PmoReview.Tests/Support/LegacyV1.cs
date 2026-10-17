namespace PmoReview.Tests.Support;

/// <summary>
/// v1 / Phase 2 documents as they are still stored in the live account's shared containers (17 Oct 2026: 7 requests and
/// their review runs), incl. Cosmos system fields. The run shape was written by the v1 code to the Cosmos emulator. SAMPLE data.
/// </summary>
public static class LegacyV1
{
    public const string RequestId = "sample-legacy-0001";

    public const string Request = """
        {"id":"sample-legacy-0001","projectId":"SAMPLE-ALPHA","requestType":"NewJoiner","role":"SAMPLE Data Engineer","environment":"Dev",
         "manager":"SAMPLE Manager One","justification":"SAMPLE","startDate":"2026-10-17","endDate":"2026-10-29",
         "joinerUpn":"sample.joiner3@sample.invalid","createdBy":"sample-user-alice","createdAt":"2026-10-16T19:00:00+00:00",
         "updatedBy":"sample-user-alice","updatedAt":"2026-10-16T19:20:26+00:00","revision":1,"schemaVersion":1,
         "_rid":"x","_self":"x","_etag":"\"x\"","_attachments":"x","_ts":1760642426}
        """;

    public const string ReviewRunId = "sample-legacy-0001.r1.a0";

    /// <summary>v1 result shape (readiness, mappingAssessment): the v2 review result cannot read it.</summary>
    public const string ReviewRun = """
        {"id":"sample-legacy-0001.r1.a0","projectId":"SAMPLE-ALPHA","requestId":"sample-legacy-0001",
         "snapshot":{"requestId":"sample-legacy-0001","projectId":"SAMPLE-ALPHA","revision":1,"requestType":"NewJoiner","role":"SAMPLE Data Engineer",
           "environment":"Dev","justification":"SAMPLE","joinerUpn":"sample.joiner1@sample.invalid","capturedAt":"2026-10-16T19:00:00+00:00"},
         "snapshotHash":"x","requestRevision":1,"initiatedBy":"sample-user-alice","processingStatus":"Completed","readiness":"ReadyForHumanReview",
         "ruleVersions":[{"id":"A-REQ-ROLE","version":1}],"mappingVersions":[{"id":"A-MAP-ENG-DEV","version":1}],
         "reviewerProvider":"RuleOnly","reviewerVersion":"1.0.0","promptVersion":null,
         "result":{"schemaVersion":"1.0","reviewRunId":"sample-legacy-0001.r1.a0","requestRevision":1,"readiness":"ReadyForHumanReview",
           "summary":"Ready for human review (not an approval).","findings":[],
           "mappingAssessment":{"status":"Matched","mappingId":"A-MAP-ENG-DEV","mappingVersion":1,"explanation":"SAMPLE"},
           "clarificationQuestions":[],"humanReviewRequired":true,"limitations":[]},
         "correlationId":"x","queuedAt":"2026-10-16T19:00:01+00:00","completedAt":"2026-10-16T19:00:02+00:00",
         "idempotencyKey":"sample-legacy-0001:1:x","schemaVersion":1,"_rid":"x","_etag":"x","_ts":1760642426}
        """;
}
