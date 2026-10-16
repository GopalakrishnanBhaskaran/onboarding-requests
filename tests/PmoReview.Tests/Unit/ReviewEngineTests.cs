using PmoReview.Core.Domain;
using PmoReview.Core.Review;
using PmoReview.Tests.Support;
using Xunit;

namespace PmoReview.Tests.Unit;

/// <summary>Deterministic checks and host-computed readiness with the active RuleOnly reviewer.</summary>
public class ReviewEngineTests
{
    [Fact]
    public async Task Complete_request_is_ready_for_human_review_with_pinned_versions()
    {
        var o = await EngineHarness.Run(EngineHarness.Complete());

        Assert.Equal(Readiness.ReadyForHumanReview, o.Result.Readiness);
        Assert.True(o.Result.HumanReviewRequired);
        Assert.DoesNotContain(o.Result.Findings, f => f.Severity == FindingSeverity.Blocking);
        Assert.Equal(MappingStatus.Matched, o.Result.MappingAssessment.Status);
        Assert.Equal("A-MAP-ENG-DEV", o.Result.MappingAssessment.MappingId);
        Assert.Contains(new PinnedVersion("A-REQ-ROLE", 1), o.RuleVersions);
        Assert.Contains(new PinnedVersion("A-MAP-ENG-DEV", 1), o.MappingVersions);
        Assert.Equal("RuleOnly", o.Reviewer.Provider);
        Assert.Null(o.Reviewer.PromptVersion);
        Assert.Contains(ResultAssembler.BaseLimitation, o.Result.Limitations);
        Assert.StartsWith("Ready for human review (not an approval)", o.Result.Summary);
    }

    [Fact]
    public async Task Missing_mandatory_field_cites_rule_and_asks_question()
    {
        var o = await EngineHarness.Run(EngineHarness.Complete(s => s with { Manager = null }));

        Assert.Equal(Readiness.NeedsInformation, o.Result.Readiness);
        var f = Assert.Single(o.Result.Findings, f => f.Code == FindingCodes.MissingField);
        Assert.Equal("manager", f.Field);
        Assert.Equal("A-REQ-MGR", f.RuleId);
        Assert.Equal(1, f.RuleVersion);
        Assert.Equal("SAMPLE-POLICY-ALPHA-01 s2.3", f.SourceReference);
        Assert.Contains(o.Result.ClarificationQuestions, q => q.Contains("line manager"));
    }

    [Theory]
    [InlineData("requestType")]
    [InlineData("role")]
    [InlineData("environment")]
    [InlineData("startDate")]
    [InlineData("manager")]
    [InlineData("justification")]
    [InlineData("endDate")]
    public async Task Missing_critical_field_is_never_ready(string field)
    {
        var s = EngineHarness.Complete(x => field switch
        {
            "requestType" => x with { RequestType = null },
            "role" => x with { Role = null },
            "environment" => x with { Environment = null },
            "startDate" => x with { StartDate = null },
            "manager" => x with { Manager = null },
            "justification" => x with { Justification = null },
            _ => x with { EndDate = null },
        });
        var o = await EngineHarness.Run(s);
        Assert.NotEqual(Readiness.ReadyForHumanReview, o.Result.Readiness);
        Assert.Contains(o.Result.Findings, f => f.Code == FindingCodes.MissingField && f.Field == field);
    }

    [Fact]
    public async Task Built_in_critical_fields_apply_even_without_a_project_rule()
    {
        // SAMPLE-BETA has no published rule for manager/justification, but environment is built-in critical.
        var s = EngineHarness.Complete(x => x with { ProjectId = TestApp.Beta, Role = "SAMPLE Tester", Environment = null });
        var o = await EngineHarness.Run(s);
        Assert.NotEqual(Readiness.ReadyForHumanReview, o.Result.Readiness);
    }

    [Fact]
    public async Task End_before_start_is_invalid()
    {
        var o = await EngineHarness.Run(EngineHarness.Complete(s => s with { EndDate = new DateOnly(2026, 10, 1) }));
        Assert.Equal(Readiness.NeedsInformation, o.Result.Readiness);
        Assert.Contains(o.Result.Findings, f => f.Code == FindingCodes.InvalidDates);
    }

