using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// The check-out area's client (#443, tranche 1 — the first area peeled off <c>SimplArchiveApiClient</c>):
/// the caller's checked-out documents, the working-copy stash, and the lock lifecycle, all over the shared
/// authenticated <see cref="ApiCore"/>. Reached as <c>api.Checkout</c>.
/// </summary>
/// <remarks>
/// <para>Lock acquisition/release take the ADVERTISED href (<c>checkout</c> / <c>cancel-checkout</c> from the
/// document resource or a row that carries it) rather than a document id — the id-shaped surface is what let
/// the one composed-URL exception survive (#443 half B): a caller holds a row, and the row states the
/// address.</para>
/// <para>Shared wire shapes that several areas read (<c>Preview</c>, <c>VersionComparison</c>) stay on
/// <see cref="SimplArchiveApiClient"/> until their own area moves.</para>
/// </remarks>
public sealed class CheckoutClient(ApiCore core)
{
    private readonly ApiCore _core = core;

    public async Task<Preview?> GetCheckoutPreviewAsync(CheckoutItem checkout, CancellationToken cancellationToken = default)
    {
        if (checkout.Href("preview") is not { } href)
        {
            return null;
        }

        using var response = await _core.Http.GetAsync(href, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent || !response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        // No text layout / pages / annotations: those belong to an archived VERSION, and a working copy is not
        // one yet. The preview is the picture, nothing more.
        return new Preview(
            json.GetProperty("previewUrl").GetString(),
            json.TryGetProperty("previewConverted", out var c) && c.ValueKind == JsonValueKind.True,
            null, null, null, checkout.FileExtension);
    }

    // ---- Check-out / check-in (ADR "Document check-out / check-in") -----------------------------------

    // A held check-out, carrying the addresses its own row advertised (ADR 0543/0555): `checkin`,
    // `working-copy`, `extend` and — only when there is a stash to diff — `compare`.
    // ImplicitAgent: the client that took this lock without the user asking — a save-by-rename edit over the
    // WebDAV mount (ADR 0562); null for an explicit check-out. Client-supplied text: display it, never act on it.

    public sealed record CheckoutItem(Guid Id, string Name, string Path, string Sha256, string FileExtension, bool HasStash, bool IsModified, string? StashDownloadUrl, DateTimeOffset? ExpiresAt, IReadOnlyDictionary<string, string>? Links = null, string? ImplicitAgent = null, bool? IsSigned = null, string? DownloadUrl = null)
    {
        public string? Href(string rel) => Links is not null && Links.TryGetValue(rel, out var href) ? href : null;
    }

    // Acquire the exclusive edit lock. 409 = already held by someone else; 403 = no permission / not a User.

    /// <summary>
    /// Acquires the lock starting from a LISTING row's advertised `self`: one fetch of the resource, then
    /// the `checkout` rel it offers (ADR 0559 — a listing advertises what browsing needs; the definitive
    /// affordance is the resource's own answer). A withheld rel means not available to this caller here.
    /// </summary>
    public async Task CheckOutViaDocumentAsync(string documentSelfHref, CancellationToken cancellationToken = default) =>
        await CheckOutAsync(await ResolveDocumentRelAsync(documentSelfHref, "checkout", cancellationToken), cancellationToken);

    /// <summary>Releases a lock (unlock / override) from a listing row's `self` — the same fetch-then-follow.</summary>
    public async Task CheckInViaDocumentAsync(string documentSelfHref, CancellationToken cancellationToken = default) =>
        await CheckInAsync(await ResolveDocumentRelAsync(documentSelfHref, "cancel-checkout", cancellationToken), cancellationToken);

    /// <summary>Releases a held check-out from ITS row — the address the checkouts listing advertised.</summary>
    public Task CheckInAsync(CheckoutItem checkout, CancellationToken cancellationToken = default) =>
        CheckInAsync(RequireHref(checkout, "cancel-checkout"), cancellationToken);

    private async Task<string> ResolveDocumentRelAsync(string documentSelfHref, string rel, CancellationToken cancellationToken)
    {
        var document = await _core.Http.GetFromJsonAsync<JsonElement>(documentSelfHref, cancellationToken);
        var links = ApiCore.ParseLinks(document);
        return links is not null && links.TryGetValue(rel, out var href)
            ? href
            : throw new ApiActionException(rel == "checkout"
                ? "You don't have permission to check out this document."
                : "You don't have permission to release this check-out.");
    }


    public async Task CheckOutAsync(string checkoutHref, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.PutAsync(checkoutHref, null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new ApiActionException("This document is already checked out by another user.");
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to check out this document.");
        }

        response.EnsureSuccessStatusCode();
    }

    // Release the lock — used for check-in / unlock / discard (the holder) and override (a CanOverrideCheckout
    // holder force-releasing someone else's). Idempotent when not checked out.

    public async Task CheckInAsync(string cancelCheckoutHref, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.DeleteAsync(cancelCheckoutHref, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to release this check-out.");
        }

        response.EnsureSuccessStatusCode();
    }

    // Stash-based check-in (ADR 0513): the server promotes the cloud stash (the WebDAV-edited working copy) to a new
    // confirmed version and releases the lock — the desktop no longer uploads a local file. Holder-only; 400 if
    // there's no stash to check in (nothing changed).

    public async Task CheckInFromStashAsync(CheckoutItem checkout, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.PostAsJsonAsync(RequireHref(checkout, "checkin"), new { }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to check in this document.");
        }

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            throw new ApiActionException("There are no changes to check in.");
        }

        response.EnsureSuccessStatusCode();
    }

