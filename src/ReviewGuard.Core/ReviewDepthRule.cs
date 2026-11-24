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
public sealed record ReviewInput(
    int? ChangedLines,
    string? LineCountCommitId,
    string SourceCommitId,
    DateTimeOffset ApprovedAt,
    DateTimeOffset LastPushAt,
    DateTimeOffset? ReviewerAddedAt,
    int InlineCommentCount);

/// <param name="Alert">True when the CTO must get an alert.</param>
public sealed record ReviewDecision(
    ReviewDepthState State,
    string Description,
    bool Alert,
    TimeSpan? ReviewTime = null,
    TimeSpan? AllowedTime = null);

/// <summary>
/// The version 1 rule from docs/DESIGN.md section 6. Pure function: no I/O, no clock.
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

        if (lines < options.MinLines)
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

        var allowedTime = TimeSpan.FromSeconds(lines * options.SecondsPer100Lines / 100.0);
        var seconds = (int)reviewTime.TotalSeconds;

        if (reviewTime > allowedTime)
        {
            return new(ReviewDepthState.Succeeded, Invariant($"{lines} lines reviewed in {seconds} s"), Alert: false, reviewTime, allowedTime);
        }

        if (input.InlineCommentCount > 0)
        {
            // Version 1 trusts inline comments. Later: LLM judge of the comments.
            return new(
                ReviewDepthState.Succeeded,
                Invariant($"{lines} lines approved in {seconds} s, {input.InlineCommentCount} inline comments"),
                Alert: false,
                reviewTime,
                allowedTime);
        }

        return new(
            ReviewDepthState.Failed,
            Invariant($"{lines} lines approved in {seconds} s, no inline comments"),
            Alert: true,
            reviewTime,
            allowedTime);
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
