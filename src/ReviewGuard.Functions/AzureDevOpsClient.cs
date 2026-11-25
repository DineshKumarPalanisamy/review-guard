using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ReviewGuard.Core;

namespace ReviewGuard.Functions;

/// <summary>
/// Azure DevOps REST API 7.1 with a PAT (basic auth, empty user name).
/// The org uses Microsoft accounts, so Managed Identity cannot sign in here. See DESIGN.md section 12.
/// </summary>
public sealed class AzureDevOpsClient(HttpClient http, IOptions<AzureDevOpsOptions> options) : IAzureDevOpsClient
{
    public const string LinesProperty = "ReviewGuard.ChangedLines";
    public const string CommitProperty = "ReviewGuard.SourceCommit";
    private const string ApiVersion = "api-version=7.1";

    public async Task<PullRequestSnapshot> GetSnapshotAsync(PullRequestRef pr, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pr);
        var baseUrl = PrUrl(pr);

        var prTask = GetJsonAsync($"{baseUrl}?{ApiVersion}", ct);
        var propsTask = GetJsonAsync($"{baseUrl}/properties?{ApiVersion}", ct);
        var iterationsTask = GetJsonAsync($"{baseUrl}/iterations?{ApiVersion}", ct);
        var threadsTask = GetJsonAsync($"{baseUrl}/threads?{ApiVersion}", ct);
        await Task.WhenAll(prTask, propsTask, iterationsTask, threadsTask).ConfigureAwait(false);

        using var prDoc = await prTask.ConfigureAwait(false);
        using var propsDoc = await propsTask.ConfigureAwait(false);
        using var iterationsDoc = await iterationsTask.ConfigureAwait(false);
        using var threadsDoc = await threadsTask.ConfigureAwait(false);

        var p = prDoc.RootElement;
        var props = propsDoc.RootElement.TryGetProperty("value", out var v) ? v : default;
        var linesText = AzureDevOpsJson.ReadProperty(props, LinesProperty);
        int? lines = int.TryParse(linesText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

        var latestIteration = iterationsDoc.RootElement.GetProperty("value").EnumerateArray()
            .OrderByDescending(i => i.GetProperty("id").GetInt32())
            .First();
        var latestIterationId = latestIteration.GetProperty("id").GetInt32();
        var changedPaths = await GetChangedPathsAsync(baseUrl, latestIterationId, ct).ConfigureAwait(false);
        var repository = p.TryGetProperty("repository", out var repo) ? repo : default;

        var approvers = p.TryGetProperty("reviewers", out var reviewers)
            ? reviewers.EnumerateArray()
                .Where(r => !(r.TryGetProperty("isContainer", out var c) && c.GetBoolean()))
                .Select(r => new ApproverInfo(
                    r.GetProperty("id").GetString()!,
                    r.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "",
                    r.GetProperty("vote").GetInt32()))
                .ToList()
            : [];

        return new PullRequestSnapshot(
            Status: p.GetProperty("status").GetString() ?? "",
            SourceCommitId: p.GetProperty("lastMergeSourceCommit").GetProperty("commitId").GetString() ?? "",
            Title: p.TryGetProperty("title", out var t) ? t.GetString() : null,
            WebUrl: $"{options.Value.OrganizationUrl.TrimEnd('/')}/{pr.ProjectId}/_git/{pr.RepositoryId}/pullrequest/{pr.PullRequestId}",
            ChangedLines: lines,
            LineCountCommitId: AzureDevOpsJson.ReadProperty(props, CommitProperty),
            LatestIterationId: latestIterationId,
            LastPushAt: latestIteration.GetProperty("createdDate").GetDateTimeOffset(),
            Approvers: approvers,
            Threads: [.. threadsDoc.RootElement.GetProperty("value").EnumerateArray().Select(ReadThread)],
            ReviewerAdditions: AzureDevOpsJson.ReadReviewerAdditions(threadsDoc.RootElement),
            ProjectName: repository.ValueKind == JsonValueKind.Object && repository.TryGetProperty("project", out var proj)
                && proj.TryGetProperty("name", out var pn) ? pn.GetString() : null,
            RepositoryName: repository.ValueKind == JsonValueKind.Object && repository.TryGetProperty("name", out var rn) ? rn.GetString() : null,
            ChangedPaths: changedPaths);
    }

