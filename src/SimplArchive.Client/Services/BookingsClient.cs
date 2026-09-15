using System.Net.Http.Json;

namespace SimplArchive.Client.Services;

/// <summary>
/// Booking a room and cancelling a booking, with the <c>If-Match</c> the cancel needs (#1218).
/// </summary>
/// <remarks>
/// <para>
/// The mirror of the desktop's <c>BookingsClient</c> (ADR 0511). Both writes move, not just the one carrying
/// the header: leaving <c>book</c> composing its own request beside a delegated <c>cancel</c> would be worse
/// than leaving both, because the next reader could not tell which shape was intended.
/// </para>
/// <para>
/// <b>The error CODE, never the English detail</b> (#424). The server's <c>detail</c> is developer prose in one
/// language; <c>errorCode</c> is what <c>ApiErrorText</c> turns into the user's. The slot-conflict refusal is
/// the one people actually meet here, so getting this right is not a corner case. These methods return the
/// code — null on success — and the caller renders it, because the two dialogs place the message differently.
/// </para>
/// <para>
/// <see cref="ErrorCodeAsync"/> reads the problem body ONCE. It moved here with the requests deliberately: a
/// response body is a stream, and a second read of it yields nothing, so a helper that each caller re-derives
/// is a helper each caller can get wrong in the same invisible way.
/// </para>
/// </remarks>
public sealed class BookingsClient(HttpClient http)
{
    /// <summary>Books a slot. Null on success, else the server's error code for <c>ApiErrorText</c>.</summary>
    /// <remarks>
    /// The instants are the caller's: a picker hands back local wall-clock, and the server compares real
    /// instants under ADR 0735's <c>[start, end)</c> semantics. Converting here would guess at a zone this
    /// class cannot see.
    /// </remarks>
    public async Task<string?> BookAsync(
        string bookingsHref,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        string? purpose,
        CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync(
            bookingsHref, new { startsAt, endsAt, purpose }, cancellationToken);

        return response.IsSuccessStatusCode ? null : await ErrorCodeAsync(response);
    }

    /// <summary>
    /// Cancels via the row's <c>cancel</c> rel, the row-borne ETag as <c>If-Match</c>. Null on success.
    /// </summary>
    /// <remarks>
    /// The tag travels WITH the row (ADR 0557) — fetching one per cancel would be a request spent re-learning
    /// something already in hand. It is quoted here because that is how the row carries it and how an entity
    /// tag is spelled on the wire.
    /// </remarks>
    public async Task<string?> CancelAsync(string cancelHref, string etag, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, cancelHref);
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{etag}\"");
        using var response = await http.SendAsync(request, cancellationToken);

        return response.IsSuccessStatusCode ? null : await ErrorCodeAsync(response);
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            return problem.TryGetProperty("errorCode", out var code) ? code.GetString() : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
