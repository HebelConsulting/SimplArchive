using SimplArchive.Api.Checkouts;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Users;

namespace SimplArchive.Api.WebDav;

/// <summary>
/// A word processor's atomic-save collection (<c>&lt;file&gt;.sb-&lt;hex&gt;-&lt;rand&gt;</c>), served as a real
/// working directory backed by the per-user scratch area rather than by the archive (#762, ADR 0707).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a directory and not a discard.</b> Three cheaper answers were tried and each failed on the wire.
/// Refusing the MKCOL made the editor roll back and DELETE THE ORIGINAL. Materialising it left version-less
/// documents drawn as phantom folders. Accepting and discarding it broke the very next request — and then,
/// with writes staged but invisible, produced <i>"Word cannot complete the save due to a file permission
/// error"</i>: we took the PUT and answered 404 when asked whether the file was there, so the editor concluded
/// it could not write.
/// </para>
/// <para>
/// The measured sequence is an ordinary directory session, and nothing less will satisfy it:
/// <c>PROPFIND</c> candidate names until one is free → <c>MKCOL</c> → <c>PUT</c>/<c>LOCK</c>/<c>PROPFIND</c>
/// the files inside → swap → <c>DELETE</c>. So the collection has to EXIST for the life of the save, and
/// everything written into it has to be visible.
/// </para>
/// <para>
/// It exists in object storage only. Nothing here creates a Document, so no phantom folder can appear in the
/// tree, and an abandoned save leaves orphaned objects under a per-user prefix rather than rows in the archive.
/// </para>
/// </remarks>
internal static class WebDavSafeSave
{
    /// <summary>Everything one user has in flight; a sibling of the intray/checkout tiers.</summary>
    private static string Prefix(User user) => $"tenants/{user.TenantId}/users/{user.Id}/safe-save/";

    /// <summary>The marker written at MKCOL, which is what makes the collection EXIST before anything is in it.</summary>
    private const string Marker = ".collection";

    /// <summary>
    /// The storage prefix for one collection, keyed by its FULL path.
    /// </summary>
    /// <remarks>
    /// The full path, not the leaf: two documents being saved at once in different folders can produce
    /// collections whose names collide only by their random tail, and a leaf-keyed area would let one save's
    /// files appear inside another's listing.
    /// </remarks>
    internal static string CollectionPrefix(User user, IReadOnlyList<string> segments)
    {
        var upTo = CollectionDepth(segments);
        var path = string.Join('/', segments.Take(upTo));
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path))).ToLowerInvariant();
        return $"{Prefix(user)}{hash}/";
    }

    /// <summary>How many segments make up the collection itself (everything after it is inside it).</summary>
    private static int CollectionDepth(IReadOnlyList<string> segments)
    {
        for (var i = 0; i < segments.Count; i++)
        {
            if (WebDavClutter.IsSafeSaveTemp(segments[i]))
            {
                return i + 1;
            }
        }

        return segments.Count;
    }

    /// <summary>True when the path addresses the collection itself rather than something inside it.</summary>
    internal static bool IsCollectionItself(IReadOnlyList<string> segments) =>
        segments.Count > 0 && WebDavClutter.IsSafeSaveTemp(segments[^1]);

    /// <summary>The storage key of a file inside the collection. Nesting is flattened onto the leaf name.</summary>
    internal static string FileKey(User user, IReadOnlyList<string> segments) =>
        CollectionPrefix(user, segments) + segments[^1];

    // ---- The shadow filesystem -----------------------------------------------------------------------------
    //
    // Anything we ACCEPT but decline to file has to be readable back, or accepting it was a lie. OS clutter was
    // answered 201 and thrown away, and the wire shows what that costs: an editor wrote `._<name>`, asked for it,
    // got 404, wrote it AGAIN (201 rather than 204 — proof nothing was kept), asked again, and gave up with
    // "Word cannot complete the save due to a file permission error". The same shape as the safe-save collection,
    // one file-class over.
    //
    // Kept per user and per PATH, outside the archive: no Document is created, so none of it reaches the tree,
    // the search index or anyone else's view. It is a scratch surface the client owns for the life of its work.

    private static string ShadowPrefix(User user) => $"tenants/{user.TenantId}/users/{user.Id}/shadow/";

    /// <summary>The key under which a swallowed path is remembered.</summary>
    internal static string ShadowKey(User user, IReadOnlyList<string> segments)
    {
        var path = string.Join('/', segments);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path))).ToLowerInvariant();
        return ShadowPrefix(user) + hash;
    }

    internal static Task<bool> ExistsAsync(IObjectStorageClient storage, User user, IReadOnlyList<string> segments, CancellationToken cancellationToken) =>
        storage.ExistsAsync(IsCollectionItself(segments) ? CollectionPrefix(user, segments) + Marker : FileKey(user, segments), cancellationToken);

    /// <summary>MKCOL: record the collection so every later verb can see it.</summary>
    internal static Task CreateAsync(IObjectStorageClient storage, User user, IReadOnlyList<string> segments, CancellationToken cancellationToken) =>
        storage.PutObjectAsync(CollectionPrefix(user, segments) + Marker, new MemoryStream([]), "application/octet-stream", cancellationToken);

    /// <summary>The files staged inside the collection, marker excluded.</summary>
    internal static async Task<List<(string Name, long Size, DateTimeOffset Modified)>> FilesAsync(
        IObjectStorageClient storage, User user, IReadOnlyList<string> segments)
    {
        var prefix = CollectionPrefix(user, segments);
        var files = new List<(string, long, DateTimeOffset)>();
        foreach (var obj in await storage.ListObjectsAsync(prefix))
        {
            var name = obj.Key[prefix.Length..];
            if (name.Length == 0 || name == Marker)
            {
                continue;
            }

            files.Add((name, obj.Size, obj.LastModified));
        }

        return files;
    }

    /// <summary>DELETE: drop the whole collection, marker and staged files alike.</summary>
    internal static async Task RemoveAsync(IObjectStorageClient storage, User user, IReadOnlyList<string> segments, CancellationToken cancellationToken)
    {
        var prefix = CollectionPrefix(user, segments);
        foreach (var obj in await storage.ListObjectsAsync(prefix))
        {
            await storage.DeleteObjectAsync(obj.Key, cancellationToken);
        }
    }

    // ---- The caller's working copy ------------------------------------------------------------------------

    /// <summary>The size a node should REPORT: the caller's working copy when they hold the check-out.</summary>
    /// <remarks>
    /// The listing and the download have to agree. Serving the stash from GET while PROPFIND still reported the
    /// checked-in version's length is what leaves Finder showing <b>0 bytes</b> for a file whose content
    /// downloads perfectly — the same contradiction as every other defect in #762, in its last hiding place.
    /// So one rule, asked in both places.
    /// </remarks>
    internal static async Task<long?> WorkingCopySizeAsync(
        IObjectStorageClient storage, User user, Guid? documentId, Guid? checkedOutBy, CancellationToken cancellationToken)
    {
        if (documentId is not { } id || checkedOutBy != user.Id)
        {
            return null;
        }

        var key = CheckoutStashKey.Build(user.TenantId, user.Id, id);
        return await storage.ExistsAsync(key, cancellationToken) ? await storage.GetObjectSizeAsync(key) : null;
    }
}