    /// <summary>
    /// All files the PR changes, compared with the target branch ($compareTo=0), not only the
    /// last push. The API returns pages: loop until nextSkip is 0, or large PRs lose files.
    /// </summary>
    private async Task<List<string>> GetChangedPathsAsync(string baseUrl, int iterationId, CancellationToken ct)
    {
        const int PageSize = 1000;
        var paths = new List<string>();
        var skip = 0;
        while (true)
        {
            using var doc = await GetJsonAsync(
                $"{baseUrl}/iterations/{iterationId}/changes?$compareTo=0&$top={PageSize}&$skip={skip}&{ApiVersion}", ct).ConfigureAwait(false);
            paths.AddRange(AzureDevOpsJson.ReadChangedPaths(doc.RootElement));

            var next = doc.RootElement.TryGetProperty("nextSkip", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : 0;
            if (next <= skip)
            {
                return paths;
            }

            skip = next;
        }
    }

    public async Task PostStatusAsync(PullRequestRef pr, PrStatus status, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pr);
        ArgumentNullException.ThrowIfNull(status);

        var body = new
        {
            state = status.State.ToString().ToLowerInvariant(),
            description = status.Description,
            context = new { genre = "review-guard", name = "review-depth" },
            iterationId = status.IterationId,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{PrUrl(pr)}/statuses?{ApiVersion}")
        {
            Content = JsonContent.Create(body),
        };
        Authorize(request);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    private string PrUrl(PullRequestRef pr) =>
        $"{options.Value.OrganizationUrl.TrimEnd('/')}/{pr.ProjectId}/_apis/git/repositories/{pr.RepositoryId}/pullRequests/{pr.PullRequestId}";

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        Authorize(request);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    private void Authorize(HttpRequestMessage request)
    {
        var token = Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + options.Value.Pat));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        // A wrong PAT gives a 203 with a sign-in HTML page, not a 401.
        if (response.StatusCode == System.Net.HttpStatusCode.NonAuthoritativeInformation || !response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException(
                $"Azure DevOps call {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.AbsolutePath} returned {(int)response.StatusCode}: {text[..Math.Min(300, text.Length)]}",
                null,
                response.StatusCode);
        }
    }

    private static ThreadInfo ReadThread(JsonElement thread)
    {
        string? filePath = null;
        int? line = null;
        if (thread.TryGetProperty("threadContext", out var ctx) && ctx.ValueKind == JsonValueKind.Object)
        {
            filePath = ctx.TryGetProperty("filePath", out var fp) ? fp.GetString() : null;
            line = ReadLine(ctx, "rightFileStart") ?? ReadLine(ctx, "leftFileStart");
        }

        var deleted = thread.TryGetProperty("isDeleted", out var d) && d.GetBoolean();
        var authors = deleted || !thread.TryGetProperty("comments", out var comments)
            ? []
            : comments.EnumerateArray()
                .Where(c => !(c.TryGetProperty("isDeleted", out var cd) && cd.GetBoolean()))
                .Where(c => !c.TryGetProperty("commentType", out var type) || type.GetString() != "system")
                .Select(c => c.GetProperty("author").GetProperty("id").GetString() ?? "")
                .ToList();

        var status = thread.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
        var startedBy = thread.TryGetProperty("comments", out var all) && all.GetArrayLength() > 0
            && all[0].TryGetProperty("author", out var first) && first.TryGetProperty("displayName", out var name)
            ? name.GetString()
            : null;

        return new ThreadInfo(filePath, line, authors, status, startedBy);
    }

    private static int? ReadLine(JsonElement ctx, string name) =>
        ctx.TryGetProperty(name, out var pos) && pos.ValueKind == JsonValueKind.Object && pos.TryGetProperty("line", out var l)
            ? l.GetInt32()
            : null;
}

public sealed class AzureDevOpsOptions
{
    public const string SectionName = "ReviewGuard";

    /// <summary>For example https://dev.azure.com/DigiCovai</summary>
    public string OrganizationUrl { get; set; } = "";

    /// <summary>PAT with Code (read) and Code (status). In Azure: a Key Vault reference.</summary>
    public string Pat { get; set; } = "";
}