    [Fact]
    public async Task Duration_rule_not_met_is_cited()
    {
        var o = await EngineHarness.Run(EngineHarness.Complete(s => s with { EndDate = new DateOnly(2027, 8, 1) }));
        var f = Assert.Single(o.Result.Findings, f => f.Code == FindingCodes.RuleNotMet);
        Assert.Equal("A-DURATION", f.RuleId);
        Assert.Equal(Readiness.NeedsInformation, o.Result.Readiness);
    }

    [Fact]
    public async Task Unsupported_project_needs_manual_review()
    {
        var o = await EngineHarness.Run(EngineHarness.Complete(s => s with { ProjectId = "SAMPLE-GAMMA" }));
        Assert.Equal(Readiness.ManualReviewRequired, o.Result.Readiness);
        Assert.Contains(o.Result.Findings, f => f.Code == FindingCodes.UnsupportedProject);
        Assert.Equal(MappingStatus.NotApplicable, o.Result.MappingAssessment.Status);
    }

    [Fact]
    public async Task Unsupported_environment_is_blocking()
    {
        var o = await EngineHarness.Run(EngineHarness.Complete(s => s with { Environment = "Sandbox" }));
        Assert.NotEqual(Readiness.ReadyForHumanReview, o.Result.Readiness);
        Assert.Contains(o.Result.Findings, f => f.Code == FindingCodes.UnsupportedEnvironment);
    }

    [Fact]
    public async Task Unknown_role_needs_information()
    {
        var o = await EngineHarness.Run(EngineHarness.Complete(s => s with { Role = "SAMPLE Astronaut" }));
        Assert.Equal(Readiness.NeedsInformation, o.Result.Readiness);
        Assert.Equal(MappingStatus.NotFound, o.Result.MappingAssessment.Status);
        Assert.Contains(o.Result.Findings, f => f.Code == FindingCodes.UnknownRole);
        Assert.Contains(o.Result.ClarificationQuestions, q => q.Contains("approved business role"));
    }

    [Fact]
    public async Task Conflicting_mappings_need_manual_review()
    {
        var o = await EngineHarness.Run(EngineHarness.Complete(s => s with { Environment = "Prod" }));
        Assert.Equal(Readiness.ManualReviewRequired, o.Result.Readiness);
        Assert.Equal(MappingStatus.Ambiguous, o.Result.MappingAssessment.Status);
        Assert.Contains(o.Result.Findings, f => f.Code == FindingCodes.AmbiguousRole);
    }

    [Fact]
    public async Task Expired_mapping_is_not_used()
    {
        var o = await EngineHarness.Run(EngineHarness.Complete(s => s with { Role = "SAMPLE Analyst" }));
        Assert.Equal(MappingStatus.NotFound, o.Result.MappingAssessment.Status);
        Assert.DoesNotContain(o.MappingVersions, p => p.Id == "A-MAP-ANALYST-DEV");
    }

    [Fact]
    public async Task Expired_or_draft_requirement_is_not_used()
    {
        // RoleChange has one expired and one draft rule only.
        var o = await EngineHarness.Run(EngineHarness.Complete(s => s with { RequestType = "RoleChange" }));
        Assert.Equal(Readiness.ManualReviewRequired, o.Result.Readiness);
        Assert.Contains(o.Result.Findings, f => f.Code == FindingCodes.NoPublishedRequirement);
        Assert.DoesNotContain(o.RuleVersions, p => p.Id is "A-RC-OLD" or "A-RC-NEW");
    }

    [Fact]
    public async Task Result_always_passes_schema()
    {
        foreach (var s in new[]
                 {
                     EngineHarness.Complete(),
                     EngineHarness.Complete(x => x with { Manager = null, Role = null, StartDate = null }),
                     EngineHarness.Complete(x => x with { ProjectId = "SAMPLE-GAMMA" }),
                     EngineHarness.Complete(x => x with { Environment = "Prod" }),
                 })
        {
            var o = await EngineHarness.Run(s);
            Assert.Empty(ResultSchema.Validate(PmoReview.Core.Common.AppJson.Serialize(o.Result)));
        }
    }
}
