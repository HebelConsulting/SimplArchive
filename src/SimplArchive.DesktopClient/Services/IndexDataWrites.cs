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
    // Duplicate detection (ADRs 0398/0686) — documents whose latest confirmed version is byte-identical to
    // the SHA-256, plus (for an e-mail) those sharing its Message-ID, ACL-filtered. Warns before an upload.
    // Here with the index-data choreography because DocumentsClient is on the standing-debt list and this is
    // the same kind of pre-write wire conversation.
    public static async Task<List<DocumentsClient.DuplicateInfo>> FindDuplicatesAsync(
        this DocumentsClient documents, string hash, string? entryId = null, CancellationToken cancellationToken = default)
    {
        // entryId (#704): an e-mail's Message-ID, so two byte-different copies of one message still meet in
        // the dialog. A query on the advertised href is following it (ADR 0557).
        var query = $"?hash={hash}{(entryId is null ? string.Empty : $"&entryId={Uri.EscapeDataString(entryId)}")}";
        var json = await documents.Core.Http.GetFromJsonAsync<JsonElement>(
            $"{await documents.Core.RootHrefAsync("duplicates", cancellationToken)}{query}", cancellationToken);
        var list = new List<DocumentsClient.DuplicateInfo>();
        if (json.TryGetProperty("duplicates", out var arr))
        {
            foreach (var d in arr.EnumerateArray())
            {
                list.Add(new DocumentsClient.DuplicateInfo(
                    d.GetProperty("id").GetGuid(),
                    d.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    d.TryGetProperty("path", out var p) ? p.GetString() ?? "" : ""));
            }
        }

        return list;
    }

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
