using System.Globalization;
using HealthMonitor.Api.AiQueries;
using HealthMonitor.Api.Composition;
using HealthMonitor.Api.Contracts.V1.AiQueries;
using HealthMonitor.Application.AiQueries;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;

namespace HealthMonitor.Api.Controllers;

/// <summary>Translates one explicit submission without querying storage or applying the result.</summary>
/// <param name="translate">Application's single-call translation boundary.</param>
/// <remarks>Production discovery currently removes this controller until transport and admission controls are ready.</remarks>
[ApiController]
[Route("api/v1/ai/device-queries")]
[EnableCors(AiQueryApiComposition.CorsPolicyName)]
[ServiceFilter(typeof(AiQueryBodyLimitFilter))]
public sealed class AiDeviceQueriesController(TranslateDeviceQuery translate) : ControllerBase
{
    /// <summary>Returns a validated filter proposal or a contextual safe input, admission, output, or provider failure.</summary>
    /// <param name="request">Strictly inspected prompt shape.</param>
    /// <param name="token">Request cancellation forwarded through Application to the generator.</param>
    /// <returns>A server-owned V1 decision with no provider reason or narrative fields.</returns>
    [HttpPost]
    [RequestSizeLimit(AiQueryBodyLimitFilter.MaximumBytes)]
    [ProducesResponseType<AiDeviceQueryResponseDto>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(413)]
    [ProducesResponseType<ProblemDetails>(415)]
    [ProducesResponseType<ProblemDetails>(429)]
    [ProducesResponseType<ProblemDetails>(502)]
    [ProducesResponseType<ProblemDetails>(503)]
    [ProducesResponseType<ProblemDetails>(504)]
    [ProducesResponseType<ProblemDetails>(500)]
    public async Task<IActionResult> Translate([FromBody] AiDeviceQueryRequestDto request, CancellationToken token)
    {
        QueryTranslationResult result;
        try { result = await translate.TranslateAsync(request.Prompt, token); }
        catch (InvalidAiPromptException)
        { return AiQueryProblems.Result(HttpContext, 400, "ai_prompt_invalid", "The prompt must contain 1-500 characters after trimming."); }
        catch (InvalidStructuredQueryException)
        { return AiQueryProblems.Result(HttpContext, 502, "ai_output_invalid", "The generated query proposal is invalid."); }
        catch (AiAdmissionRejectedException rejected)
        {
            // Only validated local metadata supplies Retry-After; no provider header or diagnostic is forwarded.
            if (rejected.RetryAfterSeconds is { } seconds)
                Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
            return rejected.Reason switch
            {
                AiAdmissionRejectedException.Rejection.RateLimited => AiQueryProblems.Result(HttpContext, 429,
                    "ai_rate_limited", "The AI translation request limit was reached."),
                AiAdmissionRejectedException.Rejection.BudgetExhausted => AiQueryProblems.Result(HttpContext, 429,
                    "ai_budget_exhausted", "The daily AI translation budget is exhausted."),
                AiAdmissionRejectedException.Rejection.AdmissionUnavailable => AiQueryProblems.Result(HttpContext, 503,
                    "ai_admission_unavailable", "AI admission is unavailable."),
                AiAdmissionRejectedException.Rejection.BudgetUnavailable => AiQueryProblems.Result(HttpContext, 503,
                    "ai_budget_unavailable", "The AI usage budget is unavailable."),
                _ => throw new InvalidOperationException("Unsupported AI admission rejection.")
            };
        }
        // Only owned provider classifications select these statuses; caller cancellation and unexpected faults retain their existing paths.
        catch (AiProviderUnavailableException)
        { return AiQueryProblems.Result(HttpContext, 503, "ai_provider_unavailable", "The AI provider is unavailable."); }
        catch (AiProviderTimeoutException)
        { return AiQueryProblems.Result(HttpContext, 504, "ai_provider_timeout", "The AI provider request timed out."); }
        var query = result.Query;
        return Ok(new AiDeviceQueryResponseDto(result.Outcome,
            query is null ? null : new(query.Q, query.Site, query.Type, query.Status, query.Sort!, query.Direction!), result.Message));
    }
}
