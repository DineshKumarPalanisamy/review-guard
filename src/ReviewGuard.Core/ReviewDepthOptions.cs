namespace ReviewGuard.Core;

/// <summary>
/// Thresholds for the review-depth rule. Bound from app settings (section "ReviewGuard"),
/// so they can be tuned in the report-only phase without a deploy.
/// </summary>
public sealed class ReviewDepthOptions
{
    public const string SectionName = "ReviewGuard";

    /// <summary>PRs with fewer changed lines than this are not checked.</summary>
    public int MinLines { get; set; } = 200;

    /// <summary>Minimum review time allowed for each 100 changed lines.</summary>
    public int SecondsPer100Lines { get; set; } = 30;

    /// <summary>
    /// Lowest allowed review time for a PR that touches critical paths or has unresolved bot
    /// review threads. Without it, a 20-line change to payment code would be allowed only 6 s.
    /// </summary>
    public int MinRiskyReviewSeconds { get; set; } = 60;

    /// <summary>
    /// Display names of bot reviewers (for example a Copilot review identity). Their unresolved
    /// threads at approval time are a risk signal. Empty = signal off.
    /// </summary>
    public IReadOnlyList<string> BotReviewerNames { get; set; } = [];
}
