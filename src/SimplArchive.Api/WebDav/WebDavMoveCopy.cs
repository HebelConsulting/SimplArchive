using System.Globalization;
using System.Net;
using System.Text;
using System.Xml;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Documents;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;


namespace SimplArchive.Api.WebDav;

/// <summary>
/// The WebDAV namespace verbs: MOVE and COPY (RFC 4918 §9.8–9.9), plus the recursive subtree copy.
/// </summary>
/// <remarks>
/// One family because they share the Destination header, the overwrite rules and the same lock checks on BOTH
/// ends. This is also where the 2026-08 interop work landed, which is what took the middleware from 964 back
/// over 1,600 (#909).
/// </remarks>
internal static class WebDavMoveCopy
{
    // ---- MOVE (reparent + rename) -------------------------------------------------------------------------
    internal static async Task HandleMoveAsync(WebDavLockStore lockStore, HttpContext context, IServiceProvider services, SimplArchiveDbContext db, User user, List<string> segments)
    {
        // An atomic save in the INTRAY, whose two halves both cross the special-path boundary (#794). Handled
        // ahead of the divert below, because the source or the destination is an ordinary Intray item while the
        // other side lives in the scratch collection — and the flat-folder handler refuses anything deeper.
        if (await WebDavSpecialHandlers.TryIntraySafeSaveMoveAsync(context, services, db, user, segments))
        {
            return;
        }

        if (WebDavMiddleware.IsSpecialPath(context, segments))
        {
            await WebDavSpecialHandlers.HandleSpecialRenameAsync(context, services, db, user, segments, keepSource: false); // atomic-save rename within Intray/Check-out (ADR 0508)
            return;
        }

        if (WebDavLockHandling.IsLocked(lockStore, context, user, segments))
        {
            context.Response.StatusCode = StatusCodes.Status423Locked;
            return;
        }

        // Commit-on-rename: a browser finishing a download renames its in-progress temp (X.crdownload) to the
        // final name — if the source is a staged download-temp, materialize the real document from the staged
        // bytes (ADR "WebDAV .crdownload staging"). Falls through to a normal move when nothing is staged.
        if (WebDavClutter.IsDownloadTemp(segments[^1]) && await WebDavSpecialHandlers.TryCommitDownloadTempAsync(context, services, db, user, segments))
        {
            return;
        }

        // The safe-save swap. The staged file lives under the collection's own area, so its bytes are bridged
        // into the scratch key the implicit-checkout commit already looks for — one commit path serves both
        // atomic-save shapes, rather than a second one that would drift from it.
        //
        // Only when something is actually staged; otherwise this falls through and an ordinary move happens.
        // The SET-ASIDE, which is how macOS actually begins an atomic replace: it moves the ORIGINAL into the
        // scratch collection as a backup (`…/~WRL0328`) and then renames the new content into the vacated name.
        // Measured (#762) — and the opposite direction from the one this code was built for:
        //
        //     MOVE /…/Test_xxx.docx  Destination: /…/Test_xxx.docx.sb-…-KXPK2o/~WRL0328  → 409
        //     DELETE /…/Test_xxx.docx                                                    → 204
        //
        // The 409 came from the destination's parent not being a real folder, and the line after it is the cost:
        // refused the backup, macOS concluded the save had failed and DELETED THE FILE. "The file disappeared."
        //
        // Accepted as a no-op that KEEPS the document where it is. There is nothing to back up — a replace here
        // becomes a new version, and the previous bytes stay reachable through version history, which is a
        // better backup than a copy in a temp folder. The name is remembered so a later read of it answers.
        if (ParseDestination(context) is { Count: > 0 } setAside && WebDavClutter.IsUnderSafeSaveTemp(setAside)
            && !WebDavClutter.IsUnderSafeSaveTemp(segments))
        {
            var asideStorage = services.GetRequiredService<IObjectStorageClient>();
            var asideKey = WebDavSafeSave.FileKey(user, setAside);
            var source = await WebDavPathResolver.ResolveAsync(db, user, segments);

            // The backup must CONTAIN the document, not merely exist. Writing an empty marker here was fine for
            // a new file — the placeholder was empty anyway — and fatal for editing one in place: Word reads its
            // backup back before continuing, and measured (#762) it got `200, Content-Length: 0`, so it stopped
            // rather than destroy the original. That refusal is correct of it and the empty backup was wrong of
            // us. The document itself still stays where it is; version history is our backup, and this copy is
            // the one the editor insists on seeing.
            if (source is { IsCollection: false, ObjectKey: { } sourceKey })
            {
                var held = source.Document is { CheckedOutByUserId: { } by } && by == user.Id
                    ? CheckoutStashKey.Build(user.TenantId, user.Id, source.Document.Id)
                    : null;
                var from = held is not null && await asideStorage.ExistsAsync(held, context.RequestAborted) ? held : sourceKey;
                await asideStorage.CopyObjectAsync(from, asideKey, context.RequestAborted);
            }
            else
            {
                await asideStorage.PutObjectAsync(asideKey, new MemoryStream([]), "application/octet-stream", context.RequestAborted);
            }

            // The move is now ANSWERED, not merely accepted: the document keeps its row and its place in the
            // archive, and the mount reports the path it left as gone until the swap puts something back there
            // (#794). Before this, 201 was a claim nothing upheld — the editor retitled its own window to the
            // backup name and then stopped writing, because the name it had just freed was still occupied.
            await WebDavSafeSave.MarkSetAsideAsync(asideStorage, user, segments, context.RequestAborted);

            context.Response.StatusCode = StatusCodes.Status201Created;
            return;
        }

        // The safe-save swap. The staged bytes are bridged into the keys the two EXISTING commit helpers look
        // for, so neither's logic is duplicated here — one of them serves a REPLACE (implicit check-out onto an
        // existing document, ADR 0562) and the other a CREATE.
        //
        // Both are needed, and only the first was here: TryCommitImplicitCheckoutAsync declines a destination
        // that does not exist ("an ordinary create, handled elsewhere"), and elsewhere resolves the SOURCE as a
        // Document — which a staged file is not. So saving a NEW document through an atomic save had no commit
        // path at all, which is what the wire showed: the target PROPFINDed 404 because it had never existed.
        if (WebDavClutter.IsUnderSafeSaveTemp(segments))
        {
            var safeSaveStorage = services.GetRequiredService<IObjectStorageClient>();
            var stagedKey = WebDavSafeSave.FileKey(user, segments);

            // A sidecar leaving the collection stays OUT of the archive. macOS moves its AppleDouble to the
            // final name alongside the document (`…/.sb-…/._.~WRD3471` → `…/._Test.docx`), and committing THAT
            // minted documents called `._ahfsjishaijf`, `._Line1`, `._The real test` — 4 KB of resource-fork
            // metadata filed as though it were someone's work.
            //
            // The clutter filter decides what may become a document, and it has to decide that on EVERY path in,
            // not just on PUT. A rule enforced at one entrance is not a rule.
            var movedDestination = ParseDestination(context);
            if (movedDestination is { Count: > 0 } && WebDavClutter.IsOsClutter(movedDestination[^1]))
            {
                if (await safeSaveStorage.ExistsAsync(stagedKey, context.RequestAborted))
                {
                    await safeSaveStorage.CopyObjectAsync(
                        stagedKey, WebDavSafeSave.ShadowKey(user, movedDestination), context.RequestAborted);
                    await safeSaveStorage.DeleteObjectAsync(stagedKey, context.RequestAborted);
                }

                context.Response.StatusCode = StatusCodes.Status201Created;
                return;
            }

            // The swap is the other half of the set-aside: something is being put back at the name it emptied,
            // so that name exists again (#794). Cleared before the commit rather than after, so the path is
            // never briefly hidden while it is being written.
            if (movedDestination is { Count: > 0 })
            {
                await WebDavSafeSave.ClearSetAsideAsync(safeSaveStorage, user, movedDestination, context.RequestAborted);
            }

            if (await safeSaveStorage.ExistsAsync(stagedKey, context.RequestAborted))
            {
                await safeSaveStorage.CopyObjectAsync(
                    stagedKey, WebDavUserAreas.CheckoutScratchPrefix(user) + segments[^1], context.RequestAborted);
                await safeSaveStorage.CopyObjectAsync(
                    stagedKey, WebDavUserAreas.TempKeyFor(user, segments), context.RequestAborted);
                await safeSaveStorage.DeleteObjectAsync(stagedKey, context.RequestAborted);

                // EVERY save over WebDAV is a working copy, never a silent version (ADR 0562, reaffirmed for
                // #762). The bytes land in the stash, the document shows on the Check-out tab, and check-in is
                // the deliberate act that mints the next version — including the FIRST save of a newly created
                // document, so the rule has no special case to remember.
                //
                // What makes that honest rather than lossy is that the tree SERVES the stash to its owner (see
                // HandleGetAsync): you read back what you saved. Without that half, this half reads as data
                // loss — the file returns the empty placeholder and the editor appears to have thrown your work
                // away.
                if (await WebDavSpecialHandlers.TryCommitImplicitCheckoutAsync(context, services, db, user, segments))
                {
                    await safeSaveStorage.DeleteObjectAsync(WebDavUserAreas.TempKeyFor(user, segments), context.RequestAborted);
                    return;
                }

                if (await WebDavSpecialHandlers.TryCommitDownloadTempAsync(context, services, db, user, segments))
                {
                    await safeSaveStorage.DeleteObjectAsync(
                        WebDavUserAreas.CheckoutScratchPrefix(user) + segments[^1], context.RequestAborted);
                    return;
                }
            }
        }

        // Commit-on-rename for a save-by-rename edit: the source is a buffered editor temp and the destination is
        // an existing document, so this rename IS the save. Turns it into an implicit check-out with the bytes in
        // the user's stash (ADR 0562) — never a silent new version, and never a second document beside the first.
        if (await WebDavSpecialHandlers.TryCommitImplicitCheckoutAsync(context, services, db, user, segments))
        {
            return;
        }

        var node = await WebDavPathResolver.ResolveAsync(db, user, segments);
        if (node?.Document is not { } document)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (string.IsNullOrEmpty(context.Request.Headers["Destination"].ToString()))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var destSegments = ParseDestination(context);
        if (destSegments is null || destSegments.Count < 2 || WebDavMiddleware.IsSpecialPath(context, destSegments))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden; // no/blank destination, the root, or a special folder
            return;
        }

