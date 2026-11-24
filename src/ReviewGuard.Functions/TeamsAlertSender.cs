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
        LogAlert(logger, alert.PullRequest.PullRequestId, alert.ReviewerName, alert.Description);

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
                            new { type = "TextBlock", text = "Review Guard: fast approval", weight = "Bolder", size = "Medium" },
                            new { type = "TextBlock", text = $"PR {alert.PullRequest.PullRequestId}: {alert.Title}", wrap = true },
                            new { type = "TextBlock", text = $"{alert.ReviewerName}: {alert.Description}", wrap = true },
                        },
                        actions = new[] { new { type = "Action.OpenUrl", title = "Open PR", url = alert.WebUrl } },
                    },
                },
            },
        };

        using var response = await http.PostAsJsonAsync(options.Value.TeamsWebhookUrl, card, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "ALERT PR {PullRequestId}: {ReviewerName}: {Description}")]
    private static partial void LogAlert(ILogger logger, int pullRequestId, string reviewerName, string description);
}

public sealed class AlertOptions
{
    public const string SectionName = "ReviewGuard";

    /// <summary>Optional. Empty means log only.</summary>
    public string TeamsWebhookUrl { get; set; } = "";
}
