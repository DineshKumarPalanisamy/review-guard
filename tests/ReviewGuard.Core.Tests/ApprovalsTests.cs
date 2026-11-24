using System.Text.Json;
using ReviewGuard.Core.ServiceHooks;

namespace ReviewGuard.Core.Tests;

public sealed class ApprovalsTests
{
    // Trimmed shape of a real "git.pullrequest.updated" payload. Extra fields must be ignored.
    private const string Payload = """
        {
          "subscriptionId": "00000000-0000-0000-0000-000000000000",
          "id": "evt-1",
          "eventType": "git.pullrequest.updated",
          "createdDate": "2026-11-24T10:00:50Z",
          "message": { "text": "Ana approved pull request 7" },
          "resource": {
            "pullRequestId": 7,
            "status": "active",
            "repository": { "id": "repo-1", "project": { "id": "proj-1" } },
            "lastMergeSourceCommit": { "commitId": "abc123" },
            "reviewers": [
              { "id": "u1", "displayName": "Ana",  "vote": 10, "isContainer": false },
              { "id": "u2", "displayName": "Ben",  "vote": 5 },
              { "id": "u3", "displayName": "Cara", "vote": 0 },
              { "id": "u4", "displayName": "Dev",  "vote": -5 },
              { "id": "g1", "displayName": "[Proj]\\Team", "vote": 10, "isContainer": true }
            ]
          }
        }
        """;

    private static PullRequestUpdatedEvent Parse(string json) =>
        JsonSerializer.Deserialize<PullRequestUpdatedEvent>(json)!;

    [Fact]
    public void Parses_the_fields_we_use()
    {
        var evt = Parse(Payload);

        Assert.Equal("evt-1", evt.Id);
        Assert.Equal(new DateTimeOffset(2026, 11, 24, 10, 0, 50, TimeSpan.Zero), evt.CreatedDate);
        Assert.Equal(7, evt.Resource!.PullRequestId);
        Assert.Equal("proj-1", evt.Resource.Repository!.Project!.Id);
        Assert.Equal("abc123", evt.Resource.LastMergeSourceCommit!.CommitId);
    }

    [Fact]
    public void Vote_10_and_vote_5_are_approvals_groups_are_skipped()
    {
        var approvers = Approvals.FindApprovers(Parse(Payload));

        Assert.Equal(["u1", "u2"], approvers.Select(a => a.Id));
    }

    [Fact]
    public void Other_event_types_give_no_approvers()
    {
        var evt = Parse(Payload) with { EventType = "git.pullrequest.created" };

        Assert.Empty(Approvals.FindApprovers(evt));
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("abandoned")]
    public void Closed_prs_give_no_approvers(string status)
    {
        var evt = Parse(Payload);
        evt = evt with { Resource = evt.Resource! with { Status = status } };

        Assert.Empty(Approvals.FindApprovers(evt));
    }

    [Theory]
    [InlineData(10, true)]
    [InlineData(5, true)]
    [InlineData(0, false)]
    [InlineData(-5, false)]
    [InlineData(-10, false)]
    public void IsApproval(int vote, bool expected) => Assert.Equal(expected, Votes.IsApproval(vote));
}