        if (WebDavLockHandling.IsLocked(lockStore, context, user, destSegments))
        {
            context.Response.StatusCode = StatusCodes.Status423Locked;
            return;
        }

        var newName = Path.GetFileNameWithoutExtension(destSegments[^1]);
        var destParent = await WebDavPathResolver.ResolveAsync(db, user, destSegments[..^1]);
        if (destParent is not { IsCollection: true, Document: { } destParentDoc })
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        // Dragging a message out of the mounted inbox files it, so the bytes move too (#633). A refused
        // crossing is a 409, the same answer the client gets for any placement this server will not make.
        try
        {
            await services.GetRequiredService<Documents.DocumentMover>()
                .RelocateContentForMoveAsync(document.Id, destParentDoc.Id, context.RequestAborted);
        }
        catch (Errors.Exceptions.Documents.CannotFileIntoEphemeralMailException)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        document.Name = newName;
        document.ParentId = destParentDoc.Id;
        try { await db.SaveChangesAsync(context.RequestAborted); }
        catch (InvalidOperationException) { context.Response.StatusCode = StatusCodes.Status409Conflict; return; }

        context.Response.StatusCode = StatusCodes.Status201Created;
    }

    // Parses the Destination header into WebDAV path segments (null when absent/unparseable).
    internal static List<string>? ParseDestination(HttpContext context)
    {
        var destination = context.Request.Headers["Destination"].ToString();
        if (string.IsNullOrEmpty(destination))
        {
            return null;
        }

        var uri = new Uri(destination, UriKind.RelativeOrAbsolute);
        var absolute = uri.IsAbsoluteUri ? uri.AbsolutePath : destination;
        var baseIndex = absolute.IndexOf(WebDavMiddleware.BasePath, StringComparison.OrdinalIgnoreCase);
        var matchedLength = WebDavMiddleware.BasePath.Length;

        var tail = baseIndex >= 0 ? absolute[(baseIndex + matchedLength)..] : absolute;
        return tail.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToList();
    }

    // ---- COPY (duplicate a file or a folder subtree) ------------------------------------------------------
    internal static async Task HandleCopyAsync(WebDavLockStore lockStore, HttpContext context, IServiceProvider services, SimplArchiveDbContext db, User user, List<string> segments)
    {
        if (WebDavMiddleware.IsSpecialPath(context, segments))
        {
            await WebDavSpecialHandlers.HandleSpecialRenameAsync(context, services, db, user, segments, keepSource: true); // atomic-save copy within Intray/Check-out (ADR 0508)
            return;
        }

        var source = await WebDavPathResolver.ResolveAsync(db, user, segments);
        if (source?.Document is not { } sourceDoc)
        {
            context.Response.StatusCode = source is null ? StatusCodes.Status404NotFound : StatusCodes.Status403Forbidden;
            return;
        }

        if (string.IsNullOrEmpty(context.Request.Headers["Destination"].ToString()))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var destSegments = ParseDestination(context);
        if (destSegments is null || destSegments.Count < 2 || WebDavMiddleware.IsSpecialPath(context, destSegments))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (WebDavLockHandling.IsLocked(lockStore, context, user, destSegments))
        {
            context.Response.StatusCode = StatusCodes.Status423Locked;
            return;
        }

        if (!(await WebDavMiddleware.RightsAsync(services, user, sourceDoc.Id)).CanReadContent)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var destParent = await WebDavPathResolver.ResolveAsync(db, user, destSegments[..^1]);
        if (destParent is not { IsCollection: true, Document: { } destParentDoc })
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        if (!(await WebDavMiddleware.RightsAsync(services, user, destParentDoc.Id)).CanCreateSubItems)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // Overwrite defaults to true (T); Overwrite: F fails 412 if the destination already exists.
        var overwrite = !context.Request.Headers["Overwrite"].ToString().Equals("F", StringComparison.OrdinalIgnoreCase);
        var existing = await WebDavPathResolver.ResolveAsync(db, user, destSegments);
        if (existing is not null && !overwrite)
        {
            context.Response.StatusCode = StatusCodes.Status412PreconditionFailed;
            return;
        }

        var storage = services.GetRequiredService<IObjectStorageClient>();
        var finalizer = services.GetRequiredService<DocumentFinalizer>();
        try
        {
            await CopyDocumentAsync(db, storage, finalizer, user, sourceDoc.Id, destParentDoc.Id, Path.GetFileNameWithoutExtension(destSegments[^1]), context.RequestAborted);
        }
        catch (InvalidOperationException)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict; // sibling-name clash
            return;
        }

        context.Response.StatusCode = existing is null ? StatusCodes.Status201Created : StatusCodes.Status204NoContent;
    }

    // Recursively copies a document under destParentId: a file → a new Document + a copy of its current version
    // blob (finalized like an upload); a folder → a new folder + recursed children (keeping their names).
    private static async Task CopyDocumentAsync(SimplArchiveDbContext db, IObjectStorageClient storage, DocumentFinalizer finalizer, User user, Guid sourceId, Guid destParentId, string newName, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        // Copy the source's current version honoring the CurrentVersionId pointer (issue #265), else latest confirmed.
        var sourcePointer = await db.Documents.Where(d => d.Id == sourceId).Select(d => d.CurrentVersionId).FirstOrDefaultAsync(ct);
        var version = await CurrentVersion.ResolveAsync(db.DocumentVersions, sourceId, sourcePointer, ct);

        if (version is null)
        {
            var folder = new Document
            {
                Id = Guid.NewGuid(),
                TenantId = user.TenantId,
                ParentId = destParentId,
                Name = newName,
                MaskVersionId = await FolderMask.CurrentVersionIdAsync(db, ct),
                CreatedByUserId = user.Id,
                CreatedAt = now,
            };
            db.Documents.Add(folder);
            await db.SaveChangesAsync(ct);

            var children = await db.Documents.Where(d => d.ParentId == sourceId).Select(d => new { d.Id, d.Name }).ToListAsync(ct);
            foreach (var child in children)
            {
                await CopyDocumentAsync(db, storage, finalizer, user, child.Id, folder.Id, child.Name, ct);
            }

            return;
        }

        // A copy is a brand-new document, so it groups under a fresh storage folder (ADR 0530): `now` + a new
        // storage folder, the new version id as the leaf.
        var storageFolderId = Guid.NewGuid();
        var newVersionId = Guid.NewGuid();
        var newKey = ObjectKeyBuilder.Build(user.TenantId, now, storageFolderId, newVersionId, Path.GetExtension(version.ObjectKey));
        await storage.CopyObjectAsync(version.ObjectKey, newKey, ct);

        var doc = new Document { Id = Guid.NewGuid(), TenantId = user.TenantId, ParentId = destParentId, Name = newName, CreatedByUserId = user.Id, CreatedAt = now, StorageFolderId = storageFolderId };
        db.Documents.Add(doc);
        await db.SaveChangesAsync(ct);

        var newVersion = new DocumentVersion
        {
            Id = newVersionId,
            DocumentId = doc.Id,
            TenantId = user.TenantId,
            Status = DocumentVersionStatus.Pending,
            ObjectKey = newKey,
            CreatedByUserId = user.Id,
            CreatedAt = now,
            DocumentDate = version.DocumentDate,
        };
        db.DocumentVersions.Add(newVersion);
        await db.SaveChangesAsync(ct);
        await finalizer.FinalizeAsync(newVersion, ct);
    }
}
