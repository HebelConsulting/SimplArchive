using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// The legal-holds + retention area (#443, ops tranche): holds and their items, and the retention
/// schedule with dispose/extend — all addressed from what the rows advertise (ADR 0543/0555).
/// Rides the shared authenticated <see cref="ApiCore"/>.
/// </summary>
public sealed class LegalHoldsClient(ApiCore core)
{
    private readonly ApiCore _core = core;

    private static string RequireHref(LegalHoldInfo hold, string rel) =>
        hold.Href(rel)
        ?? throw new InvalidOperationException($"The legal hold '{hold.Name}' advertised no '{rel}' rel — a RELEASED hold offers neither release nor add-item (ADR 0543/0555).");

    // ApiActionException, deliberately, and not InvalidOperationException (#870): the retention commands catch
    // the former and put it on the status line, while the latter escapes to the global crash guard — so a
    // missing rel used to give the user a CRASH DIALOG for a records action.
    //
    // The gate makes this unreachable (RetentionRowViewModel.CanDispose now reads the rel), so this is the floor
    // beneath the gate rather than the gate itself. A floor is still worth having: the row could be stale, and
    // "this document can no longer be disposed" is a sentence, whereas a crash dialog is an incident.
    private static string RequireHref(RetentionItemInfo item, string rel) =>
        item.Href(rel)
        ?? throw new ApiActionException($"'{item.DocumentName}' can no longer be {(rel == "dispose" ? "disposed" : "extended")} — a legal hold or a required review withholds it (ADR 0543/0555).");
    // ---- Legal holds (ADR "Legal hold & retention enforcement") -------------------------------------

    // A hold, carrying the addresses its own row advertised (ADR 0543/0555): `self`, plus `release`/`add-item`
    // only while it is active — a released hold offers neither, so the affordance is the server's answer.
    public sealed record LegalHoldInfo(Guid Id, string Name, string? Reason, DateTimeOffset PlacedAt, bool IsActive, int ItemCount, List<LegalHoldItemInfo> Items, IReadOnlyDictionary<string, string>? Links = null)
    {
        public string? Href(string rel) => Links is not null && Links.TryGetValue(rel, out var href) ? href : null;
    }

    // A covered document. RemoveHref is the pairing's own address — the item is the only thing that knows both
    // ends of it — and is null once the hold is released.
    // The row's advertised addresses (ADR 0555): `remove` while the hold is active, plus `document`/`parent`
    // — what the Go-to follows (#443).
    public sealed record LegalHoldItemInfo(Guid DocumentId, string DocumentName, string? RemoveHref = null, Guid? ParentId = null,
        IReadOnlyDictionary<string, string>? Links = null)
    {
        public string? Href(string rel) => Links is not null && Links.TryGetValue(rel, out var href) ? href : null;
    }

    public async Task<List<LegalHoldInfo>> GetLegalHoldsAsync(CancellationToken cancellationToken = default)
    {
        var json = await _core.Http.GetFromJsonAsync<JsonElement>(await _core.RootHrefAsync("legalHolds", cancellationToken), cancellationToken);
        var list = new List<LegalHoldInfo>();
        if (json.TryGetProperty("holds", out var holds))
        {
            foreach (var h in holds.EnumerateArray())
            {
                list.Add(ParseLegalHold(h));
            }
        }

        return list;
    }

    public async Task<LegalHoldInfo> GetLegalHoldAsync(LegalHoldInfo hold, CancellationToken cancellationToken = default) =>
        ParseLegalHold(await _core.Http.GetFromJsonAsync<JsonElement>(RequireHref(hold, "self"), cancellationToken));

