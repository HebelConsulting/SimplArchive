using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.WebDav;

// ---- Special Intray / Check-out areas -----------------------------------------------------------------
// `Created` is separate from `Modified` because the tree reports both and the special areas have to answer
// the same way (#794). An object store keeps no creation time, so for a staged object the two are the same
// value — truthfully, since the write that created it IS its last modification.
internal sealed record SpecialFile(string Name, long Size, DateTimeOffset Modified, Guid DocumentId, string Key, DateTimeOffset Created);

// The per-user S3-backed special areas the WebDAV tree nests under Personal (issue #466 moved this out of
// the middleware): the Intray staging prefix, the Check-out working copies, and the temp/scratch tiers for
// in-progress downloads and editor atomic saves (ADRs 0368/0508/0562). Object-key derivation + listings —
// the HTTP verbs that act on them stay in the middleware.
internal static class WebDavUserAreas
{
    internal static string IntrayPrefix(User user) => $"tenants/{user.TenantId}/users/{user.Id}/inbox/";

    // Per-user temp staging area for in-progress downloads — the same tier as intray/ and checkout/ (ADR 0368).
    internal static string TempPrefix(User user) => $"tenants/{user.TenantId}/users/{user.Id}/temp/";

    // A staged download-temp's object key is derived from its WebDAV path so the PUT (stage) and the later MOVE
    // (commit) resolve the same object across requests. Hashed to keep the key opaque + free of path characters.
    internal static string TempKeyFor(User user, List<string> segments) =>
        TempPrefix(user) + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('/', segments))))
            .ToLowerInvariant();

    internal static async Task<List<SpecialFile>> IntrayFilesAsync(IObjectStorageClient storage, User user)
    {
        var prefix = IntrayPrefix(user);
        var result = new List<SpecialFile>();
        foreach (var obj in await storage.ListObjectsAsync(prefix))
        {
            var name = obj.Key[prefix.Length..];
            if (name.Length == 0 || name.Contains('/') || WebDavClutter.IsIntrayLitter(name) || WebDavClutter.IsOsClutter(name) || WebDavClutter.IsLockFile(name))
            {
                continue; // the prefix placeholder, a nested key, a litter artifact, OS clutter, or a lock file
            }

            result.Add(new SpecialFile(name, obj.Size, obj.LastModified, Guid.Empty, obj.Key, obj.LastModified));
        }

        return result;
    }

    /// <summary>Where the bytes an Intray write is about to replace are kept.</summary>
    /// <remarks>
    /// An Intray item is a bare object: no Document, no versions, no soft-delete — so an overwrite is final in a
    /// way nothing else in this system is. That was not a theoretical concern. A word processor saved correctly,
    /// then six seconds later rolled its own save back by MOVEing an EMPTY backup slot over the file, and 13 KB
    /// of the user's work ceased to exist (#794). Every other surface would have survived this: the tree keeps
    /// version history, and a deleted document is soft-deleted.
    /// </remarks>
    internal static string IntrayPreviousPrefix(User user) => $"tenants/{user.TenantId}/users/{user.Id}/inbox-previous/";

    /// <summary>Copy aside whatever an Intray write is about to replace, so the replacement cannot be final.</summary>
    /// <remarks>
    /// <para>
    /// Called before every write that can replace an Intray item, and deliberately NOT a refusal: truncating a
    /// file to nothing is legitimate, and a gateway that second-guesses its client's writes trades one defect
    /// for another. What it must not do is make the client's mistake unrecoverable.
    /// </para>
    /// <para>
    /// Only NON-EMPTY outgoing bytes are kept, and only under the item's own name — so a run of overwrites keeps
    /// the last real content rather than accumulating a copy per keystroke-triggered autosave.
    /// </para>
    /// </remarks>
    internal static async Task PreserveIntrayBytesAsync(
        IObjectStorageClient storage, ILogger logger, User user, string name, CancellationToken cancellationToken)
    {
        var key = IntrayPrefix(user) + name;
        if (!await storage.ExistsAsync(key, cancellationToken) || await storage.GetObjectSizeAsync(key) == 0)
        {
            return; // nothing to lose
        }

        var previous = IntrayPreviousPrefix(user) + name;
        await storage.CopyObjectAsync(key, previous, cancellationToken);

        // The user cannot see that this happened, and if the write turns out to be a rollback they will see only
        // that their work is gone (ADR 0626: name what we did silently, and where the evidence is).
        logger.LogWarning(
            "Intray item {Item} is being overwritten; the previous bytes were copied to {PreviousKey}. "
            + "The Intray has no version history, so this copy is the only way back. Turn Trace on for "
            + "SimplArchive.Api.WebDav to see the exchange that caused it.",
            name, previous);
    }

    internal static string CheckoutScratchPrefix(User user) => $"tenants/{user.TenantId}/users/{user.Id}/checkout-scratch/";

    internal static async Task<List<SpecialFile>> CheckoutScratchFilesAsync(IObjectStorageClient storage, User user)
    {
        var prefix = CheckoutScratchPrefix(user);
        var result = new List<SpecialFile>();
        foreach (var obj in await storage.ListObjectsAsync(prefix))
        {
            var name = obj.Key[prefix.Length..];
            if (name.Length == 0 || name.Contains('/') || WebDavClutter.IsLockFile(name))
            {
                continue; // the prefix placeholder, a nested key, or a hidden lock/owner file
            }

            result.Add(new SpecialFile(name, obj.Size, obj.LastModified, Guid.Empty, obj.Key, obj.LastModified));
        }

        return result;
    }

    // The files a special folder exposes over WebDAV: the Intray's staged objects, or the Check-out's checked-out
    // documents PLUS any in-flight atomic-save scratch temps (ADR 0508).
    internal static async Task<List<SpecialFile>> SpecialFolderFilesAsync(IObjectStorageClient storage, SimplArchiveDbContext db, User user, string folder)
    {
        if (folder == WebDavMiddleware.IntrayName)
        {
            return await IntrayFilesAsync(storage, user);
        }

        var files = await CheckoutFilesAsync(storage, db, user);
        files.AddRange(await CheckoutScratchFilesAsync(storage, user));
        return files;
    }

    internal static async Task<List<SpecialFile>> CheckoutFilesAsync(IObjectStorageClient storage, SimplArchiveDbContext db, User user)
    {
        var checkedOut = await db.Documents.Where(d => d.CheckedOutByUserId == user.Id).ToListAsync();
        var result = new List<SpecialFile>();
        foreach (var doc in checkedOut)
        {
            // Current version honoring the CurrentVersionId pointer (issue #265), else latest confirmed.
            var version = await CurrentVersion.ResolveAsync(db.DocumentVersions, doc.Id, doc.CurrentVersionId);
            if (version is null)
            {
                continue;
            }

            var stashKey = CheckoutStashKey.Build(user.TenantId, user.Id, doc.Id);
            var hasStash = await storage.ExistsAsync(stashKey);
            var key = hasStash ? stashKey : version.ObjectKey;
            var size = hasStash ? await storage.GetObjectSizeAsync(stashKey) : version.SizeBytes ?? 0;
            var name = doc.Name + Path.GetExtension(version.ObjectKey);
            result.Add(new SpecialFile(name, size, doc.CheckedOutAt ?? doc.CreatedAt, doc.Id, key, doc.CreatedAt));
        }

        return result;
    }

    // PROPFIND for the special Personal/Intray and Personal/Check-out folders (segments = [Personal, folder, file?]).
    // Resolves a single file inside a special (Intray / Check-out) folder for GET/HEAD/PROPFIND. Beyond the listed
    // files, this also resolves the hidden lock/owner sidecars (.~lock.name# / ~$name) directly from the store —
    // they're kept out of the folder LISTING (so they don't clutter the view) but MUST round-trip, or LibreOffice /
    // Office read back their own just-PUT lock file, get 404, and revert the document to read-only (ADR 0513).
    /// <param name="segments">
    /// The full request path, needed only to find a remembered write in the shadow area (#794). Optional so
    /// callers that cannot have it still resolve everything else.
    /// </param>
    internal static async Task<SpecialFile?> ResolveSpecialFileAsync(IObjectStorageClient storage, SimplArchiveDbContext db, User user, string folder, string name, IReadOnlyList<string>? segments = null)
    {
        var files = await SpecialFolderFilesAsync(storage, db, user, folder);
        if (files.FirstOrDefault(f => f.Name == name) is { } listed)
        {
            return listed;
        }

        if (WebDavClutter.IsLockFile(name) && !name.Contains('/'))
        {
            var key = folder == WebDavMiddleware.IntrayName ? IntrayPrefix(user) + name : CheckoutScratchPrefix(user) + name;
            if (await storage.ExistsAsync(key))
            {
                return new SpecialFile(name, await storage.GetObjectSizeAsync(key), DateTimeOffset.UtcNow, Guid.Empty, key, DateTimeOffset.UtcNow);
            }
        }

        // OS clutter never becomes an item here, but it is REMEMBERED (#794) — and a write we answered 201 to
        // must be readable back, or accepting it was a lie. Storing it on PUT while GET and PROPFIND still
        // answered 404 is what left an editor rewriting its sidecar and never reaching the document at all.
        if (segments is { Count: > 0 } && WebDavClutter.IsOsClutter(name))
        {
            var shadow = WebDavSafeSave.ShadowKey(user, segments);
            if (await storage.ExistsAsync(shadow))
            {
                return new SpecialFile(name, await storage.GetObjectSizeAsync(shadow), DateTimeOffset.UtcNow, Guid.Empty, shadow, DateTimeOffset.UtcNow);
            }
        }

        return null;
    }

}
