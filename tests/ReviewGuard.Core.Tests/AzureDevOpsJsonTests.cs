using System.Text.Json;
using ReviewGuard.Core;

namespace ReviewGuard.Core.Tests;

public sealed class AzureDevOpsJsonTests
{
    // Shape copied from a real threads response on review-guard-sandbox PR 401 (ids changed).
    private const string Threads = """
        {
          "value": [
            {
              "id": 1, "publishedDate": "2026-11-25T11:00:27Z",
              "properties": {
                "CodeReviewThreadType": { "$type": "System.String", "$value": "ReviewersUpdate" },
                "CodeReviewReviewersUpdatedAddedIdentity": { "$type": "System.String", "$value": "1" },
                "CodeReviewReviewersUpdatedByIdentity": { "$type": "System.String", "$value": "1" },
                "CodeReviewReviewersUpdatedNumAdded": { "$type": "System.Int32", "$value": 1 }
              },
              "identities": { "1": { "id": "ana", "displayName": "Ana" } }
            },
            {
              "id": 2, "publishedDate": "2026-11-25T10:30:00Z",
              "properties": {
                "CodeReviewThreadType": { "$type": "System.String", "$value": "ReviewersUpdate" },
                "CodeReviewReviewersUpdatedAddedIdentity": { "$type": "System.String", "$value": "2" },
                "CodeReviewReviewersUpdatedByIdentity": { "$type": "System.String", "$value": "1" }
              },
              "identities": { "1": { "id": "ana" }, "2": { "id": "ben" } }
            },
            {
              "id": 3, "publishedDate": "2026-11-25T11:00:27Z",
              "properties": {
                "CodeReviewThreadType": { "$type": "System.String", "$value": "VoteUpdate" },
                "CodeReviewVoteResult": { "$type": "System.String", "$value": "10" }
              },
              "identities": { "1": { "id": "ana" } }
            },
            { "id": 4, "publishedDate": "2026-11-25T11:05:00Z", "threadContext": { "filePath": "/a.cs" } }
          ]
        }
        """;

    [Fact]
    public void Only_reviewers_added_by_someone_else_count()
    {
        using var doc = JsonDocument.Parse(Threads);

        var additions = AzureDevOpsJson.ReadReviewerAdditions(doc.RootElement);

        // Thread 1 is Ana joining by voting (added by herself): skipped.
        var added = Assert.Single(additions);
        Assert.Equal("ben", added.ReviewerId);
        Assert.Equal(new DateTimeOffset(2026, 11, 25, 10, 30, 0, TimeSpan.Zero), added.AddedAt);
    }

    [Fact]
    public void Property_value_is_read_from_the_dollar_value_wrapper()
    {
        using var doc = JsonDocument.Parse("""{ "a": { "$type": "System.Int32", "$value": 450 }, "b": "x" }""");

        Assert.Equal("450", AzureDevOpsJson.ReadProperty(doc.RootElement, "a"));
        Assert.Equal("x", AzureDevOpsJson.ReadProperty(doc.RootElement, "b"));
        Assert.Null(AzureDevOpsJson.ReadProperty(doc.RootElement, "c"));
    }

    [Fact]
    public void Changed_paths_skip_folders_and_missing_items()
    {
        using var doc = JsonDocument.Parse("""
            {
              "changeEntries": [
                { "changeType": "add", "item": { "path": "/src/SampleApp/Auth/TokenService.cs" } },
                { "changeType": "add", "item": { "path": "/src/SampleApp/Auth", "isFolder": true } },
                { "changeType": "delete", "item": { "path": "/src/Old.cs" } },
                { "changeType": "edit" }
              ],
              "nextSkip": 4, "nextTop": 2
            }
            """);

        Assert.Equal(["/src/SampleApp/Auth/TokenService.cs", "/src/Old.cs"], AzureDevOpsJson.ReadChangedPaths(doc.RootElement));
    }
}
