using System.Globalization;
using System.Text.Json.Serialization;

namespace ReviewGuard.Core.ServiceHooks;

/// <summary>
/// The parts of the "build.complete" service hook payload that we use.
/// A PR validation build has triggerInfo["pr.number"].
/// </summary>
public sealed record BuildCompletedEvent(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("eventType")] string EventType,
    [property: JsonPropertyName("createdDate")] DateTimeOffset CreatedDate,
    [property: JsonPropertyName("resource")] BuildResource? Resource,
    [property: JsonPropertyName("resourceContainers")] ResourceContainers? ResourceContainers)
{
    public PullRequestRef? ToPullRequestRef()
    {
        if (Resource?.Repository?.Id is not { Length: > 0 } repoId || PullRequestNumber(Resource) is not { } prId)
        {
            return null;
        }

        var projectId = Resource.Project?.Id ?? ResourceContainers?.Project?.Id;
        return projectId is null ? null : new PullRequestRef(projectId, repoId, prId);
    }

    /// <summary>triggerInfo["pr.number"], or the number in sourceBranch "refs/pull/391/merge".</summary>
    private static int? PullRequestNumber(BuildResource resource)
    {
        if (resource.TriggerInfo is { } info && info.TryGetValue("pr.number", out var number)
            && int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fromTrigger))
        {
            return fromTrigger;
        }

        const string prefix = "refs/pull/";
        if (resource.SourceBranch is { } branch && branch.StartsWith(prefix, StringComparison.Ordinal))
        {
            var rest = branch.AsSpan(prefix.Length);
            var slash = rest.IndexOf('/');
            if (int.TryParse(slash < 0 ? rest : rest[..slash], NumberStyles.Integer, CultureInfo.InvariantCulture, out var fromBranch))
            {
                return fromBranch;
            }
        }

        return null;
    }
}

public sealed record BuildResource(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("triggerInfo")] IReadOnlyDictionary<string, string>? TriggerInfo,
    [property: JsonPropertyName("sourceBranch")] string? SourceBranch,
    [property: JsonPropertyName("repository")] BuildRepository? Repository,
    [property: JsonPropertyName("project")] ProjectRef? Project);

public sealed record BuildRepository(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("type")] string? Type);

public sealed record ResourceContainers([property: JsonPropertyName("project")] ProjectRef? Project);
