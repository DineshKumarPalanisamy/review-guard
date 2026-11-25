using System.Globalization;
using Azure;
using Azure.Data.Tables;
using ReviewGuard.Core;

namespace ReviewGuard.Functions;

/// <summary>
/// Three tables in the Function's storage account:
/// ReviewEvents (seen event ids), ReviewApprovals (first approval time), ReviewDecisions (audit log).
/// </summary>
public sealed class TableAuditStore(TableServiceClient service) : IAuditStore
{
    private readonly TableClient events = service.GetTableClient("ReviewEvents");
    private readonly TableClient approvals = service.GetTableClient("ReviewApprovals");
    private readonly TableClient decisions = service.GetTableClient("ReviewDecisions");
    private bool created;

    public async Task<bool> IsEventSeenAsync(string eventId, CancellationToken ct)
    {
        await EnsureTablesAsync(ct).ConfigureAwait(false);
        var (pk, rk) = EventKeys(eventId);
        var existing = await events.GetEntityIfExistsAsync<TableEntity>(pk, rk, ["PartitionKey"], ct).ConfigureAwait(false);
        return existing.HasValue;
    }

    public async Task MarkEventSeenAsync(string eventId, CancellationToken ct)
    {
        await EnsureTablesAsync(ct).ConfigureAwait(false);
        var (pk, rk) = EventKeys(eventId);
        // Upsert: two parallel deliveries of the same event may both get here.
        await events.UpsertEntityAsync(new TableEntity(pk, rk) { ["SeenAt"] = DateTimeOffset.UtcNow }, TableUpdateMode.Replace, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The partition comes from the event id, not from today's date, so a retry on the next
    /// day still finds the row. Event ids are GUIDs, so the first two characters spread the rows.
    /// </summary>
    private static (string Pk, string Rk) EventKeys(string eventId)
    {
        var rk = Safe(eventId);
        return ("e" + rk[..Math.Min(2, rk.Length)].ToLowerInvariant(), rk);
    }

    public async Task<ApprovalRecord> GetOrAddApprovalAsync(ApprovalRecord candidate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        await EnsureTablesAsync(ct).ConfigureAwait(false);

        var (pk, rk) = Keys(candidate);
        var existing = await approvals.GetEntityIfExistsAsync<TableEntity>(pk, rk, cancellationToken: ct).ConfigureAwait(false);
        if (existing.HasValue && existing.Value is { } e)
        {
            return candidate with
            {
                FirstApprovedAt = e.GetDateTimeOffset("FirstApprovedAt") ?? candidate.FirstApprovedAt,
                AlertSent = e.GetBoolean("AlertSent") ?? false,
            };
        }

        var entity = new TableEntity(pk, rk)
        {
            ["FirstApprovedAt"] = candidate.FirstApprovedAt,
            ["AlertSent"] = false,
            ["ProjectId"] = candidate.PullRequest.ProjectId,
        };
        try
        {
            await approvals.AddEntityAsync(entity, ct).ConfigureAwait(false);
            return candidate;
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            // Another event stored it first. Read theirs.
            return await GetOrAddApprovalAsync(candidate, ct).ConfigureAwait(false);
        }
    }

    public async Task MarkAlertSentAsync(ApprovalRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        var (pk, rk) = Keys(record);
        var entity = new TableEntity(pk, rk) { ["AlertSent"] = true };
        await approvals.UpdateEntityAsync(entity, ETag.All, TableUpdateMode.Merge, ct).ConfigureAwait(false);
    }

    public async Task WriteDecisionAsync(DecisionRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        await EnsureTablesAsync(ct).ConfigureAwait(false);

        // Newest first inside a partition.
        var rk = (DateTimeOffset.MaxValue.Ticks - DateTimeOffset.UtcNow.Ticks).ToString("D19", CultureInfo.InvariantCulture) + "_" + Safe(record.ReviewerId);
        var entity = new TableEntity(record.PullRequest.Key, rk)
        {
            ["ProjectId"] = record.PullRequest.ProjectId,
            ["ReviewerId"] = record.ReviewerId,
            ["ReviewerName"] = record.ReviewerName,
            ["SourceCommitId"] = record.SourceCommitId,
            ["EventId"] = record.EventId,
            ["State"] = record.Decision.State.ToString(),
            ["Description"] = record.Decision.Description,
            ["Alert"] = record.Decision.Alert,
            ["ChangedLines"] = record.ChangedLines,
            ["InlineComments"] = record.InlineComments,
            ["ReviewSeconds"] = record.Decision.ReviewTime?.TotalSeconds,
            ["AllowedSeconds"] = record.Decision.AllowedTime?.TotalSeconds,
        };
        await decisions.AddEntityAsync(entity, ct).ConfigureAwait(false);
    }

    private static (string Pk, string Rk) Keys(ApprovalRecord r) =>
        (r.PullRequest.Key, $"{Safe(r.ReviewerId)}_{r.SourceCommitId}");

    /// <summary>Table keys may not contain / \ # ?</summary>
    private static string Safe(string key) =>
        key.Replace('/', '_').Replace('\\', '_').Replace('#', '_').Replace('?', '_');

    private async Task EnsureTablesAsync(CancellationToken ct)
    {
        if (created)
        {
            return;
        }

        await Task.WhenAll(
            events.CreateIfNotExistsAsync(ct),
            approvals.CreateIfNotExistsAsync(ct),
            decisions.CreateIfNotExistsAsync(ct)).ConfigureAwait(false);
        created = true;
    }
}
