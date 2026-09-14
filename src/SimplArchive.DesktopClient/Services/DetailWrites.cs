using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// The document detail read and its one-request save (ADRs 0794/0796), as an extension on
/// <see cref="DocumentsClient"/> — its own file, because that client is on the 1000-line standing-debt list.
/// </summary>
/// <remarks>
/// The pencil commits ONE logical edit, and this client used to turn it into up to eight independent writes
/// with no transaction and, for seven of them, no precondition — so a refusal part-way left a half-saved
/// document. Now it is one PUT, guarded by the tag the form was LOADED with.
///
/// The duplicate-claim ask-and-retry lives here for the same reason it lives in <see cref="IndexDataWrites"/>:
/// it is wire choreography, not view-model logic. It gets strictly better in one request — the attempt that
/// asks the question writes NOTHING, so the retry is simply the same request with the flag set, where the
/// per-aspect path had already committed the earlier fields and leaned on change detection to skip them.
/// </remarks>
public static class DetailWrites
{
    /// <summary>The detail as it stands, with the tag a save of it will be measured against.</summary>
    public static async Task<(JsonElement Detail, string? Etag)> GetDetailAsync(
        this DocumentsClient documents, string detailHref, CancellationToken cancellationToken = default)
    {
        using var response = await documents.Core.Http.GetAsync(detailHref, cancellationToken);
        await ApiCore.ThrowIfProblemAsync(response, "Could not read the document detail", cancellationToken);

        return (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken), response.Headers.ETag?.Tag);
    }

    /// <summary>
    /// Replaces the whole detail in one request. Returns the saved detail, so the pane adopts what the server
    /// actually stored rather than what it hoped it had sent.
    /// </summary>
    public static async Task<JsonElement> SaveDetailAsync(
        this DocumentsClient documents,
        string detailHref,
        Func<bool, object> body,
        string? etag,
        Func<string, Task<bool>>? confirmDuplicateClaim = null,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(documents, detailHref, body(false), etag, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        }

        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            // Somebody else wrote while this form was open. Reachable for the first time: the precondition is
            // the tag the form was LOADED with, where the old per-aspect rename re-read it immediately before
            // writing and so asked whether anything had changed in the last few milliseconds.
            throw new DetailChangedElsewhereException(
                SimplArchive.Localization.Strings.Get("SaveFailChangedElsewhere"));
        }

        // The problem body is read ONCE and every branch works from the parse — probing for one code and then
        // handing the response on consumes the stream, which is how every OTHER refusal once fell through to a
        // generic message (the bug IndexDataWrites records).
        string? errorCode = null;
        string? claimedBy = null;
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            errorCode = problem.TryGetProperty("errorCode", out var code) ? code.GetString() : null;
            claimedBy = problem.TryGetProperty("claimedBy", out var c) ? c.GetString() : null;
        }
        catch
        {
            throw new ApiActionException(SimplArchive.Localization.Strings.Get("SaveFailSave"));
        }

        if (errorCode == "DUPLICATE_ADDRESS_CLAIM")
        {
            // Composed HERE from the claimedBy extension, as DATA — never the server's English prose (#424).
            var question = string.Format(SimplArchive.Localization.Strings.Get("DupClaimBody"), claimedBy ?? "?");
            if (confirmDuplicateClaim is null || !await confirmDuplicateClaim(question))
            {
                throw new DuplicateAddressClaimException(question);
            }

            var retry = await SendAsync(documents, detailHref, body(true), etag, cancellationToken);
            await ApiCore.ThrowIfProblemAsync(retry, SimplArchive.Localization.Strings.Get("SaveFailSave"), cancellationToken);

            return await retry.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        }

        throw new ApiActionException(errorCode is null
            ? SimplArchive.Localization.Strings.Get("SaveFailSave")
            : SimplArchive.Localization.ApiErrorText.For(errorCode));
    }

    private static async Task<HttpResponseMessage> SendAsync(
        DocumentsClient documents, string detailHref, object body, string? etag, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, detailHref) { Content = JsonContent.Create(body) };
        if (etag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", etag);
        }

        return await documents.Core.Http.SendAsync(request, cancellationToken);
    }
}
