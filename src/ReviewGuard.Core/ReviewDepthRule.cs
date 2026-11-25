using System.Globalization;

namespace ReviewGuard.Core;

/// <summary>Status state posted to the PR. Matches the Azure DevOps GitStatusState names.</summary>
public enum ReviewDepthState
{
    Succeeded,
    Failed,
    Pending,
}

/// <summary>Everything the rule needs about one approval. Collected from the event and the PR APIs.</summary>
/// <param name="ChangedLines">Line count the validation pipeline wrote on the PR. Null if not written yet.</param>
/// <param name="LineCountCommitId">Source commit the line count was computed for.</param>
/// <param name="SourceCommitId">Current source commit of the PR (lastMergeSourceCommit).</param>
/// <param name="ApprovedAt">createdDate of the service hook event. Azure DevOps stores no vote time.</param>
/// <param name="LastPushAt">createdDate of the latest PR iteration.</param>
/// <param name="ReviewerAddedAt">When the reviewer was added, if known.</param>
/// <param name="InlineCommentCount">Threads by this reviewer that have a file path and a line.</param>
/// <param name="CriticalPaths">Changed paths that match the critical path list. Empty if none.</param>
/// <param name="UnresolvedBotThreads">Bot review threads (for example Copilot) still open at approval.</param>
public sealed record ReviewInput(
    int? ChangedLines,
    string? LineCountCommitId,
    string SourceCommitId,
    DateTimeOffset ApprovedAt,
    DateTimeOffset LastPushAt,
    DateTimeOffset? ReviewerAddedAt,
    int InlineCommentCount,
    IReadOnlyList<string>? CriticalPaths = null,
    int UnresolvedBotThreads = 0);

/// <param name="Alert">True when the CTO must get an alert.</param>
/// <param name="Reasons">One line per signal, for the alert and the audit log.</param>
public sealed record ReviewDecision(
    ReviewDepthState State,
    string Description,
    bool Alert,
    TimeSpan? ReviewTime = null,
    TimeSpan? AllowedTime = null,
    IReadOnlyList<string>? Reasons = null);

/// <summary>
/// The evidence rule (ROADMAP.md, Phase 2). Pure function: no I/O, no clock.
/// A PR is risky when it is large, touches critical paths, or has unresolved bot review threads.
/// A risky PR fails when it was approved fast AND the reviewer left no inline comments.
/// </summary>
public static class ReviewDepthRule
{
    public static ReviewDecision Evaluate(ReviewInput input, ReviewDepthOptions options)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(options);

        if (input.ChangedLines is not { } lines)
        {
            return new(ReviewDepthState.Pending, "Waiting for the line count from the validation pipeline", Alert: false);
        }

        if (!string.Equals(input.LineCountCommitId, input.SourceCommitId, StringComparison.OrdinalIgnoreCase))
        {
            return new(ReviewDepthState.Pending, "Line count is for an older commit, waiting for the pipeline", Alert: false);
        }

        var critical = input.CriticalPaths ?? [];
        var large = lines >= options.MinLines;
        var hasBotThreads = input.UnresolvedBotThreads > 0;
        if (!large && critical.Count == 0 && !hasBotThreads)
        {
            return new(ReviewDepthState.Succeeded, Invariant($"Small PR ({lines} lines), not checked"), Alert: false);
        }

        var clockStart = input.ReviewerAddedAt is { } added && added > input.LastPushAt ? added : input.LastPushAt;
        var reviewTime = input.ApprovedAt - clockStart;
        if (reviewTime < TimeSpan.Zero)
        {
            // Clock skew between the event and the iteration. Treat as an instant approval.
            reviewTime = TimeSpan.Zero;
        }

        var allowedSeconds = lines * options.SecondsPer100Lines / 100.0;
        if (critical.Count > 0 || hasBotThreads)
        {
            allowedSeconds = Math.Max(allowedSeconds, options.MinRiskyReviewSeconds);
        }

        var allowedTime = TimeSpan.FromSeconds(allowedSeconds);
        var seconds = (int)reviewTime.TotalSeconds;
        var risk = RiskText(critical.Count, input.UnresolvedBotThreads);

        if (reviewTime > allowedTime)
        {
            return new(ReviewDepthState.Succeeded, Invariant($"{lines} lines{risk} reviewed in {seconds} s"), Alert: false, reviewTime, allowedTime);
        }

        if (input.InlineCommentCount > 0)
        {
            // Inline comments count as evidence. Later: LLM judge of the comments.
            return new(
                ReviewDepthState.Succeeded,
                Invariant($"{lines} lines{risk} approved in {seconds} s, {input.InlineCommentCount} inline comments"),
                Alert: false,
                reviewTime,
                allowedTime);
        }

        var reasons = new List<string>();
        if (large)
        {
            reasons.Add(Invariant($"Large PR: {lines} changed lines (limit {options.MinLines})"));
        }

        if (critical.Count > 0)
        {
            var shown = string.Join(", ", critical.Take(3));
            var more = critical.Count > 3 ? Invariant($" and {critical.Count - 3} more") : "";
            reasons.Add($"Touches critical paths: {shown}{more}");
        }

        if (hasBotThreads)
        {
            reasons.Add(Invariant($"{input.UnresolvedBotThreads} unresolved bot review threads at approval"));
        }

        reasons.Add(Invariant($"Approved in {seconds} s (allowed {(int)allowedSeconds} s)"));
        reasons.Add("No inline comments by the reviewer");

        return new(
            ReviewDepthState.Failed,
            Invariant($"{lines} lines{risk} approved in {seconds} s, no inline comments"),
            Alert: true,
            reviewTime,
            allowedTime,
            reasons);
    }

    /// <summary>" (2 critical files, 1 open bot thread)" or "" when neither.</summary>
    private static string RiskText(int criticalFiles, int botThreads)
    {
        var parts = new List<string>();
        if (criticalFiles > 0)
        {
            parts.Add(Invariant($"{criticalFiles} critical {(criticalFiles == 1 ? "file" : "files")}"));
        }

        if (botThreads > 0)
        {
            parts.Add(Invariant($"{botThreads} open bot {(botThreads == 1 ? "thread" : "threads")}"));
        }

        return parts.Count == 0 ? "" : $" ({string.Join(", ", parts)})";
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
