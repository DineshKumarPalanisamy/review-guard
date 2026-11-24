using ReviewGuard.Core;

namespace ReviewGuard.Core.Tests;

public sealed class ReviewDepthRuleTests
{
    private const string Commit = "abc123";
    private static readonly DateTimeOffset Push = new(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);
    private static readonly ReviewDepthOptions Options = new(); // 200 lines, 30 s per 100 lines

    private static ReviewInput Input(
        int? lines = 450,
        int secondsAfterPush = 50,
        int comments = 0,
        string? lineCountCommit = Commit,
        DateTimeOffset? reviewerAdded = null) =>
        new(lines, lineCountCommit, Commit, Push.AddSeconds(secondsAfterPush), Push, reviewerAdded, comments);

    [Fact]
    public void Large_fast_approval_without_inline_comments_fails_and_alerts()
    {
        var result = ReviewDepthRule.Evaluate(Input(), Options);

        Assert.Equal(ReviewDepthState.Failed, result.State);
        Assert.True(result.Alert);
        Assert.Equal("450 lines approved in 50 s, no inline comments", result.Description);
        Assert.Equal(TimeSpan.FromSeconds(135), result.AllowedTime);
    }

    [Fact]
    public void Large_fast_approval_with_inline_comments_succeeds()
    {
        var result = ReviewDepthRule.Evaluate(Input(comments: 2), Options);

        Assert.Equal(ReviewDepthState.Succeeded, result.State);
        Assert.False(result.Alert);
    }

    [Fact]
    public void Slow_review_succeeds()
    {
        var result = ReviewDepthRule.Evaluate(Input(secondsAfterPush: 136), Options);

        Assert.Equal(ReviewDepthState.Succeeded, result.State);
    }

    [Fact]
    public void Review_exactly_at_allowed_time_counts_as_fast()
    {
        // DESIGN.md: "review time > allowed time -> succeeded". Equal is not greater.
        var result = ReviewDepthRule.Evaluate(Input(secondsAfterPush: 135), Options);

        Assert.Equal(ReviewDepthState.Failed, result.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(199)]
    public void Small_pr_is_not_checked(int lines)
    {
        var result = ReviewDepthRule.Evaluate(Input(lines: lines, secondsAfterPush: 1), Options);

        Assert.Equal(ReviewDepthState.Succeeded, result.State);
        Assert.False(result.Alert);
    }

    [Fact]
    public void Pr_at_threshold_is_checked()
    {
        var result = ReviewDepthRule.Evaluate(Input(lines: 200, secondsAfterPush: 10), Options);

        Assert.Equal(ReviewDepthState.Failed, result.State);
    }

    [Fact]
    public void Missing_line_count_is_pending()
    {
        var result = ReviewDepthRule.Evaluate(Input(lines: null), Options);

        Assert.Equal(ReviewDepthState.Pending, result.State);
        Assert.False(result.Alert);
    }

    [Fact]
    public void Line_count_for_older_commit_is_pending()
    {
        var result = ReviewDepthRule.Evaluate(Input(lineCountCommit: "old999"), Options);

        Assert.Equal(ReviewDepthState.Pending, result.State);
    }

    [Fact]
    public void Commit_ids_compare_case_insensitive()
    {
        var result = ReviewDepthRule.Evaluate(Input(lineCountCommit: Commit.ToUpperInvariant()), Options);

        Assert.NotEqual(ReviewDepthState.Pending, result.State);
    }

    [Fact]
    public void Clock_starts_when_reviewer_was_added_after_the_last_push()
    {
        // Pushed at 10:00, reviewer added at 10:05, approved at 10:06 = 60 s review.
        var result = ReviewDepthRule.Evaluate(
            Input(secondsAfterPush: 360, reviewerAdded: Push.AddMinutes(5)), Options);

        Assert.Equal(TimeSpan.FromSeconds(60), result.ReviewTime);
        Assert.Equal(ReviewDepthState.Failed, result.State);
    }

    [Fact]
    public void Reviewer_added_before_the_last_push_does_not_move_the_clock()
    {
        var result = ReviewDepthRule.Evaluate(
            Input(secondsAfterPush: 200, reviewerAdded: Push.AddHours(-1)), Options);

        Assert.Equal(TimeSpan.FromSeconds(200), result.ReviewTime);
        Assert.Equal(ReviewDepthState.Succeeded, result.State);
    }

    [Fact]
    public void Approval_before_push_time_is_treated_as_zero_seconds()
    {
        var result = ReviewDepthRule.Evaluate(Input(secondsAfterPush: -5), Options);

        Assert.Equal(TimeSpan.Zero, result.ReviewTime);
        Assert.Equal(ReviewDepthState.Failed, result.State);
    }

    [Fact]
    public void Thresholds_come_from_options()
    {
        var strict = new ReviewDepthOptions { MinLines = 50, SecondsPer100Lines = 60 };

        // 100 lines, 50 s. Default options: small PR. Strict options: allowed 60 s, so fast.
        Assert.Equal(ReviewDepthState.Succeeded, ReviewDepthRule.Evaluate(Input(lines: 100), Options).State);
        Assert.Equal(ReviewDepthState.Failed, ReviewDepthRule.Evaluate(Input(lines: 100), strict).State);
    }
}
