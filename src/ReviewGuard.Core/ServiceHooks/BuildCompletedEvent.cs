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
        if (Resource?.TriggerInfo is null
            || !Resource.TriggerInfo.TryGetValue("pr.number", out var number)
            || !int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var prId)
            || Resource.Repository?.Id is not { Length: > 0 } repoId)
        {
            return null;
        }

        var projectId = Resource.Project?.Id ?? ResourceContainers?.Project?.Id;
        return projectId is null ? null : new PullRequestRef(projectId, repoId, prId);
    }
}

public sealed record BuildResource(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("triggerInfo")] IReadOnlyDictionary<string, string>? TriggerInfo,
    [property: JsonPropertyName("repository")] BuildRepository? Repository,
    [property: JsonPropertyName("project")] ProjectRef? Project);

public sealed record BuildRepository(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("type")] string? Type);

public sealed record ResourceContainers([property: JsonPropertyName("project")] ProjectRef? Project);
