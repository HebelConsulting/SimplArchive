using System.Net.Http.Json;
using System.Text.Json;
using SimplArchive.Localization;

namespace SimplArchive.ApiClient;

/// <summary>
/// The wire helpers every area client shares (ADR 0917): problem responses in the reader's language, link maps,
/// optional strings, and the content funnel by its old name.
/// </summary>
public static class ApiWire
{
    // Turns a failed response into an ApiActionException carrying text the USER can read, in their language.
    //
    // Reads `errorCode`, not `detail`. The detail is English — the API's 153 exception classes carry their
    // message as a constructor literal, so no Accept-Language handling reaches them — and this method is on the
    // path of every failed call in the desktop, which made it the single biggest source of English in an
    // otherwise German UI (issue #424). The code is the stable, language-neutral contract (ADR 0543), so it
    // crosses the wire and ApiErrorText supplies the words.
    public static async Task ThrowIfProblemAsync(HttpResponseMessage response, string fallback, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string? errorCode = null;
        var offered = new List<(DateTimeOffset StartsAt, DateTimeOffset EndsAt)>();
        try
        {
            var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            if (json.TryGetProperty("errorCode", out var c) && c.GetString() is { Length: > 0 } code)
            {
                errorCode = code;
            }

            // The FACTS a refusal computed, never its prose (#1135 / issue #424): a booking outside the
            // offered hours sends the hours as data, and the sentence is composed HERE in the reader's own
            // language rather than quoted from an English exception message.
            if (json.TryGetProperty("offered", out var windows) && windows.ValueKind == JsonValueKind.Array)
            {
                offered.AddRange(windows.EnumerateArray()
                    .Where(w => w.TryGetProperty("startsAt", out _) && w.TryGetProperty("endsAt", out _))
                    .Select(w => (w.GetProperty("startsAt").GetDateTimeOffset(), w.GetProperty("endsAt").GetDateTimeOffset())));
            }
        }
        catch
        {
            // No problem body at all (a proxy error page, a connection reset) — fall back to the caller's message,
            // which is already localised at its call site.
            throw new ApiActionException(fallback);
        }

        var sentence = errorCode is null ? fallback : ApiErrorText.For(errorCode);
        throw new ApiActionException(offered.Count > 0
            ? sentence + " " + string.Format(
                Strings.Get("ApiErrSlotNotOfferedHours"),
                SimplArchive.Presentation.OfferedHours.Describe(offered))
            : sentence);
    }

    // As ThrowIfProblemAsync: the machine code, never the server's English `detail` (issue #424).
    public static async Task<string> ErrorMessageAsync(HttpResponseMessage resp, string fallback)
    {
        try
        {
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
            if (json.TryGetProperty("errorCode", out var c) && c.GetString() is { Length: > 0 } code) return ApiErrorText.For(code);
        }
        catch { /* not a problem+json body */ }

        return fallback;
    }

    public static LinkMap? ParseLinks(JsonElement item) => LinkMap.From(item);

    public static string? StrOrNull(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    /// <summary>A content address's bytes and content type, through the one funnel (<see cref="ApiCore.GetContentAsync"/>).</summary>
    public static Task<(byte[] Bytes, string ContentType)> DownloadAsync(string url, CancellationToken cancellationToken = default) =>
        ApiCore.GetContentAsync(url, cancellationToken);
}
