using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// The reminders & subscriptions area (#443, tranche 5): per-document reminders, follows, and the dashboard rows. The two id-based overloads stay on the god client until the documents finale retires DocumentAddress. Rides the shared authenticated <see cref="ApiCore"/> (#443).
/// </summary>
public sealed class RemindersClient(ApiCore core)
{
    private readonly ApiCore _core = core;


    // Takes the advertised href (detail.Href("subscription")) — one address, read/followed/unfollowed.
    public async Task<bool> GetSubscriptionAsync(string subscriptionHref, CancellationToken cancellationToken = default)
    {
        var json = await _core.Http.GetFromJsonAsync<JsonElement>(subscriptionHref, cancellationToken);
        return json.TryGetProperty("subscribed", out var s) && s.ValueKind == JsonValueKind.True;
    }

    // Follow (subscribe = true) or unfollow (false) the document.
    public async Task SetSubscriptionAsync(string subscriptionHref, bool subscribe, CancellationToken cancellationToken = default)
    {
        using var response = subscribe
            ? await _core.Http.PutAsync(subscriptionHref, null, cancellationToken)
            : await _core.Http.DeleteAsync(subscriptionHref, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiActionException($"Could not update your subscription ({(int)response.StatusCode}).");
        }
    }

    // A document reminder (Wiedervorlage, ADR "Document reminders"). Carries its own links, so cancelling one
    // follows the `cancel` rel the row advertised rather than rebuilding a path from two ids (ADR 0543/0555).
    public sealed record ReminderInfo(Guid Id, DateTimeOffset RemindAt, string? Note, int Recurrence, string RecurrenceName, string TargetName, IReadOnlyDictionary<string, string>? Links = null)
    {
        public string? Href(string rel) => Links is not null && Links.TryGetValue(rel, out var href) ? href : null;
    }

    // Dashboard rows (ADR "My work dashboard"): a due-soon reminder / a followed document, each with the
    // document + its parent folder for click-through.
    public sealed record DashReminderInfo(Guid DocumentId, Guid? ParentId, string DocumentName, DateTimeOffset RemindAt, string? Note, int Recurrence, string RecurrenceName, bool Overdue, IReadOnlyDictionary<string, string>? Links = null);

    // The caller's overdue + due-soon reminders across all documents (the dashboard's Reminders section).
    public async Task<IReadOnlyList<DashReminderInfo>> GetDashboardRemindersAsync(CancellationToken cancellationToken = default)
    {
        var json = await _core.Http.GetFromJsonAsync<JsonElement>(await _core.RootHrefAsync("reminders", cancellationToken), cancellationToken);
        var list = new List<DashReminderInfo>();
        if (json.TryGetProperty("reminders", out var arr))
        {
            foreach (var r in arr.EnumerateArray())
            {
                list.Add(new DashReminderInfo(
                    r.GetProperty("documentId").GetGuid(),
                    r.TryGetProperty("parentId", out var p) && p.ValueKind == JsonValueKind.String ? p.GetGuid() : null,
                    r.GetProperty("documentName").GetString() ?? "",
                    r.GetProperty("remindAt").GetDateTimeOffset(),
                    r.TryGetProperty("note", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null,
                    r.GetProperty("recurrence").GetInt32(),
                    r.TryGetProperty("recurrenceName", out var rn) ? rn.GetString() ?? "" : "",
                    r.TryGetProperty("overdue", out var o) && o.ValueKind == JsonValueKind.True,
                    ApiCore.ParseLinks(r)));
            }
        }

        return list;
    }

    // Active tenant users the caller can target a reminder at (the picker).
    //
    // The picker belongs to the reminders COLLECTION, which is what advertises `targets` — hanging "/targets"
    // off the reminders href would be composing a URL out of one the server happened to give us, which is the
    // same mistake in nicer clothing (ADR 0543). Callers that also want the reminders should take both from
    // GetRemindersViewAsync and pass the href here, so the collection is read once rather than twice.
    public async Task<IReadOnlyList<SimplArchiveApiClient.UserOptionInfo>> GetReminderTargetsAsync(string targetsHref, CancellationToken cancellationToken = default)
    {
        var json = await _core.Http.GetFromJsonAsync<JsonElement>(targetsHref, cancellationToken);
        var list = new List<SimplArchiveApiClient.UserOptionInfo>();
        if (json.TryGetProperty("targets", out var targets))
        {
            foreach (var u in targets.EnumerateArray())
            {
                list.Add(new SimplArchiveApiClient.UserOptionInfo(u.GetProperty("id").GetGuid(), u.GetProperty("displayName").GetString() ?? ""));
            }
        }

        return list;
    }

    // The caller's pending reminders on the document (set by or targeted at them).
    // Takes the advertised href (detail.Href("reminders")).
    public async Task<IReadOnlyList<ReminderInfo>> GetRemindersAsync(string remindersHref, CancellationToken cancellationToken = default) =>
        ParseReminders(await _core.Http.GetFromJsonAsync<JsonElement>(remindersHref, cancellationToken));

    internal static List<ReminderInfo> ParseReminders(JsonElement json)
    {
        var list = new List<ReminderInfo>();
        if (json.TryGetProperty("reminders", out var reminders))
        {
            foreach (var r in reminders.EnumerateArray())
            {
                list.Add(new ReminderInfo(
                    r.GetProperty("id").GetGuid(),
                    r.GetProperty("remindAt").GetDateTimeOffset(),
                    r.TryGetProperty("note", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null,
                    r.GetProperty("recurrence").GetInt32(),
                    r.TryGetProperty("recurrenceName", out var rn) ? rn.GetString() ?? "" : "",
                    r.TryGetProperty("targetName", out var tn) ? tn.GetString() ?? "" : "",
                    ApiCore.ParseLinks(r)));
            }
        }

        return list;
    }

    // Sets a reminder; targetUserId null = the caller. Returns nothing on success, throws on a rejected request.
    public async Task CreateReminderAsync(string remindersHref, DateTimeOffset remindAt, string? note, int recurrence, Guid? targetUserId, CancellationToken cancellationToken = default)
    {
        var body = new { remindAt, note, recurrence, targetUserId };
        using var response = await _core.Http.PostAsJsonAsync(remindersHref, body, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiActionException($"Could not set the reminder ({(int)response.StatusCode}).");
        }
    }

    /// <summary>Cancels the reminder at the address its own row advertised (ADR 0555).</summary>
    public async Task CancelReminderAsync(ReminderInfo reminder, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.DeleteAsync(RequireHref(reminder, "cancel"), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiActionException($"Could not cancel the reminder ({(int)response.StatusCode}).");
        }
    }

    private static string RequireHref(ReminderInfo reminder, string rel) =>
        reminder.Href(rel)
        ?? throw new InvalidOperationException($"The reminder row advertised no '{rel}' rel (ADR 0543/0555).");
}
