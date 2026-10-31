using PmoReview.Core.Offboarding;

namespace PmoReview.Core.Http;

/// <summary>
/// F5 routes. <c>POST offboarding/{enterpriseId}?projectId=…&amp;mode=plan|execute</c> returns the F5 output contract (202 while
/// the worker plans or executes, 200 for a run already there; <c>&amp;replace=true</c> with plan replaces a plan nobody confirmed); <c>GET offboarding/{enterpriseId}?projectId=…</c> reads the latest
/// run. PMO and Operators only; the project is the tenant the person is offboarded from.
/// </summary>
public static class OffboardingEndpoints
{
    public static ApiHost MapOffboarding(this ApiHost host, OffboardingService offboarding)
    {
        host.Map("POST", "offboarding/{enterpriseId:upn}", async c =>
        {
            var (status, view) = c.QueryValue("mode") switch
            {
                OffboardingService.ModePlan => await offboarding.PlanAsync(c.Caller, c.QueryValue("projectId"), c["enterpriseId"],
                    c.QueryValue("replace") == "true", c.Request.CorrelationId, c.Ct),
                OffboardingService.ModeExecute => await offboarding.ExecuteAsync(c.Caller, c.QueryValue("projectId"), c["enterpriseId"],
                    c.Body<OffboardingExecuteInput>(), c.Request.CorrelationId, c.Ct),
                _ => throw Common.AppException.Invalid("Query parameter mode must be plan or execute."),
            };
            return new ApiResponse(status, view);
        });
        host.Map("GET", "offboarding/{enterpriseId:upn}", async c =>
            new ApiResponse(200, await offboarding.GetAsync(c.Caller, c.QueryValue("projectId"), c["enterpriseId"], c.QueryValue("offboardingId"), c.Ct)));
        return host;
    }
}
