using System.Text.Json;

namespace ReviewGuard.Core;

/// <summary>Parsing helpers for Azure DevOps REST responses. Pure functions, tested in Core.</summary>
public static class AzureDevOpsJson
{
    /// <summary>Properties come as {"key": {"$type": "System.String", "$value": "450"}}.</summary>
    public static string? ReadProperty(JsonElement props, string name)
    {
        if (props.ValueKind != JsonValueKind.Object || !props.TryGetProperty(name, out var prop))
        {
            return null;
        }

        var value = prop.ValueKind == JsonValueKind.Object && prop.TryGetProperty("$value", out var inner) ? inner : prop;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    /// <summary>
    /// Reads the system threads with CodeReviewThreadType = "ReviewersUpdate" from a threads
    /// response. Example properties: CodeReviewReviewersUpdatedAddedIdentity = "1",
    /// CodeReviewReviewersUpdatedByIdentity = "1". The numbers point into the thread's
    /// "identities" map. When both are the same identity, the reviewer joined by voting,
    /// and the entry is skipped.
    /// </summary>
    public static IReadOnlyList<ReviewerAddition> ReadReviewerAdditions(JsonElement threadsRoot)
    {
        var result = new List<ReviewerAddition>();
        foreach (var thread in threadsRoot.GetProperty("value").EnumerateArray())
        {
            if (!thread.TryGetProperty("properties", out var props)
                || ReadProperty(props, "CodeReviewThreadType") != "ReviewersUpdate"
                || !thread.TryGetProperty("publishedDate", out var published)
                || !thread.TryGetProperty("identities", out var identities)
                || identities.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var byId = IdentityId(identities, ReadProperty(props, "CodeReviewReviewersUpdatedByIdentity"));
            var addedKeys = (ReadProperty(props, "CodeReviewReviewersUpdatedAddedIdentity") ?? "")
                .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var key in addedKeys)
            {
                var addedId = IdentityId(identities, key);
                if (addedId is null || string.Equals(addedId, byId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                result.Add(new ReviewerAddition(addedId, published.GetDateTimeOffset()));
            }
        }

        return result;
    }

    /// <summary>File paths from one page of the iteration changes API. Folders are skipped.</summary>
    public static IEnumerable<string> ReadChangedPaths(JsonElement changesRoot)
    {
        if (!changesRoot.TryGetProperty("changeEntries", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("item", out var item)
                || (item.TryGetProperty("isFolder", out var folder) && folder.ValueKind == JsonValueKind.True)
                || !item.TryGetProperty("path", out var path)
                || path.GetString() is not { Length: > 0 } value)
            {
                continue;
            }

            yield return value;
        }
    }

    private static string? IdentityId(JsonElement identities, string? key) =>
        key is not null && identities.TryGetProperty(key, out var identity) && identity.TryGetProperty("id", out var id)
            ? id.GetString()
            : null;
}
