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
}
