using System.Text.Json.Serialization;

namespace ReviewGuard.Core.ServiceHooks;

/// <summary>
/// The parts of the Azure DevOps service hook payload for "git.pullrequest.updated" that we use.
/// Unknown fields are ignored by System.Text.Json.
/// </summary>
public sealed record PullRequestUpdatedEvent(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("eventType")] string EventType,
    [property: JsonPropertyName("createdDate")] DateTimeOffset CreatedDate,
    [property: JsonPropertyName("resource")] PullRequestResource? Resource);

public sealed record PullRequestResource(
    [property: JsonPropertyName("pullRequestId")] int PullRequestId,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("repository")] RepositoryRef? Repository,
    [property: JsonPropertyName("lastMergeSourceCommit")] CommitRef? LastMergeSourceCommit,
    [property: JsonPropertyName("reviewers")] IReadOnlyList<Reviewer>? Reviewers);

public sealed record RepositoryRef(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("project")] ProjectRef? Project);

public sealed record ProjectRef([property: JsonPropertyName("id")] string Id);

public sealed record CommitRef([property: JsonPropertyName("commitId")] string CommitId);

public sealed record Reviewer(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("vote")] int Vote,
    [property: JsonPropertyName("isContainer")] bool IsContainer);
