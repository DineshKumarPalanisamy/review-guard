namespace ReviewGuard.Core.ServiceHooks;

/// <summary>Azure DevOps reviewer vote values.</summary>
public static class Votes
{
    public const int Approved = 10;
    public const int ApprovedWithSuggestions = 5;

    /// <summary>Vote 10 and vote 5 both let the PR merge, so both are checked (DESIGN.md section 12).</summary>
    public static bool IsApproval(int vote) => vote is Approved or ApprovedWithSuggestions;
}

public static class Approvals
{
    public const string PullRequestUpdatedEventType = "git.pullrequest.updated";

    /// <summary>
    /// Returns the individual reviewers who currently approve the PR.
    /// The event carries every reviewer's current vote, not which vote changed.
    /// Group reviewers (isContainer) are skipped: a person voted, the group only inherits it.
    /// </summary>
    public static IReadOnlyList<Reviewer> FindApprovers(PullRequestUpdatedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (!string.Equals(evt.EventType, PullRequestUpdatedEventType, StringComparison.Ordinal)
            || evt.Resource is not { Status: "active", Reviewers: { } reviewers })
        {
            return [];
        }

        return [.. reviewers.Where(r => !r.IsContainer && Votes.IsApproval(r.Vote))];
    }
}
