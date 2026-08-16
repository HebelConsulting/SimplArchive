using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// The notifications area (#443, tranche 5): the bell's listing and read-marking. Rides the shared authenticated <see cref="ApiCore"/> (#443).
/// </summary>
public sealed class NotificationsClient(ApiCore core)
{
    private readonly ApiCore _core = core;


    private static string RequireHref(NotificationInfo notification, string rel) =>
        notification.Href(rel)
        ?? throw new InvalidOperationException($"The notification row advertised no '{rel}' rel (ADR 0543/0555).");

    // ---- In-app notifications viewer (ADR "Notification viewer + click-through") ---------------------

    // A notification row, carrying its own `read` address (ADR 0543/0555) — an already-read one advertises
    // none, so "can this be marked read" is the server's answer rather than an IsRead flag re-interpreted here.
    public sealed record NotificationInfo(Guid Id, string Type, string Title, string Body, Guid? DocumentId, Guid? DocumentParentId, DateTimeOffset CreatedAt, bool IsRead, int EventCount = 1, IReadOnlyDictionary<string, string>? Links = null)
    {
        public string? Href(string rel) => Links is not null && Links.TryGetValue(rel, out var href) ? href : null;
    }

    // ReadAllHref is the collection's own `read-all`; null when the server did not offer it.
    public sealed record NotificationList(IReadOnlyList<NotificationInfo> Items, int UnreadCount, string? ReadAllHref = null);

    public async Task<NotificationList> GetNotificationsAsync(CancellationToken cancellationToken = default)
    {
        var json = await _core.Http.GetFromJsonAsync<JsonElement>(await _core.RootHrefAsync("notifications", cancellationToken), cancellationToken);
        var items = new List<NotificationInfo>();
        if (json.TryGetProperty("notifications", out var arr))
        {
            foreach (var n in arr.EnumerateArray())
            {
                items.Add(new NotificationInfo(
                    n.GetProperty("id").GetGuid(),
                    n.GetProperty("type").GetString() ?? "",
                    n.GetProperty("title").GetString() ?? "",
                    n.GetProperty("body").GetString() ?? "",
                    n.TryGetProperty("documentId", out var d) && d.ValueKind != JsonValueKind.Null ? d.GetGuid() : null,
                    n.TryGetProperty("documentParentId", out var p) && p.ValueKind != JsonValueKind.Null ? p.GetGuid() : null,
                    n.GetProperty("createdAt").GetDateTimeOffset(),
                    n.TryGetProperty("isRead", out var r) && r.ValueKind == JsonValueKind.True,
                    n.TryGetProperty("eventCount", out var ec) && ec.ValueKind == JsonValueKind.Number ? ec.GetInt32() : 1,
                    ApiCore.ParseLinks(n)));
            }
        }

        return new NotificationList(
            items,
            json.TryGetProperty("unreadCount", out var uc) ? uc.GetInt32() : 0,
            ApiCore.ParseLinks(json) is { } links && links.TryGetValue("read-all", out var readAll) ? readAll : null);
    }

    /// <summary>Marks one notification read at the address its own row advertised (ADR 0555).</summary>
    public async Task MarkNotificationReadAsync(NotificationInfo notification, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.PostAsync(RequireHref(notification, "read"), null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Marks everything read at the collection's own `read-all` address (ADR 0555).</summary>
    public async Task MarkAllNotificationsReadAsync(string readAllHref, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.PostAsync(readAllHref, null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
