using System.Text.Json;
using ReviewGuard.Core;
using ReviewGuard.Core.ServiceHooks;

namespace ReviewGuard.Core.Tests;

public sealed class ReviewGuardServiceTests
{
    private static readonly PullRequestRef Pr = new("proj", "repo", 7);
    private static readonly DateTimeOffset Push = new(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeAzureDevOps ado = new();
    private readonly FakeAudit audit = new();
    private readonly FakeAlerts alerts = new();

    private ReviewGuardService Service() => new(ado, audit, alerts, new ReviewDepthOptions());

    private static PullRequestSnapshot Snapshot(
        int? lines = 450,
        string? lineCommit = "c1",
        IReadOnlyList<ApproverInfo>? approvers = null,
        IReadOnlyList<ThreadInfo>? threads = null,
        string status = "active") =>
        new(status, "c1", "Title", "https://x", lines, lineCommit, 3, Push,
            approvers ?? [new ApproverInfo("ana", "Ana", Votes.Approved)], threads ?? []);

    [Fact]
    public async Task Fast_approval_posts_failed_and_alerts_once()
    {
        ado.Snapshot = Snapshot();

        var first = await Service().EvaluateAsync(Pr, "e1", Push.AddSeconds(50), TestContext.Current.CancellationToken);
        var second = await Service().EvaluateAsync(Pr, "e2", Push.AddSeconds(500), TestContext.Current.CancellationToken);

        Assert.Equal(ReviewDepthState.Failed, first.Status!.State);
        Assert.Equal(3, first.Status.IterationId);
        // Second event keeps the first approval time, so still failed. Alert is not repeated.
        Assert.Equal(ReviewDepthState.Failed, second.Status!.State);
        Assert.Single(alerts.Sent);
        Assert.Equal(2, audit.Decisions.Count);
    }

    [Fact]
    public async Task Duplicate_event_id_is_skipped()
    {
        ado.Snapshot = Snapshot();

        await Service().EvaluateAsync(Pr, "e1", Push.AddSeconds(50), TestContext.Current.CancellationToken);
        var again = await Service().EvaluateAsync(Pr, "e1", Push.AddSeconds(50), TestContext.Current.CancellationToken);

        Assert.True(again.Skipped);
        Assert.Single(ado.Posted);
    }

    [Fact]
    public async Task Pending_until_pipeline_then_failed_with_original_approval_time()
    {
        ado.Snapshot = Snapshot(lines: null, lineCommit: null);
        var vote = await Service().EvaluateAsync(Pr, "vote", Push.AddSeconds(20), TestContext.Current.CancellationToken);

        ado.Snapshot = Snapshot();
        var build = await Service().EvaluateAsync(Pr, "build", Push.AddMinutes(10), TestContext.Current.CancellationToken);

        Assert.Equal(ReviewDepthState.Pending, vote.Status!.State);
        Assert.Equal(ReviewDepthState.Failed, build.Status!.State);
        Assert.Equal(TimeSpan.FromSeconds(20), build.Decisions[0].Decision.ReviewTime);
    }

    [Fact]
    public async Task Worst_result_across_approvers_wins()
    {
        ado.Snapshot = Snapshot(
            approvers: [new("ana", "Ana", Votes.Approved), new("ben", "Ben", Votes.ApprovedWithSuggestions)],
            threads: [new ThreadInfo("/a.cs", 10, ["ana"])]);

        var result = await Service().EvaluateAsync(Pr, "e1", Push.AddSeconds(50), TestContext.Current.CancellationToken);

        Assert.Equal(ReviewDepthState.Failed, result.Status!.State);
        Assert.StartsWith("Ben: ", result.Status.Description, StringComparison.Ordinal);
        Assert.Equal("Ben", Assert.Single(alerts.Sent).ReviewerName);
    }

    [Fact]
    public async Task No_approvers_or_closed_pr_posts_nothing()
    {
        ado.Snapshot = Snapshot(approvers: [new("ana", "Ana", 0)]);
        Assert.True((await Service().EvaluateAsync(Pr, "e1", Push, TestContext.Current.CancellationToken)).Skipped);

        ado.Snapshot = Snapshot(status: "completed");
        Assert.True((await Service().EvaluateAsync(Pr, "e2", Push, TestContext.Current.CancellationToken)).Skipped);

        Assert.Empty(ado.Posted);
    }

    [Fact]
    public void Only_inline_threads_with_the_reviewer_count()
    {
        ThreadInfo[] threads =
        [
            new("/a.cs", 10, ["ana"]),
            new("/b.cs", 3, ["ben", "ana"]), // reply in an inline thread counts
            new(null, null, ["ana"]),        // general comment ("LGTM") does not
            new("/c.cs", null, ["ana"]),     // file comment without a line does not
            new("/d.cs", 5, ["ben"]),
        ];

        Assert.Equal(2, ReviewGuardService.CountInlineComments(threads, "ana"));
    }

    [Fact]
    public void Build_completed_event_maps_to_the_pr()
    {
        const string json = """
            {
              "id": "b-evt", "eventType": "build.complete", "createdDate": "2026-11-24T10:05:00Z",
              "resource": {
                "id": 99, "reason": "pullRequest",
                "triggerInfo": { "pr.number": "7", "pr.sourceBranch": "refs/heads/x" },
                "repository": { "id": "repo", "type": "TfsGit" }
              },
              "resourceContainers": { "project": { "id": "proj" } }
            }
            """;

        var evt = JsonSerializer.Deserialize<BuildCompletedEvent>(json)!;

        Assert.Equal(Pr, evt.ToPullRequestRef());
    }

    [Fact]
    public void Ci_build_without_pr_number_maps_to_nothing()
    {
        var evt = new BuildCompletedEvent("id", "build.complete", Push,
            new BuildResource(1, "individualCI", new Dictionary<string, string>(), new BuildRepository("repo", "TfsGit"), null), null);

        Assert.Null(evt.ToPullRequestRef());
    }

    private sealed class FakeAzureDevOps : IAzureDevOpsClient
    {
        public PullRequestSnapshot Snapshot { get; set; } = null!;

        public List<PrStatus> Posted { get; } = [];

        public Task<PullRequestSnapshot> GetSnapshotAsync(PullRequestRef pr, CancellationToken ct) => Task.FromResult(Snapshot);

        public Task PostStatusAsync(PullRequestRef pr, PrStatus status, CancellationToken ct)
        {
            Posted.Add(status);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAudit : IAuditStore
    {
        private readonly HashSet<string> events = [];
        private readonly Dictionary<string, ApprovalRecord> approvals = [];

        public List<DecisionRecord> Decisions { get; } = [];

        public Task<bool> TryMarkEventAsync(string eventId, CancellationToken ct) => Task.FromResult(events.Add(eventId));

        public Task<ApprovalRecord> GetOrAddApprovalAsync(ApprovalRecord candidate, CancellationToken ct)
        {
            var key = Key(candidate);
            if (!approvals.TryGetValue(key, out var existing))
            {
                approvals[key] = existing = candidate;
            }

            return Task.FromResult(existing);
        }

        public Task MarkAlertSentAsync(ApprovalRecord record, CancellationToken ct)
        {
            approvals[Key(record)] = record with { AlertSent = true };
            return Task.CompletedTask;
        }

        public Task WriteDecisionAsync(DecisionRecord record, CancellationToken ct)
        {
            Decisions.Add(record);
            return Task.CompletedTask;
        }

        private static string Key(ApprovalRecord r) => $"{r.PullRequest.Key}/{r.ReviewerId}/{r.SourceCommitId}";
    }

    private sealed class FakeAlerts : IAlertSender
    {
        public List<AlertMessage> Sent { get; } = [];

        public Task SendAsync(AlertMessage alert, CancellationToken ct)
        {
            Sent.Add(alert);
            return Task.CompletedTask;
        }
    }
}
