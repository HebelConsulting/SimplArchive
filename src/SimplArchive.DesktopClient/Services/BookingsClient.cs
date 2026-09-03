using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// The inventory-booking primitive's client half (ADR 0735): list a resource's bookings, book a slot,
/// cancel one. Every address is followed from a rel — the `bookings` rel a bookable document advertises,
/// and the `cancel` rel + row-borne etag each row carries (ADR 0557: the token travels with the row, or
/// every cancel costs a fetch).
/// </summary>
public sealed class BookingsClient(ApiCore core)
{
    public sealed record BookingRow(
        Guid Id,
        DateTimeOffset StartsAt,
        DateTimeOffset EndsAt,
        string Status,
        string BookedBy,
        string? Purpose,
        bool CanCancel,
        string Etag,
        string? CancelHref);

    public async Task<(IReadOnlyList<BookingRow> Rows, bool CanBook)> ListAsync(string bookingsHref, CancellationToken cancellationToken = default)
    {
        var json = await core.Http.GetFromJsonAsync<JsonElement>(bookingsHref, cancellationToken);
        var rows = new List<BookingRow>();
        if (json.TryGetProperty("bookings", out var bookings) && bookings.ValueKind == JsonValueKind.Array)
        {
            foreach (var b in bookings.EnumerateArray())
            {
                rows.Add(new BookingRow(
                    b.GetProperty("id").GetGuid(),
                    b.GetProperty("startsAt").GetDateTimeOffset(),
                    b.GetProperty("endsAt").GetDateTimeOffset(),
                    b.TryGetProperty("status", out var s) ? s.GetString() ?? string.Empty : string.Empty,
                    b.TryGetProperty("bookedBy", out var w) ? w.GetString() ?? string.Empty : string.Empty,
                    b.TryGetProperty("purpose", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null,
                    b.TryGetProperty("canCancel", out var c) && c.ValueKind == JsonValueKind.True,
                    b.TryGetProperty("etag", out var e) ? e.GetString() ?? string.Empty : string.Empty,
                    ApiCore.RelHref(b, "cancel")));
            }
        }

        var canBook = json.TryGetProperty("canBook", out var cb) && cb.ValueKind == JsonValueKind.True;
        return (rows, canBook);
    }

    /// <summary>Books a slot. A refusal surfaces the errorCode mapped through ApiErrorText (issue #424).</summary>
    public async Task BookAsync(string bookingsHref, DateTimeOffset startsAt, DateTimeOffset endsAt, string? purpose, CancellationToken cancellationToken = default)
    {
        var response = await core.Http.PostAsJsonAsync(bookingsHref, new { startsAt, endsAt, purpose }, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiActionException(SimplArchive.Localization.ApiErrorText.For(await ApiCore.ErrorCodeAsync(response, cancellationToken)));
        }
    }

    /// <summary>Cancels via the row's `cancel` rel, the row-borne etag as If-Match.</summary>
    public async Task CancelAsync(BookingRow row, CancellationToken cancellationToken = default)
    {
        if (row.CancelHref is null)
        {
            return; // no rel means "not available to you, here, now" (ADR 0543) — nothing to do.
        }

        using var request = new HttpRequestMessage(HttpMethod.Delete, row.CancelHref);
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{row.Etag}\"");
        var response = await core.Http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiActionException(SimplArchive.Localization.ApiErrorText.For(await ApiCore.ErrorCodeAsync(response, cancellationToken)));
        }
    }
}