    public async Task<LegalHoldInfo> CreateLegalHoldAsync(string name, string? reason, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.PostAsJsonAsync(await _core.RootHrefAsync("legalHolds", cancellationToken), new { name, reason }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to place legal holds.");
        }

        response.EnsureSuccessStatusCode();
        return ParseLegalHold(await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken));
    }

    public async Task AddLegalHoldItemAsync(LegalHoldInfo hold, Guid documentId, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.PostAsJsonAsync(RequireHref(hold, "add-item"), new { documentId }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new ApiActionException("The document is already on this hold.");
        }

        response.EnsureSuccessStatusCode();
    }

    public async Task RemoveLegalHoldItemAsync(LegalHoldItemInfo item, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.DeleteAsync(
            item.RemoveHref ?? throw new InvalidOperationException($"'{item.DocumentName}' advertised no 'remove' rel — a released hold offers none (ADR 0543/0555)."),
            cancellationToken);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    public async Task ReleaseLegalHoldAsync(LegalHoldInfo hold, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.PostAsync(RequireHref(hold, "release"), null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static LegalHoldInfo ParseLegalHold(JsonElement e)
    {
        var items = new List<LegalHoldItemInfo>();
        if (e.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var i in itemsEl.EnumerateArray())
            {
                items.Add(new LegalHoldItemInfo(i.GetProperty("documentId").GetGuid(), i.GetProperty("documentName").GetString() ?? "", ApiCore.RelHref(i, "remove"), i.TryGetProperty("parentId", out var pp) && pp.ValueKind == JsonValueKind.String ? pp.GetGuid() : null, ApiCore.ParseLinks(i)));
            }
        }

        return new LegalHoldInfo(
            e.GetProperty("id").GetGuid(),
            e.GetProperty("name").GetString() ?? "",
            e.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null,
            e.GetProperty("placedAt").GetDateTimeOffset(),
            e.TryGetProperty("isActive", out var a) && a.ValueKind == JsonValueKind.True,
            e.TryGetProperty("itemCount", out var c) ? c.GetInt32() : items.Count,
            items,
            ApiCore.ParseLinks(e));
    }

    // ---- Retention schedule (ADR "Retention policies (auto-disposition)") ---------------------------

    // A scheduled document. `dispose` is CONDITIONAL server-side — absent while a review is required or a hold
    // suspends it — so the row's own links are what decide whether the action is offered (ADR 0543/0555).
    public sealed record RetentionItemInfo(Guid DocumentId, string DocumentName, int RetentionYears, string DispositionDate, bool Overdue, bool SuspendedByHold, string? RetentionOverrideUntil, IReadOnlyDictionary<string, string>? Links = null)
    {
        public string? Href(string rel) => Links is not null && Links.TryGetValue(rel, out var href) ? href : null;
    }
    public sealed record RetentionScheduleInfo(IReadOnlyList<RetentionItemInfo> Items, bool RequiresReview);

    public async Task<RetentionScheduleInfo> GetRetentionScheduleAsync(CancellationToken cancellationToken = default)
    {
        var json = await _core.Http.GetFromJsonAsync<JsonElement>(await _core.RootHrefAsync("retentionSchedule", cancellationToken), cancellationToken);
        var list = new List<RetentionItemInfo>();
        if (json.TryGetProperty("items", out var items))
        {
            foreach (var i in items.EnumerateArray())
            {
                list.Add(new RetentionItemInfo(
                    i.GetProperty("documentId").GetGuid(),
                    i.GetProperty("documentName").GetString() ?? "",
                    i.TryGetProperty("retentionYears", out var y) ? y.GetInt32() : 0,
                    i.GetProperty("dispositionDate").GetString() ?? "",
                    i.TryGetProperty("overdue", out var o) && o.ValueKind == JsonValueKind.True,
                    i.TryGetProperty("suspendedByHold", out var s) && s.ValueKind == JsonValueKind.True,
                    i.TryGetProperty("retentionOverrideUntil", out var ru) && ru.ValueKind == JsonValueKind.String ? ru.GetString() : null,
                    ApiCore.ParseLinks(i)));
            }
        }

        return new RetentionScheduleInfo(list, json.TryGetProperty("requiresReview", out var rr) && rr.ValueKind == JsonValueKind.True);
    }

    // Manually dispose an eligible document (ADR "Retention review-before-disposition").
    public async Task DisposeRetentionAsync(RetentionItemInfo item, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.PostAsync(RequireHref(item, "dispose"), null, cancellationToken);
        await SimplArchiveApiClient.ThrowIfProblemAsync(response, "Could not dispose the document.", cancellationToken);
    }

    // Extend a document's retention to a new "retain until" date ("yyyy-MM-dd").
    public async Task ExtendRetentionAsync(RetentionItemInfo item, string until, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.PostAsJsonAsync(RequireHref(item, "extend"), new { until }, cancellationToken);
        await SimplArchiveApiClient.ThrowIfProblemAsync(response, "Could not extend retention.", cancellationToken);
    }
}