    // "Extend my check-out" (ADR "Self-service check-out extension") — resets the auto-release idle timer. The
    // holder or a CanOverrideCheckout admin; 409 if the document isn't checked out.

    public async Task ExtendCheckoutAsync(CheckoutItem checkout, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.PostAsync(RequireHref(checkout, "extend"), null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to extend this check-out.");
        }

        response.EnsureSuccessStatusCode();
    }

    // The caller's currently checked-out documents (tenant-wide), each with the current version's SHA-256.

    public async Task<List<CheckoutItem>> GetCheckoutsAsync(CancellationToken cancellationToken = default)
    {
        var json = await _core.Http.GetFromJsonAsync<JsonElement>(await _core.RootHrefAsync("checkouts", cancellationToken), cancellationToken);
        var items = new List<CheckoutItem>();
        if (json.TryGetProperty("items", out var arr))
        {
            foreach (var i in arr.EnumerateArray())
            {
                items.Add(new CheckoutItem(
                    i.GetProperty("id").GetGuid(), i.GetProperty("name").GetString() ?? "",
                    i.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "",
                    i.TryGetProperty("sha256", out var s) ? s.GetString() ?? "" : "",
                    i.TryGetProperty("fileExtension", out var fe) ? fe.GetString() ?? "" : "",
                    i.TryGetProperty("hasStash", out var hst) && hst.ValueKind == JsonValueKind.True,
                    i.TryGetProperty("isModified", out var im) && im.ValueKind == JsonValueKind.True,
                    i.TryGetProperty("stashDownloadUrl", out var sdu) && sdu.ValueKind == JsonValueKind.String ? sdu.GetString() : null,
                    i.TryGetProperty("expiresAt", out var ea) && ea.ValueKind == JsonValueKind.String ? ea.GetDateTimeOffset() : null,
                    ApiCore.ParseLinks(i),
                    i.TryGetProperty("implicitAgent", out var ia) && ia.ValueKind == JsonValueKind.String ? ia.GetString() : null,
                    // Tri-state: absent means never examined (#491), which is not the same as "not signed".
                    i.TryGetProperty("isSigned", out var sg) && sg.ValueKind is JsonValueKind.True or JsonValueKind.False
                        ? sg.GetBoolean()
                        : null,
                    SimplArchiveApiClient.StrOrNull(i, "downloadUrl")));
            }
        }

        return items;
    }

    // "Save to cloud" — uploads the in-progress working copy to the S3 stash so it survives logout/close and is
    // re-downloaded on next login (ADR "Check-out working-copy stash + exit guard"). Holder-only server-side.

    public async Task SaveWorkingCopyAsync(CheckoutItem checkout, byte[] bytes, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.PostAsJsonAsync(RequireHref(checkout, "working-copy"), new { }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't hold the check-out on this document.");
        }

        response.EnsureSuccessStatusCode();
        var uploadUrl = (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("uploadUrl").GetString()!;

        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var upload = await ApiCore.Anonymous.PutAsync(uploadUrl, content, cancellationToken);
        upload.EnsureSuccessStatusCode();
    }

    // Downloads the cloud working-copy stash bytes (restoring in-progress edits on login).

    public async Task<byte[]> DownloadStashAsync(string stashDownloadUrl, CancellationToken cancellationToken = default)
    {
        var (bytes, _) = await SimplArchiveApiClient.DownloadAsync(stashDownloadUrl, cancellationToken);
        return bytes;
    }

    // Downloads the current confirmed version's bytes (for writing to the local checkout working copy).

    private static string RequireHref(CheckoutItem checkout, string rel) =>
        checkout.Href(rel)
        ?? throw new InvalidOperationException($"The check-out on '{checkout.Name}' advertised no '{rel}' rel — `compare` is absent with no stash to diff (ADR 0543/0555).");

    public async Task<VersionComparison> GetCheckoutComparisonAsync(CheckoutItem checkout, CancellationToken cancellationToken = default)
    {
        var json = await _core.Http.GetFromJsonAsync<JsonElement>(RequireHref(checkout, "compare"), cancellationToken);
        return VersionComparison.Parse(json);
    }
}
