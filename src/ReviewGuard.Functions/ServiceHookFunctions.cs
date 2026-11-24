using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReviewGuard.Core;
using ReviewGuard.Core.ServiceHooks;

namespace ReviewGuard.Functions;

/// <summary>
/// Receives the Azure DevOps service hooks.
/// Two locks on the door: the Function key in the URL (AuthorizationLevel.Function)
/// and the shared secret header set in the service hook.
/// </summary>
public sealed partial class ServiceHookFunctions(
    ReviewGuardService service,
    IOptions<WebhookOptions> webhookOptions,
    ILogger<ServiceHookFunctions> logger)
{
    /// <summary>A reviewer voted. Service hook "Pull request updated", filtered to vote changes.</summary>
    [Function("PullRequestUpdated")]
    public async Task<IActionResult> PullRequestUpdated(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "hooks/pull-request-updated")] HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!HasValidSecret(request))
        {
            LogRejected(logger);
            return new UnauthorizedResult();
        }

        var evt = await ReadAsync<PullRequestUpdatedEvent>(request).ConfigureAwait(false);
        if (evt?.Resource?.Repository?.Project is null)
        {
            return new BadRequestObjectResult("Body is not a pull request event");
        }

        // Quick filter on the event itself. The service reads the full PR again.
        if (Approvals.FindApprovers(evt).Count == 0)
        {
            return new OkObjectResult("No approvals");
        }

        var pr = new PullRequestRef(evt.Resource.Repository.Project.Id, evt.Resource.Repository.Id, evt.Resource.PullRequestId);
        return await EvaluateAsync(pr, evt.Id, evt.CreatedDate, request.HttpContext.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>The line count pipeline finished. Re-check PRs that were pending.</summary>
    [Function("BuildCompleted")]
    public async Task<IActionResult> BuildCompleted(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "hooks/build-completed")] HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!HasValidSecret(request))
        {
            LogRejected(logger);
            return new UnauthorizedResult();
        }

        var evt = await ReadAsync<BuildCompletedEvent>(request).ConfigureAwait(false);
        var pr = evt?.ToPullRequestRef();
        if (evt is null || pr is null)
        {
            return new OkObjectResult("Not a pull request build");
        }

        return await EvaluateAsync(pr, evt.Id, evt.CreatedDate, request.HttpContext.RequestAborted).ConfigureAwait(false);
    }

    private async Task<IActionResult> EvaluateAsync(PullRequestRef pr, string eventId, DateTimeOffset eventTime, CancellationToken ct)
    {
        var result = await service.EvaluateAsync(pr, eventId, eventTime, ct).ConfigureAwait(false);
        if (result.Skipped || result.Status is null)
        {
            LogSkipped(logger, eventId, pr.PullRequestId);
            return new OkObjectResult("Skipped");
        }

        LogDecision(logger, eventId, pr.PullRequestId, result.Status.State, result.Status.Description);
        return new OkObjectResult(new { state = result.Status.State.ToString(), result.Status.Description });
    }

    private static async Task<T?> ReadAsync<T>(HttpRequest request)
        where T : class
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(request.Body, cancellationToken: request.HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private bool HasValidSecret(HttpRequest request)
    {
        var expected = Encoding.UTF8.GetBytes(webhookOptions.Value.WebhookSecret);
        var actual = Encoding.UTF8.GetBytes(request.Headers[WebhookOptions.SecretHeader].ToString());
        return expected.Length > 0 && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected call without a valid secret header")]
    private static partial void LogRejected(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Event {EventId}: PR {PullRequestId} skipped (duplicate, closed or no approvals)")]
    private static partial void LogSkipped(ILogger logger, string eventId, int pullRequestId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Event {EventId}: PR {PullRequestId} status {State}: {Description}")]
    private static partial void LogDecision(ILogger logger, string eventId, int pullRequestId, ReviewDepthState state, string description);
}
