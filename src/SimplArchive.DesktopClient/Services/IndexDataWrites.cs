using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// The index-data PUT and its duplicate-claim choreography (#703), as an extension on
/// <see cref="DocumentsClient"/> — same call sites, its own file, because the client is on the 1000-line
/// standing-debt list and this method GREW there (the ask-and-retry moved in from the view-model, which was
/// the right direction; the file was just the wrong room).
/// </summary>
public static class IndexDataWrites
{
    // Replaces the whole index-data set. 400 FIELD_VALUE_INVALID / MULTIPLE_VALUES_NOT_ALLOWED surface as a message.
    //
    // A 409 DUPLICATE_ADDRESS_CLAIM (#703) is a QUESTION, not a failure: the response's `claimedBy` extension
    // names the mailbox already holding the address — as DATA, so this composes a LOCALIZED question instead
    // of surfacing the server's English prose (issue #424) — and `confirmDuplicateClaim` asks it. Yes retries
    // with the confirmation; no (or no asker wired) throws DuplicateAddressClaimException for the caller's
    // failure list. The ask-and-retry lives HERE, not in the view-model, because it is wire choreography.
    public static async Task SetIndexDataAsync(
        this DocumentsClient documents, string indexDataHref, IEnumerable<(Guid FieldDefinitionId, IReadOnlyList<string> Values)> fields,
        Func<string, Task<bool>>? confirmDuplicateClaim = null, CancellationToken cancellationToken = default)
    {
        var groups = fields.Select(f => new { fieldDefinitionId = f.FieldDefinitionId, values = f.Values }).ToList();
        var response = await documents.Core.Http.PutAsJsonAsync(indexDataHref, new { fields = groups, confirmDuplicateClaims = false }, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        // The problem body is read ONCE and every branch works from the parse — probing for one code and then
        // handing the response to ThrowIfProblemAsync consumed the stream, so every OTHER refusal (a legal
        // hold, a required field) fell to the generic fallback. Found by DesktopApiErrorLocalizationTests.
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
            throw new ApiActionException("Could not save the index data");
        }

        if (errorCode == "DUPLICATE_ADDRESS_CLAIM")
        {
            var question = string.Format(SimplArchive.Localization.Strings.Get("DupClaimBody"), claimedBy ?? "?");
            if (confirmDuplicateClaim is null || !await confirmDuplicateClaim(question))
            {
                throw new DuplicateAddressClaimException(question);
            }

            var retry = await documents.Core.Http.PutAsJsonAsync(indexDataHref, new { fields = groups, confirmDuplicateClaims = true }, cancellationToken);
            await ApiCore.ThrowIfProblemAsync(retry, "Could not save the index data", cancellationToken);
            return;
        }

        throw new ApiActionException(errorCode is null
            ? "Could not save the index data"
            : SimplArchive.Localization.ApiErrorText.For(errorCode));
    }
}
