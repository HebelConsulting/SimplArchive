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
        return $"{Prefix(user)}{FolderHash(segments.Take(upTo - 1))}/{Hex(segments[upTo - 1])}/";
    }

    /// <summary>One key segment for the folder a scratch entry lives in, one for its own name.</summary>
    /// <remarks>
    /// The earlier scheme hashed the WHOLE path into a single opaque segment, which answered every direct
    /// request and could never answer a LISTING: "which scratch entries live in this folder" is not a question
    /// a full-path hash can take. That gap is what let the folder's Depth-1 answer contradict the direct ones —
    /// the defect this file exists to prevent, one level up (#794). Splitting the key at the folder boundary
    /// keeps it opaque (the folder is still a hash) while making its members enumerable; the entry's own name
    /// is hex-coded rather than hashed because the listing has to give it BACK.
    /// </remarks>
    private static string FolderHash(IEnumerable<string> parentSegments) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join('/', parentSegments)))).ToLowerInvariant();

    private static string Hex(string name) =>
        Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(name)).ToLowerInvariant();

    private static string? UnHex(string coded)
    {
        try
        {
            return System.Text.Encoding.UTF8.GetString(Convert.FromHexString(coded));
        }
        catch (FormatException)
        {
            return null; // a key from an older scheme, or foreign debris — not listable, and not an error
        }
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
    internal static string ShadowKey(User user, IReadOnlyList<string> segments) =>
        $"{ShadowPrefix(user)}{FolderHash(segments.Take(segments.Count - 1))}/{Hex(segments[^1])}";

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
    /// <summary>Where the working-copy bytes a save is about to replace are kept.</summary>
    internal static string PreviousStashKey(User user, Guid documentId) =>
        $"tenants/{user.TenantId}/users/{user.Id}/stash-previous/{documentId:D}";

    /// <summary>Copy aside the working copy a save is about to overwrite, so the overwrite cannot be final.</summary>
    /// <remarks>
    /// <para>
    /// The same net as the Intray's (#794), for the same measured reason, on the surface that needed it just as
    /// much. An editor's rollback moves an EMPTY backup slot onto the document — <c>MOVE …/~WRL2558 → the
    /// document</c>, observed on BOTH this branch and on main, so it is not a regression but a standing hazard.
    /// </para>
    /// <para>
    /// Version history is NOT the answer here, which is the trap. A save-in-place writes the STASH and
    /// deliberately does not create a version (ADR 0562), so the confirmed version is untouched while the user's
    /// actual in-flight work — everything since they last checked in — is what the empty write destroys.
    /// </para>
    /// <para>
    /// Only non-empty outgoing bytes, and only one copy per document, so a run of autosaves keeps the last real
    /// content rather than a copy per keystroke. Nothing is refused: truncating a file is legitimate, and the
    /// commit and the rollback are the same verb carrying different bytes.
    /// </para>
    /// </remarks>
    internal static async Task PreserveWorkingCopyAsync(
        IObjectStorageClient storage, ILogger logger, User user, Guid documentId, string documentName, CancellationToken cancellationToken)
    {
        var stash = CheckoutStashKey.Build(user.TenantId, user.Id, documentId);
        if (!await storage.ExistsAsync(stash, cancellationToken) || await storage.GetObjectSizeAsync(stash) == 0)
        {
            return; // nothing in flight to lose
        }

        var previous = PreviousStashKey(user, documentId);
        await storage.CopyObjectAsync(stash, previous, cancellationToken);

        logger.LogWarning(
            "The working copy of {Document} is being overwritten; the previous bytes were copied to {PreviousKey}. "
            + "A save in place writes no version, so this copy is the only way back to the in-flight edit. "
            + "Turn Trace on for SimplArchive.Api.WebDav to see the exchange that caused it.",
            documentName, previous);
    }

    /// <summary>One of the caller's own in-flight scratch entries in a folder: a save collection or a sidecar.</summary>
    internal sealed record ScratchMember(string Name, bool IsCollection, long Size, DateTimeOffset Modified);

    /// <summary>How long a scratch entry stays LISTED. Direct reads are unaffected — nothing is deleted here.</summary>
    /// <remarks>
    /// An abandoned save's debris should not clutter a folder forever, but its staged bytes may be the only
    /// copy of typed work (#794 recovered 13 KB from exactly such a collection), so aging out of the LISTING is
    /// as far as this goes. The keys stay readable for whoever goes looking.
    /// </remarks>
    internal static readonly TimeSpan ScratchListingLifetime = TimeSpan.FromMinutes(10);

    /// <summary>The caller's own scratch entries in <paramref name="folderSegments"/>, for its listing.</summary>
    /// <remarks>
    /// <para>
    /// This is the other half of accepting a write (ADR 0707), and the half that was missing: every scratch
    /// path answered PROPFIND, GET and LOCK directly while the folder's Depth-1 listing denied it existed.
    /// Measured (#794): mid-save, the OS re-enumerated the folder, its cache dropped the collection the editor
    /// was standing in, and the editor concluded its scratch had vanished — abandoning collection after
    /// collection, never writing the document's bytes into any of them. A listing is a verb too.
    /// </para>
    /// <para>
    /// Per caller by construction: the scratch tiers are per-user, so one user's save debris never appears in a
    /// colleague's listing of the same folder.
    /// </para>
    /// </remarks>
    internal static async Task<List<ScratchMember>> ScratchMembersAsync(
        IObjectStorageClient storage, User user, IReadOnlyList<string> folderSegments)
    {
        var cutoff = DateTimeOffset.UtcNow - ScratchListingLifetime;
        var members = new List<ScratchMember>();

        var shadowPrefix = $"{ShadowPrefix(user)}{FolderHash(folderSegments)}/";
        foreach (var obj in await storage.ListObjectsAsync(shadowPrefix))
        {
            var name = UnHex(obj.Key[shadowPrefix.Length..]);
            if (name is not null && !name.Contains('/') && obj.LastModified >= cutoff)
            {
                members.Add(new ScratchMember(name, IsCollection: false, obj.Size, obj.LastModified));
            }
        }

        var collectionPrefix = $"{Prefix(user)}{FolderHash(folderSegments)}/";
        var newest = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var obj in await storage.ListObjectsAsync(collectionPrefix))
        {
            var coded = obj.Key[collectionPrefix.Length..];
            var slash = coded.IndexOf('/');
            if (slash <= 0)
            {
                continue;
            }

            coded = coded[..slash];
            if (!newest.TryGetValue(coded, out var seen) || obj.LastModified > seen)
            {
                newest[coded] = obj.LastModified;
            }
        }

        foreach (var (coded, modified) in newest)
        {
            var name = UnHex(coded);
            if (name is not null && modified >= cutoff)
            {
                members.Add(new ScratchMember(name, IsCollection: true, 0, modified));
            }
        }

        return members;
    }

    /// <summary>Where a document staged aside by an atomic save is recorded, keyed by the path it left.</summary>
    private static string SetAsidePrefix(User user) => $"tenants/{user.TenantId}/users/{user.Id}/set-aside/";

    /// <summary>
    /// How long a set-aside stays believed before the mount shows the document again.
    /// </summary>
    /// <remarks>
    /// An abandoned save must heal itself. The editor crashing between the set-aside and the swap is the whole
    /// reason this has a clock: without one, a document would be missing from the mounted drive until somebody
    /// noticed and went looking for a scratch key. Generous next to the seconds a real save takes, short next
    /// to a working session.
    /// </remarks>
    internal static readonly TimeSpan SetAsideLifetime = TimeSpan.FromMinutes(10);

    internal static string SetAsideKey(User user, IReadOnlyList<string> segments) =>
        SetAsidePrefix(user) + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(string.Join('/', segments)))).ToLowerInvariant();

    /// <summary>Record that the document at <paramref name="segments"/> has been moved aside by its editor.</summary>
    /// <remarks>
    /// <para>
    /// The set-aside is the FIRST half of a macOS atomic replace: the editor moves the original into its scratch
    /// collection as a backup, then renames the new content into the name that just became free. We answer 201
    /// to that move — refusing it with 409 was measured in #762 to make the editor conclude the save had failed
    /// and DELETE THE ORIGINAL — but we do not re-parent an archived document into a temp folder for it.
    /// </para>
    /// <para>
    /// Answering 201 and then leaving the document exactly where it was is what this marker fixes. A move
    /// reported as done and not done is a lie the client acts on: measured (#794), the editor's own file
    /// identity followed the move — the window retitled itself <c>~WRL0768</c> — the original name never came
    /// free for the new content, and the editor issued no further writes at all. The user's edit was lost while
    /// every response in the exchange was a 2xx.
    /// </para>
    /// <para>
    /// So the mount answers the move honestly — the old path is gone, the bytes are at the backup path — while
    /// the ARCHIVE keeps the row untouched. Nothing leaves the tree, the search index or the app; only the
    /// mounted path is hidden, for the few seconds between the set-aside and the swap.
    /// </para>
    /// </remarks>
    internal static Task MarkSetAsideAsync(
        IObjectStorageClient storage, User user, IReadOnlyList<string> segments, CancellationToken cancellationToken) =>
        storage.PutObjectAsync(SetAsideKey(user, segments), new MemoryStream([]), "application/octet-stream", cancellationToken);

    /// <summary>True while the document at <paramref name="segments"/> is staged aside and must read as gone.</summary>
    internal static async Task<bool> IsSetAsideAsync(
        IObjectStorageClient storage, User user, IReadOnlyList<string> segments, CancellationToken cancellationToken)
    {
        if (segments.Count == 0)
        {
            return false;
        }

        var key = SetAsideKey(user, segments);
        var marker = (await storage.ListObjectsAsync(key)).FirstOrDefault(o => o.Key == key);
        if (marker is null)
        {
            return false;
        }

        // Expired markers are cleared on sight rather than swept: the read that notices is the cheapest place
        // to act, and it means a stale one can never outlive the next request for the path it hides.
        if (marker.LastModified + SetAsideLifetime >= DateTimeOffset.UtcNow)
        {
            return true;
        }

        await storage.DeleteObjectAsync(key, cancellationToken);
        return false;
    }

    /// <summary>The document is back at its own path: the swap landed, or something wrote there directly.</summary>
    internal static async Task ClearSetAsideAsync(
        IObjectStorageClient storage, User user, IReadOnlyList<string> segments, CancellationToken cancellationToken)
    {
        var key = SetAsideKey(user, segments);
        if (await storage.ExistsAsync(key, cancellationToken))
        {
            await storage.DeleteObjectAsync(key, cancellationToken);
        }
    }

    /// <summary>The caller's working copy of a document: its size AND when it was last written.</summary>
    /// <remarks>
    /// Both, not just the size. A save-in-place writes the stash and deliberately does NOT create a version
    /// (ADR 0562), so the DOCUMENT's modified time need not move — and deriving an entity tag from the working
    /// copy's length paired with the document's timestamp made two saves of equal length indistinguishable.
    /// Measured: editing `AAAA` to `BBBB` left the tag at <c>"1787850465-4"</c> across both. An editor asks for
    /// `getetag` to confirm its write landed (#794), and a tag that does not move is worse than an absent one —
    /// it is a positive claim that the file is unchanged.
    /// </remarks>
    internal static async Task<(long Size, DateTimeOffset Modified, string? ETag)?> WorkingCopyAsync(
        IObjectStorageClient storage, User user, Guid? documentId, Guid? checkedOutBy, CancellationToken cancellationToken)
    {
        if (documentId is not { } id || checkedOutBy != user.Id)
        {
            return null;
        }

        var key = CheckoutStashKey.Build(user.TenantId, user.Id, id);
        var stash = (await storage.ListObjectsAsync(key)).FirstOrDefault(o => o.Key == key);
        return stash is null ? null : (stash.Size, stash.LastModified, stash.ETag);
    }
}
