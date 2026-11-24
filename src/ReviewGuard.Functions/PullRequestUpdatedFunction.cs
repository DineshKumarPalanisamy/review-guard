using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReviewGuard.Core.ServiceHooks;

namespace ReviewGuard.Functions;

/// <summary>
/// Receives the Azure DevOps service hook "Pull request updated".
/// Two locks on the door: the Function key in the URL (AuthorizationLevel.Function)
/// and the shared secret header set in the service hook.
/// </summary>
public sealed partial class PullRequestUpdatedFunction(
    IOptions<WebhookOptions> webhookOptions,
    ILogger<PullRequestUpdatedFunction> logger)
{
    [Function("PullRequestUpdated")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "hooks/pull-request-updated")] HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!HasValidSecret(request))
        {
            LogRejected(logger);
            return new UnauthorizedResult();
        }

        PullRequestUpdatedEvent? evt;
        try
        {
            evt = await JsonSerializer.DeserializeAsync<PullRequestUpdatedEvent>(request.Body, cancellationToken: request.HttpContext.RequestAborted);
        }
        catch (JsonException)
        {
            return new BadRequestObjectResult("Body is not a service hook event");
        }

        if (evt?.Resource is null)
        {
            return new BadRequestObjectResult("Body is not a service hook event");
        }

        var approvers = Approvals.FindApprovers(evt);
        foreach (var approver in approvers)
        {
            // Next step: read line count, iterations and threads from Azure DevOps,
            // run ReviewDepthRule, write the audit row, post the PR status, alert.
            LogApproval(logger, evt.Id, evt.Resource.PullRequestId, approver.Id, approver.Vote);
        }

        return new AcceptedResult();
    }

    private bool HasValidSecret(HttpRequest request)
    {
        var expected = Encoding.UTF8.GetBytes(webhookOptions.Value.WebhookSecret);
        var actual = Encoding.UTF8.GetBytes(request.Headers[WebhookOptions.SecretHeader].ToString());
        return expected.Length > 0 && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected call without a valid secret header")]
    private static partial void LogRejected(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Event {EventId}: PR {PullRequestId} approved by {ReviewerId} with vote {Vote}")]
    private static partial void LogApproval(ILogger logger, string eventId, int pullRequestId, string reviewerId, int vote);
}
