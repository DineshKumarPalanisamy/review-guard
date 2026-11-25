using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReviewGuard.Core;

namespace ReviewGuard.Functions;

/// <summary>
/// Posts an Adaptive Card to a Teams Workflows webhook ("When a Teams webhook request is received").
/// If no webhook URL is set, it only writes a warning log. That is the report-only setup.
/// </summary>
public sealed partial class TeamsAlertSender(
    HttpClient http,
    IOptions<AlertOptions> options,
    ILogger<TeamsAlertSender> logger) : IAlertSender
{
    public async Task SendAsync(AlertMessage alert, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(alert);
        // One line, so the Azure Monitor alert rule ("ALERT PR" prefix) puts all reasons in the email.
        var detail = alert.Reasons is { Count: > 0 } r ? string.Join("; ", r) : alert.Description;
        LogAlert(logger, alert.PullRequest.PullRequestId, alert.ReviewerName, detail);

        if (string.IsNullOrWhiteSpace(options.Value.TeamsWebhookUrl))
        {
            return;
        }

        var card = new
        {
            type = "message",
            attachments = new[]
            {
                new
                {
                    contentType = "application/vnd.microsoft.card.adaptive",
                    content = new
                    {
                        type = "AdaptiveCard",
                        version = "1.4",
                        body = new object[]
                        {
                            new { type = "TextBlock", text = "Review Guard: no evidence of review", weight = "Bolder", size = "Medium" },
                            new { type = "TextBlock", text = $"PR {alert.PullRequest.PullRequestId}: {alert.Title}", wrap = true },
                            new { type = "TextBlock", text = $"Approved by {alert.ReviewerName}", wrap = true },
                            new { type = "TextBlock", text = ReasonList(alert), wrap = true },
                        },
                        actions = new[] { new { type = "Action.OpenUrl", title = "Open PR", url = alert.WebUrl } },
                    },
                },
            },
        };

        using var response = await http.PostAsJsonAsync(options.Value.TeamsWebhookUrl, card, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Markdown bullet list of the reasons. Falls back to the one-line description.</summary>
    private static string ReasonList(AlertMessage alert) =>
        alert.Reasons is { Count: > 0 } reasons
            ? string.Join("\n", reasons.Select(r => "- " + r))
            : alert.Description;

    [LoggerMessage(Level = LogLevel.Warning, Message = "ALERT PR {PullRequestId}: {ReviewerName}: {Description}")]
    private static partial void LogAlert(ILogger logger, int pullRequestId, string reviewerName, string description);
}

public sealed class AlertOptions
{
    public const string SectionName = "ReviewGuard";

    /// <summary>Optional. Empty means log only.</summary>
    public string TeamsWebhookUrl { get; set; } = "";
}
