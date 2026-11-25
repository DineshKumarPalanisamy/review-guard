namespace ReviewGuard.Core;

/// <summary>Which PR an event is about.</summary>
public sealed record PullRequestRef(string ProjectId, string RepositoryId, int PullRequestId)
{
    public string Key => $"{RepositoryId}_{PullRequestId}";
}

/// <summary>What we read from Azure DevOps about one PR, at the time of the event.</summary>
public sealed record PullRequestSnapshot(
    string Status,
    string SourceCommitId,
    string? Title,
    string? WebUrl,
    int? ChangedLines,
    string? LineCountCommitId,
    int LatestIterationId,
    DateTimeOffset LastPushAt,
    IReadOnlyList<ApproverInfo> Approvers,
    IReadOnlyList<ThreadInfo> Threads,
    IReadOnlyList<ReviewerAddition> ReviewerAdditions);

public sealed record ApproverInfo(string Id, string DisplayName, int Vote);

/// <summary>
/// Someone else added this reviewer to the PR at this time. From the "ReviewersUpdate" system
/// threads. Self-joins (a reviewer who votes without being asked) are not included: Azure DevOps
/// writes them at the moment of the vote, which would make every such review look like 0 s.
/// </summary>
public sealed record ReviewerAddition(string ReviewerId, DateTimeOffset AddedAt);

/// <summary>One PR comment thread. FilePath and Line are null for general comments.</summary>
public sealed record ThreadInfo(string? FilePath, int? Line, IReadOnlyList<string> CommentAuthorIds);

public sealed record PrStatus(ReviewDepthState State, string Description, int IterationId);

/// <summary>Azure DevOps REST calls. Implemented with a PAT in the Function app.</summary>
public interface IAzureDevOpsClient
{
    Task<PullRequestSnapshot> GetSnapshotAsync(PullRequestRef pr, CancellationToken ct);

    Task PostStatusAsync(PullRequestRef pr, PrStatus status, CancellationToken ct);
}

/// <summary>Stored per PR + reviewer + source commit. Keeps the first approval time across events.</summary>
public sealed record ApprovalRecord(
    PullRequestRef PullRequest,
    string ReviewerId,
    string SourceCommitId,
    DateTimeOffset FirstApprovedAt,
    bool AlertSent);

/// <summary>One audit row: what the rule decided and why.</summary>
public sealed record DecisionRecord(
    PullRequestRef PullRequest,
    string ReviewerId,
    string ReviewerName,
    string SourceCommitId,
    string EventId,
    ReviewDecision Decision,
    int? ChangedLines,
    int InlineComments);

/// <summary>Table Storage in Azure. Audit log, approval times and seen event ids.</summary>
public interface IAuditStore
{
    /// <summary>True if this event id was fully processed before (service hooks can deliver twice).</summary>
    Task<bool> IsEventSeenAsync(string eventId, CancellationToken ct);

    /// <summary>
    /// Marks the event as processed. Called only after the work is done, so a failed run is
    /// retried by Azure DevOps instead of being skipped as a duplicate.
    /// </summary>
    Task MarkEventSeenAsync(string eventId, CancellationToken ct);

    /// <summary>Returns the stored record, or stores and returns a new one with this approval time.</summary>
    Task<ApprovalRecord> GetOrAddApprovalAsync(ApprovalRecord candidate, CancellationToken ct);

    Task MarkAlertSentAsync(ApprovalRecord record, CancellationToken ct);

    Task WriteDecisionAsync(DecisionRecord record, CancellationToken ct);
}

public sealed record AlertMessage(
    PullRequestRef PullRequest,
    string? Title,
    string? WebUrl,
    string ReviewerName,
    string Description);

/// <summary>Sends the alert to the CTO (Teams Workflows webhook).</summary>
public interface IAlertSender
{
    Task SendAsync(AlertMessage alert, CancellationToken ct);
}
