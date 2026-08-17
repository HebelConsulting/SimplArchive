using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// The audit-log area (#443, ops tranche): the tab's event listing/export, the hash-chain + WORM
/// verifications, and audit retention/purge. Rides the shared authenticated <see cref="ApiCore"/>.
/// </summary>
public sealed class AuditClient(ApiCore core)
{
    private readonly ApiCore _core = core;

    // Audit log (ADRs "Audit trail (first slice)" / "... hash chain" / "... retention and purge").
    public sealed record AuditEventInfo(DateTimeOffset Timestamp, string ActorType, string ActorName, string Action, string? TargetType, string? TargetName, string? Details);
    public sealed record AuditPage(IReadOnlyList<AuditEventInfo> Events, string? NextCursor);
    public sealed record AuditVerifyInfo(bool Valid, int CheckedCount, long? BrokenAtSequence);
    public sealed record AuditRetentionInfo(int RetentionDays, long ChainStartSequence, DateTimeOffset? LastPurgedAt);
    public sealed record AuditPurgeInfo(int PurgedCount, long ChainStartSequence);

    // A page of audit events, newest first, with optional filters + an opaque cursor for "load more".
    public async Task<AuditPage> GetAuditEventsAsync(string? action, DateTimeOffset? from, DateTimeOffset? to, string? cursor, CancellationToken cancellationToken = default)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(action)) query.Add($"action={Uri.EscapeDataString(action.Trim())}");
        if (from is { } f) query.Add($"from={Uri.EscapeDataString(f.UtcDateTime.ToString("o"))}");
        if (to is { } t) query.Add($"to={Uri.EscapeDataString(t.UtcDateTime.ToString("o"))}");
        if (cursor is not null) query.Add($"cursor={Uri.EscapeDataString(cursor)}");

        var url = await _core.RootHrefAsync("auditEvents", cancellationToken) + (query.Count > 0 ? "?" + string.Join("&", query) : "");
        var page = await _core.Http.GetFromJsonAsync<JsonElement>(url, cancellationToken);
        var events = page.TryGetProperty("events", out var array)
            ? array.EnumerateArray().Select(ParseAuditEvent).ToList()
            : new List<AuditEventInfo>();
        return new AuditPage(events, ExtractCursor(ApiCore.FindLink(page, "next")));
    }

    // Exports the tenant audit log (respecting the filters) as NDJSON bytes (ADR "Audit trail export").
    public async Task<byte[]> ExportAuditEventsAsync(string? action, DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken = default)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(action)) query.Add($"action={Uri.EscapeDataString(action.Trim())}");
        if (from is { } f) query.Add($"from={Uri.EscapeDataString(f.UtcDateTime.ToString("o"))}");
        if (to is { } t) query.Add($"to={Uri.EscapeDataString(t.UtcDateTime.ToString("o"))}");

        var url = await AuditRelAsync("export", cancellationToken) + (query.Count > 0 ? "?" + string.Join("&", query) : "");
        return await _core.Http.GetByteArrayAsync(url, cancellationToken);
    }

    public async Task<AuditVerifyInfo> VerifyAuditChainAsync(CancellationToken cancellationToken = default)
    {
        var json = await _core.Http.GetFromJsonAsync<JsonElement>(await AuditRelAsync("verify", cancellationToken), cancellationToken);
        return new AuditVerifyInfo(
            json.GetProperty("valid").GetBoolean(),
            json.GetProperty("checkedCount").GetInt32(),
            json.TryGetProperty("brokenAtSequence", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt64() : null);
    }

    public sealed record WormVerifyInfo(bool Valid, int SegmentCount, int CheckedCount, long? BrokenAtSequence, string? Reason);

    // Verifies the sealed WORM segments against the DB (ADR "Audit WORM segment verify").
    public async Task<WormVerifyInfo> VerifyAuditWormAsync(CancellationToken cancellationToken = default)
    {
        var json = await _core.Http.GetFromJsonAsync<JsonElement>(await AuditRelAsync("worm-verify", cancellationToken), cancellationToken);
        return new WormVerifyInfo(
            json.GetProperty("valid").GetBoolean(),
            json.GetProperty("segmentCount").GetInt32(),
            json.GetProperty("checkedCount").GetInt32(),
            json.TryGetProperty("brokenAtSequence", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt64() : null,
            json.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null);
    }

    // Retention, export, verify, worm-verify and purge are all rels ON the audit-events collection (issue
    // #416), so reaching any of them means reading that collection — and pulling a page of audit events to
    // learn one address is the "two round trips, one of them large" trap. `?limit=1` on the advertised href
    // keeps the read trivial while the address still comes from the server: a query on a rel's href, not a
    // path this client invented.
    //
    // Cached like the API root's own rels, and for the same reason: these five do not change between calls,
    // and the audit tab would otherwise re-read the collection once per button.
    private async Task<string> AuditRelAsync(string rel, CancellationToken cancellationToken)
    {
        if (_auditLinks is null)
        {
            await _auditGate.WaitAsync(cancellationToken);
            try
            {
                if (_auditLinks is null)
                {
                    var href = await _core.RootHrefAsync("auditEvents", cancellationToken);
                    var page = await _core.Http.GetFromJsonAsync<JsonElement>($"{href}?limit=1", cancellationToken);
                    _auditLinks = ApiCore.ParseLinks(page) ?? new Dictionary<string, string>();
                }
            }
            finally
            {
                _auditGate.Release();
            }
        }

        return _auditLinks.TryGetValue(rel, out var relHref)
            ? relHref
            : throw new InvalidOperationException($"The audit log advertised no '{rel}' rel (ADR 0543).");
    }

    private IReadOnlyDictionary<string, string>? _auditLinks;
    private readonly SemaphoreSlim _auditGate = new(1, 1);

    private Task<string> AuditRetentionHrefAsync(CancellationToken cancellationToken) =>
        AuditRelAsync("retention", cancellationToken);

    public async Task<AuditRetentionInfo> GetAuditRetentionAsync(CancellationToken cancellationToken = default) =>
        ParseRetention(await _core.Http.GetFromJsonAsync<JsonElement>(await AuditRetentionHrefAsync(cancellationToken), cancellationToken));

    public async Task<AuditRetentionInfo> SetAuditRetentionAsync(int retentionDays, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.PutAsJsonAsync(await AuditRetentionHrefAsync(cancellationToken), new { retentionDays }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to change audit retention.");
        }

        response.EnsureSuccessStatusCode();
        return ParseRetention(await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken));
    }

    public async Task<AuditPurgeInfo> PurgeAuditAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.PostAsync(await AuditRelAsync("purge", cancellationToken), null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to purge the audit log.");
        }

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return new AuditPurgeInfo(json.GetProperty("purgedCount").GetInt32(), json.GetProperty("chainStartSequence").GetInt64());
    }

    private static AuditEventInfo ParseAuditEvent(JsonElement e) => new(
        e.GetProperty("timestamp").GetDateTimeOffset(),
        e.TryGetProperty("actorType", out var at) ? at.GetString() ?? "" : "",
        e.TryGetProperty("actorName", out var an) ? an.GetString() ?? "" : "",
        e.TryGetProperty("action", out var ac) ? ac.GetString() ?? "" : "",
        SimplArchiveApiClient.StrOrNull(e, "targetType"),
        SimplArchiveApiClient.StrOrNull(e, "targetName"),
        SimplArchiveApiClient.StrOrNull(e, "details"));

    private static AuditRetentionInfo ParseRetention(JsonElement json) => new(
        json.GetProperty("retentionDays").GetInt32(),
        json.GetProperty("chainStartSequence").GetInt64(),
        json.TryGetProperty("lastPurgedAt", out var lp) && lp.ValueKind == JsonValueKind.String ? lp.GetDateTimeOffset() : null);

    // Pulls the cursor value out of a "next" hypermedia href (…?cursor=…&limit=…).
    private static string? ExtractCursor(string? nextHref)
    {
        if (string.IsNullOrEmpty(nextHref))
        {
            return null;
        }

        var index = nextHref.IndexOf("cursor=", StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        var value = nextHref[(index + "cursor=".Length)..];
        var amp = value.IndexOf('&');
        return Uri.UnescapeDataString(amp >= 0 ? value[..amp] : value);
    }
}
