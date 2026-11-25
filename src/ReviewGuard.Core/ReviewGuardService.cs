using ReviewGuard.Core.ServiceHooks;

namespace ReviewGuard.Core;

public sealed record EvaluationResult(PrStatus? Status, IReadOnlyList<DecisionRecord> Decisions, bool Skipped);

/// <summary>
/// Runs the rule for every current approver of one PR and posts one PR status.
/// Called for a vote event and again when the line count pipeline completes.
/// Stateless between calls: all state is in Azure DevOps and the audit store.
/// </summary>
public sealed class ReviewGuardService(
    IAzureDevOpsClient azureDevOps,
    IAuditStore audit,
    IAlertSender alerts,
    ReviewDepthOptions options)
{
    public async Task<EvaluationResult> EvaluateAsync(
        PullRequestRef pr, string eventId, DateTimeOffset eventTime, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pr);

        if (await audit.IsEventSeenAsync(eventId, ct).ConfigureAwait(false))
        {
            return new(null, [], Skipped: true);
        }

        // Mark the event only after all work is done. If anything below throws, the event
        // stays unmarked and the Azure DevOps retry runs it again. A repeat run is safe:
        // the approval time is stored once, and the alert has its own AlertSent flag.
        var result = await ProcessAsync(pr, eventId, eventTime, ct).ConfigureAwait(false);
        await audit.MarkEventSeenAsync(eventId, ct).ConfigureAwait(false);
        return result;
    }

    private async Task<EvaluationResult> ProcessAsync(
        PullRequestRef pr, string eventId, DateTimeOffset eventTime, CancellationToken ct)
    {
        var snapshot = await azureDevOps.GetSnapshotAsync(pr, ct).ConfigureAwait(false);
        var approvers = snapshot.Approvers.Where(a => Votes.IsApproval(a.Vote)).ToList();
        if (!string.Equals(snapshot.Status, "active", StringComparison.OrdinalIgnoreCase) || approvers.Count == 0)
        {
            // Nothing to check. The policy still needs an approval, so no status is fine.
            return new(null, [], Skipped: true);
        }

        var decisions = new List<(ApprovalRecord Record, ApproverInfo Approver, DecisionRecord Row)>();
        foreach (var approver in approvers)
        {
            var record = await audit.GetOrAddApprovalAsync(
                new ApprovalRecord(pr, approver.Id, snapshot.SourceCommitId, eventTime, AlertSent: false), ct).ConfigureAwait(false);

            var inlineComments = CountInlineComments(snapshot.Threads, approver.Id);
            var input = new ReviewInput(
                snapshot.ChangedLines,
                snapshot.LineCountCommitId,
                snapshot.SourceCommitId,
                record.FirstApprovedAt,
                snapshot.LastPushAt,
                ReviewerAddedAt(snapshot.ReviewerAdditions, approver.Id, record.FirstApprovedAt),
                inlineComments);

            var decision = ReviewDepthRule.Evaluate(input, options);
            var row = new DecisionRecord(pr, approver.Id, approver.DisplayName, snapshot.SourceCommitId, eventId, decision, snapshot.ChangedLines, inlineComments);
            await audit.WriteDecisionAsync(row, ct).ConfigureAwait(false);
            decisions.Add((record, approver, row));
        }

        // One status per PR: the worst result across approvers wins.
        var worst = decisions.OrderBy(d => Severity(d.Row.Decision.State)).First();
        var description = decisions.Count == 1
            ? worst.Row.Decision.Description
            : $"{worst.Approver.DisplayName}: {worst.Row.Decision.Description}";
        var status = new PrStatus(worst.Row.Decision.State, description, snapshot.LatestIterationId);
        await azureDevOps.PostStatusAsync(pr, status, ct).ConfigureAwait(false);

        foreach (var (record, approver, row) in decisions)
        {
            if (row.Decision.Alert && !record.AlertSent)
            {
                await alerts.SendAsync(new AlertMessage(pr, snapshot.Title, snapshot.WebUrl, approver.DisplayName, row.Decision.Description), ct).ConfigureAwait(false);
                await audit.MarkAlertSentAsync(record, ct).ConfigureAwait(false);
            }
        }

        return new(status, [.. decisions.Select(d => d.Row)], Skipped: false);
    }

    /// <summary>Comments by this reviewer in threads that point at a file and a line.</summary>
    public static int CountInlineComments(IEnumerable<ThreadInfo> threads, string reviewerId) =>
        threads.Count(t => t.FilePath is not null && t.Line is not null
            && t.CommentAuthorIds.Contains(reviewerId, StringComparer.OrdinalIgnoreCase));

    /// <summary>The last time someone else added this reviewer, before the approval. Null if never.</summary>
    public static DateTimeOffset? ReviewerAddedAt(
        IEnumerable<ReviewerAddition> additions, string reviewerId, DateTimeOffset approvedAt) =>
        additions
            .Where(a => string.Equals(a.ReviewerId, reviewerId, StringComparison.OrdinalIgnoreCase) && a.AddedAt <= approvedAt)
            .Select(a => (DateTimeOffset?)a.AddedAt)
            .Max();

    private static int Severity(ReviewDepthState state) => state switch
    {
        ReviewDepthState.Failed => 0,
        ReviewDepthState.Pending => 1,
        _ => 2,
    };
}
