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
/// The WebDAV write verbs that act on one resource: PUT, MKCOL and DELETE.
/// </summary>
/// <remarks>
/// Static, with the lock store passed in — the shape <see cref="WebDavLockHandling"/> already uses, and the
/// only middleware state these needed (each verb asks <c>IsLocked</c> before it writes).
/// </remarks>
internal static class WebDavWrites
{
    // ---- PUT (create or new version) ----------------------------------------------------------------------
    internal static async Task HandlePutAsync(WebDavLockStore lockStore, HttpContext context, IServiceProvider services, SimplArchiveDbContext db, User user, List<string> segments)
    {
        if (WebDavMiddleware.IsSpecialPath(context, segments))
        {
            await WebDavSpecialHandlers.HandleSpecialPutAsync(context, services, db, user, segments);
            return;
        }

        // Writing to a path that was staged aside puts it back, by whatever route (#794). The set-aside is a
        // claim about the mount, not about the archive, so anything that makes the path real again retires it.
        if (segments.Count > 0)
        {
            await WebDavSafeSave.ClearSetAsideAsync(
                services.GetRequiredService<IObjectStorageClient>(), user, segments, context.RequestAborted);
        }

        // OS clutter (._*, .DS_Store, Thumbs.db, …) never becomes a document (ADR "WebDAV clutter filter") — but
        // it is REMEMBERED rather than dropped. Accepting a write and then answering 404 to a read of it is the
        // defect that produced "Word cannot complete the save due to a file permission error": measured on the
        // wire, the editor wrote `._<name>`, got 201, asked for it, got 404, wrote it AGAIN (201, not 204 —
        // proof nothing was kept), and eventually concluded it could not write at all.
        //
        // ABOVE the root refusal below, which is the whole point of it being here rather than with its siblings
        // further down (#794). Finder drops a `.DS_Store` in every directory it displays, the MOUNT ROOT
        // included, and there the root guard ran first: the same file was accepted one level down and refused
        // with 403 at the top. That 403 was the ONLY non-2xx in a ninety-second trace of a failing save, and
        // what a refusal at the root tells macOS is not "not that file" but "this volume does not take writes"
        // — after which the editor stopped attempting an atomic replace at all, opening scratch collection
        // after scratch collection and never writing the document into any of them.
        //
        // A rule enforced at one entrance is not a rule. This one is stated for the whole mount, so it is
        // applied before anything narrows the path down.
        // NOT inside a safe-save collection, which is the exception this hoist has to carry with it. A `._`
        // sidecar written into a scratch collection belongs to the COLLECTION's staging area, and LOCK and GET
        // both look for it there (`IsUnderSafeSaveTemp ? FileKey : ShadowKey`). Sending the PUT to the shadow
        // area instead splits one path across two keys, and the wire says so precisely: `PUT … → 201` followed
        // by `LOCK … → 201` — a 201 from LOCK means the resource did not exist, contradicting the PUT that had
        // just made it, after which the editor rewrote the same 4 KB sidecar four times and gave up. The
        // ordering that was here before put the safe-save branch first for exactly this reason.
        if (segments.Count > 0
            && !WebDavClutter.IsUnderSafeSaveTemp(segments)
            && WebDavClutter.IsOsClutter(segments[^1]))
        {
            await WebDavSpecialHandlers.StageShadowAsync(context, services, user, segments);
            return;
        }

        if (segments.Count < 2)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden; // can't PUT at the repository-list root
            return;
        }

        if (WebDavLockHandling.IsLocked(lockStore, context, user, segments))
        {
            context.Response.StatusCode = StatusCodes.Status423Locked;
            return;
        }

        var fileName = segments[^1];

        // A write INSIDE a safe-save collection (#762). Answering 201 to the MKCOL was a PROMISE, and this is
        // the verb that has to keep it: the collection was never materialised, so resolving this path's parent
        // fails and the editor got a 409 — "failed to write" — with the original left untouched but unsaved.
        //
        // Staged in the same per-user scratch the save-by-rename flow uses (ADR 0562), keyed by the LEAF name.
        // That is not a coincidence to lean on but the shape of the thing: the editor's next step renames this
        // file over the original, and inside a safe-save collection the leaf already HAS the original's exact
        // name — so TryCommitImplicitCheckoutAsync finds it on the MOVE and commits it, unchanged, with no new
        // code on the commit side at all.
        //
        // Accept-and-discard, which is right for junk nobody writes into (.DS_Store, .Trashes), was wrong here
        // for exactly one reason: a safe-save collection is a directory the editor DOES write into.
        if (WebDavClutter.IsUnderSafeSaveTemp(segments))
        {
            await WebDavSpecialHandlers.StageSafeSaveAsync(context, services, user, segments);
            return;
        }

        // A browser in-progress download (.crdownload/.part/.partial/.dltemp) is STAGED in the per-user temp
        // area (not dropped, not materialized as a document) and committed to a real document on the completing
        // MOVE (ADR "WebDAV .crdownload staging"). Checked before the clutter filter, which also matches these.
        if (WebDavClutter.IsDownloadTemp(fileName))
        {
            await WebDavSpecialHandlers.StageDownloadTempAsync(context, services, db, user, segments);
            return;
        }

        // An editor temp / owner sidecar (~$*, .tmp, .swp, …) is BUFFERED in the per-user scratch area rather
        // than discarded (ADR 0562). Discarding it is what made editing in place fail for a suite that saves by
        // rename: it writes the new content to a temporary name and then renames that over the original, so a
        // discarded temp leaves the committing MOVE with no source. The Check-out folder has buffered these
        // since ADR 0508; this is the same thing one level out, in the tree where the document actually lives.
        //
        // Still never a document: nothing here creates or names a row, the scratch prefix is outside the mounted
        // structure (ADR 0509), and an abandoned buffer is just an orphan object, exactly as under Check-out.
        if (WebDavClutter.IsTransientClutter(fileName))
        {
            await WebDavSpecialHandlers.StageTreeScratchAsync(context, services, fileName, user);
            return;
        }

        var parent = await WebDavPathResolver.ResolveAsync(db, user, segments[..^1]);
        if (parent is not { IsCollection: true, Document: { } parentDoc })
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict; // parent missing / not a collection
            return;
        }

        var rights = await WebDavMiddleware.RightsAsync(services, user, parentDoc.Id);
        var existing = await WebDavPathResolver.ResolveAsync(db, user, segments);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        // A LOCK/create dance from Finder sends a 0-byte PUT first; buffer the body to a temp object either way.
        var storage = services.GetRequiredService<IObjectStorageClient>();
        var finalizer = services.GetRequiredService<DocumentFinalizer>();
        var now = DateTimeOffset.UtcNow;
        // The key groups by the document (ADR 0530): an existing document reuses its filing year + storage folder;
        // a new document gets `now` + a fresh storage folder. The version id is the leaf either way, generated up
        // front so the DocumentVersion below reuses it.
        var versionId = Guid.NewGuid();
        DateTimeOffset keyYear;
        Guid keyStorageFolderId;
        if (existing is { Document: { } existingDoc, IsCollection: false })
        {
            keyYear = existingDoc.CreatedAt;
            keyStorageFolderId = existingDoc.StorageFolderId;
        }
        else
        {
            keyYear = now;
            keyStorageFolderId = Guid.NewGuid();
        }
        var objectKey = ObjectKeyBuilder.Build(user.TenantId, keyYear, keyStorageFolderId, versionId, extension);
        // Buffer the body so object storage has a known content length (the request stream may be
        // chunked / non-seekable).
        await using var buffered = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffered, context.RequestAborted);
        buffered.Position = 0;

        // A zero-byte PUT to a NEW name is the OS creating the file before it writes to it — macOS does exactly
        // this, and a browser does the same while streaming into a sibling .crdownload. It becomes a REAL, empty
        // document, and that is a deliberate reversal: it used to be accepted and discarded.
        //
        // Discarding it broke atomic saving invisibly. The file macOS had just created answered 404 on the next
        // read, so the editor wrote its content into the scratch collection, went to swap it over the original,
        // found no original, and abandoned — never issuing the MOVE. Measured (#762): PUT Test987.docx 0B → 201,
        // PUT …/.~WRD3576 13471B → 204, DELETE …/.~WRD3576, and no MOVE anywhere. Word reported success, because
        // webdavfs had been told 201 and had no reason to doubt it.
        //
        // Making it visible-but-not-a-document was tried next and moved the same contradiction one verb along:
        // PROPFIND said it existed, GET said 404, and macOS deleted the file it could not read. A placeholder
        // has to be a REAL resource or every verb has to learn about it separately — and a created-but-unwritten
        // file is an empty file on any filesystem, so an empty document is the honest representation. The
        // content that follows becomes a new VERSION of it, which is the check-out semantics we already want.


        // A write over a document that ALREADY HAS CONTENT is a working copy, not a version — whatever route the
        // application took to get here. Some editors save through a scratch collection and a swap; others, an
        // office spreadsheet among them, simply PUT the whole file at its real name:
        //
        //     PUT …/Contoso Cloud/Book1.xlsx  Content-Length: 8910   (no collection, no MOVE)
        //
        // Both are the same act. Routing only the first to a check-out made the archive's behaviour depend on
        // WHICH APPLICATION you saved from, with nothing in the UI to explain why one app's edits need checking
        // in and another's did not — an inconsistency worse than either rule alone.
        //
        // A PUT to a NEW name still creates a document: that is filing something, not editing it.
        // …but NOT a zero-byte one. macOS opens a file for writing by creating/truncating it first and sends the
        // content in a second request, so an empty body here is the OS clearing its throat, not an edit.
        // Treating it as one stashed an EMPTY working copy over a document that had content — and since the
        // tree serves the owner their working copy, the file then read as 0 bytes while v2 sat in the archive
        // holding all 13311 of them. Nothing was lost, and it looked exactly like loss, which is nearly as bad.
        if (buffered.Length > 0
            && existing is { Document: { } target, IsCollection: false }
            && await db.DocumentVersions.AnyAsync(
                v => v.DocumentId == target.Id && v.Status == DocumentVersionStatus.Confirmed && v.SizeBytes > 0,
                context.RequestAborted))
        {
            await WebDavSpecialHandlers.StashOverExistingAsync(context, services, db, user, target, buffered);
            return;
        }

        // The OS's create/truncate against a document that already has content: accepted, and it changes
        // nothing. The content arrives in the next request.
        if (buffered.Length == 0 && existing is { IsCollection: false })
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        // Enforce the tenant storage quota (ADR "WebDAV hardening" / ADR "Per-tenant storage quota") before the
        // blob is committed — return 507 Insufficient Storage (the code WebDAV clients understand).
        if (!await services.GetRequiredService<IStorageQuotaService>().CanStoreAsync(user.TenantId, buffered.Length, context.RequestAborted))
        {
            context.Response.StatusCode = StatusCodes.Status507InsufficientStorage;
            return;
        }

        await storage.PutObjectAsync(objectKey, buffered, context.Request.ContentType ?? "application/octet-stream", context.RequestAborted);

        Document document;
        if (existing is { Document: { } doc, IsCollection: false })
        {
            if (!rights.CanEditContent) { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
            document = doc;
        }
        else if (existing is not null)
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed; // a collection already exists here
            return;
        }
        else
        {
            if (!rights.CanCreateSubItems) { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }

            // Whether the destination admits only its own listed masks (a Calendar, an Addressbook, a Notebook).
            var parentMaskId = await db.MaskVersions.Where(mv => mv.Id == parentDoc.MaskVersionId)
                .Select(mv => (Guid?)mv.MaskId).FirstOrDefaultAsync(context.RequestAborted);
            var parentIsTypedFolder = parentMaskId is { } pm
                && WellKnownMaskIds.TypedFolderRules.Any(r => r.FolderMaskId == pm);

            // Stamped with the Folder mask at creation, exactly as the API's create does — the finalizer
            // reclassifies it to Basic Entry / eMail once the bytes arrive (ADR "Folder mask on folders").
            //
            // Creating it MASKLESS is what let a file dropped on the mounted `Personal` drive land at the
            // personal space's first level (#644): maskless is admitted there (it is the pre-upgrade state),
            // and the rule is gated on arrival, so the finalizer's later stamp was never re-checked. Stamping
            // here refuses it at creation, BEFORE any bytes transfer, which is what the API and both clients
            // already do (ADR 0637).
            document = new Document
            {
                Id = Guid.NewGuid(),
                TenantId = user.TenantId,
                ParentId = parentDoc.Id,
                Name = stem,
                // …but NULL inside a TYPED folder, which is the other half of the API's rule and the half that
                // matters here: a My Calendar / My Addressbook admits only Appointments / Contacts, so a
                // Folder-masked child is refused outright. Those uploads must arrive unclassified and let the
                // finalizer decide what they are — which is exactly how a .ics or .vcf becomes one.
                MaskVersionId = parentIsTypedFolder
                    ? null
                    : await FolderMask.CurrentVersionIdAsync(db, user.TenantId, WellKnownMaskIds.Folder, context.RequestAborted)
                        ?? await FolderMask.CurrentVersionIdAsync(db, context.RequestAborted),
                CreatedByUserId = user.Id,
                CreatedAt = now,
                StorageFolderId = keyStorageFolderId,
            };
            db.Documents.Add(document);
            try { await db.SaveChangesAsync(context.RequestAborted); }
            catch (SimplArchive.Domain.Documents.PersonalSpaceStructureException) { context.Response.StatusCode = StatusCodes.Status409Conflict; return; }
            catch (Domain.Masks.TypedFolderContainmentException) { context.Response.StatusCode = StatusCodes.Status409Conflict; return; }
            catch (InvalidOperationException) { context.Response.StatusCode = StatusCodes.Status409Conflict; return; } // sibling-name clash
        }

        var version = new DocumentVersion
        {
            Id = versionId,
            DocumentId = document.Id,
            TenantId = user.TenantId,
            Status = DocumentVersionStatus.Pending,
            ObjectKey = objectKey,
            CreatedByUserId = user.Id,
            CreatedAt = now,
            DocumentDate = DateOnly.FromDateTime(now.UtcDateTime),
        };
        db.DocumentVersions.Add(version);
        await db.SaveChangesAsync(context.RequestAborted);
        await finalizer.FinalizeAsync(version, context.RequestAborted);

        context.Response.StatusCode = existing is null ? StatusCodes.Status201Created : StatusCodes.Status204NoContent;
    }

    // ---- MKCOL (create folder) ----------------------------------------------------------------------------
    internal static async Task HandleMkColAsync(WebDavLockStore lockStore, HttpContext context, IServiceProvider services, SimplArchiveDbContext db, User user, List<string> segments)
    {
        // BEFORE the special-path refusal, and that ordering is the fix (#764). A word processor's atomic replace
        // creates a `<file>.sb-<hex>-<rand>` collection; refusing it made the editor roll back and DELETE THE
        // ORIGINAL — in the Intray, where items have no soft-delete, unrecoverably. Accepted and discarded, like
        // any other junk directory: the editor gets its "yes", and nothing is materialised.
        if (segments.Count >= 2 && WebDavClutter.IsSafeSaveTemp(segments[^1]))
        {
            // RECORDED, not discarded. Discarding is what made every later verb a lie: the editor went on to
            // PUT into a collection we said did not exist, and to PROPFIND files we had already accepted.
            await WebDavSafeSave.CreateAsync(
                services.GetRequiredService<IObjectStorageClient>(), user, segments, context.RequestAborted);
            context.Response.StatusCode = StatusCodes.Status201Created;
            return;
        }

        // Silently accept OS-junk directories (.Trashes, .TemporaryItems, .fseventsd, .Spotlight-V100 …) without
        // creating a folder document (ADR "WebDAV clutter filter"). Before the root refusal, for the reason
        // given on PUT: macOS creates these AT THE MOUNT ROOT, and refusing them there says the volume is
        // read-only rather than saying no to one directory (#794).
        if (segments.Count > 0 && WebDavClutter.IsOsClutter(segments[^1]))
        {
            context.Response.StatusCode = StatusCodes.Status201Created;
            return;
        }

        if (segments.Count < 2 || WebDavMiddleware.IsSpecialPath(context, segments))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden; // can't create folders at the root, on the virtual Intray/Check-out folders, or inside them
            return;
        }

        if (WebDavLockHandling.IsLocked(lockStore, context, user, segments))
        {
            context.Response.StatusCode = StatusCodes.Status423Locked;
            return;
        }

        var parent = await WebDavPathResolver.ResolveAsync(db, user, segments[..^1]);
        if (parent is not { IsCollection: true, Document: { } parentDoc })
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        if (await WebDavPathResolver.ResolveAsync(db, user, segments) is not null)
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed; // already exists
            return;
        }

        var rights = await WebDavMiddleware.RightsAsync(context.RequestServices, user, parentDoc.Id);
        if (!rights.CanCreateSubItems)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // Assign the Folder mask like every other folder-creation path (ADR "Folder mask on folders").
        db.Documents.Add(new Document
        {
            Id = Guid.NewGuid(),
            TenantId = user.TenantId,
            ParentId = parentDoc.Id,
            Name = segments[^1],
            MaskVersionId = await FolderMask.CurrentVersionIdAsync(db, context.RequestAborted),
            CreatedByUserId = user.Id,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        try { await db.SaveChangesAsync(context.RequestAborted); }
        catch (InvalidOperationException) { context.Response.StatusCode = StatusCodes.Status409Conflict; return; }

        context.Response.StatusCode = StatusCodes.Status201Created;
    }

    // ---- DELETE (soft-delete to the recycle bin) ----------------------------------------------------------
    internal static async Task HandleDeleteAsync(WebDavLockStore lockStore, HttpContext context, IServiceProvider services, SimplArchiveDbContext db, User user, List<string> segments)
    {
        // The safe-save collection, or anything inside it (#762). It was never materialised, so there is nothing
        // to delete — but 404 is the wrong answer to give an editor tidying up after a save it believes
        // succeeded, and it is the answer this path used to give. Same promise as the 201: having accepted the
        // collection, every later verb has to behave as though it exists.
        if (WebDavClutter.IsSafeSaveScope(segments))
        {
            await WebDavSafeSave.RemoveAsync(
                services.GetRequiredService<IObjectStorageClient>(), user, segments, context.RequestAborted);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        if (WebDavMiddleware.IsSpecialPath(context, segments))
        {
            if (segments.Count != 3)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            var storage = services.GetRequiredService<IObjectStorageClient>();
            var name = segments[2];

            if (segments[1] == WebDavMiddleware.IntrayName)
            {
                // A remembered write is deletable. Answering 404 for something we accepted with 201 is the same
                // contradiction in its last place on this surface (#794) — the editor deletes its sidecar as
                // part of tidying up, and a 404 there reads as the save having gone wrong.
                if (WebDavClutter.IsOsClutter(name))
                {
                    var shadowKey = WebDavSafeSave.ShadowKey(user, segments);
                    if (await storage.ExistsAsync(shadowKey, context.RequestAborted))
                    {
                        await storage.DeleteObjectAsync(shadowKey, context.RequestAborted);
                    }

                    context.Response.StatusCode = StatusCodes.Status204NoContent;
                    return;
                }

                if ((await WebDavUserAreas.IntrayFilesAsync(storage, user)).All(f => f.Name != name) && !WebDavClutter.IsLockFile(name))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                await storage.DeleteObjectAsync(WebDavUserAreas.IntrayPrefix(user) + name, context.RequestAborted);
                try { await storage.DeleteObjectAsync(WebDavUserAreas.IntrayPrefix(user) + name + ".mask.json", context.RequestAborted); } catch (Exception) { /* sidecar may not exist */ }
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            // Check-out (ADR 0508): deleting a checked-out doc's name is the editor's pre-rename delete — a no-op
            // (the check-out is released only via the client); deleting a scratch temp/lock file removes it.
            if ((await WebDavUserAreas.CheckoutFilesAsync(storage, db, user)).Any(f => f.Name == name))
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent; // no-op: keep the check-out
                return;
            }

            var scratchKey = WebDavUserAreas.CheckoutScratchPrefix(user) + name;
            if (await storage.ExistsAsync(scratchKey, context.RequestAborted))
            {
                await storage.DeleteObjectAsync(scratchKey, context.RequestAborted);
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            context.Response.StatusCode = WebDavClutter.IsLockFile(name) ? StatusCodes.Status204NoContent : StatusCodes.Status404NotFound;
            return;
        }

        // A browser cancelling/finishing a download deletes its in-progress temp file; drop any staged blob and
        // succeed (there is no Document to remove). ADR "WebDAV .crdownload staging".
        if (WebDavClutter.IsDownloadTemp(segments[^1]))
        {
            var storage = services.GetRequiredService<IObjectStorageClient>();
            var tempKey = WebDavUserAreas.TempKeyFor(user, segments);
            if (await storage.ExistsAsync(tempKey, context.RequestAborted))
            {
                await storage.DeleteObjectAsync(tempKey, context.RequestAborted);
            }

            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        if (WebDavLockHandling.IsLocked(lockStore, context, user, segments))
        {
            context.Response.StatusCode = StatusCodes.Status423Locked;
            return;
        }

        var node = await WebDavPathResolver.ResolveAsync(db, user, segments);
        if (node?.Document is not { } document)
        {
            // A remembered write is deletable, ANYWHERE — the Intray path below has said so since #794 and the
            // tree had not, so a `.DS_Store` we accepted at the mount root could not be removed again. Accepting
            // a write is a promise every later verb has to keep (ADR 0707), and DELETE is a later verb: the OS
            // tidies its own junk, and a 404 for something it just wrote reads as the volume losing writes.
            if (segments.Count > 0 && (WebDavClutter.IsOsClutter(segments[^1]) || WebDavClutter.IsTransientClutter(segments[^1])))
            {
                var clutterStorage = services.GetRequiredService<IObjectStorageClient>();

                // The SAME key selector PUT, GET and LOCK use. A `._` sidecar inside a scratch collection is
                // staged with the collection, not in the shadow area, and deleting the shadow key instead
                // answers 204 while the file it claimed to remove is still there (#794). A transient name
                // (`~$…`, editor temps) lives in the tree scratch — refusing its DELETE with 404 is what left
                // the editor's own lock file behind after it closed, unable to clean up after itself; the
                // legacy shadow a pre-fix LOCK wrote for the same name is swept in the same breath.
                var clutterKey = WebDavClutter.IsUnderSafeSaveTemp(segments)
                    ? WebDavSafeSave.FileKey(user, segments)
                    : WebDavClutter.IsTransientClutter(segments[^1])
                        ? WebDavUserAreas.CheckoutScratchPrefix(user) + segments[^1]
                        : WebDavSafeSave.ShadowKey(user, segments);
                if (await clutterStorage.ExistsAsync(clutterKey, context.RequestAborted))
                {
                    await clutterStorage.DeleteObjectAsync(clutterKey, context.RequestAborted);
                }

                var legacyShadow = WebDavSafeSave.ShadowKey(user, segments);
                if (legacyShadow != clutterKey && await clutterStorage.ExistsAsync(legacyShadow, context.RequestAborted))
                {
                    await clutterStorage.DeleteObjectAsync(legacyShadow, context.RequestAborted);
                }

                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            context.Response.StatusCode = node is null ? StatusCodes.Status404NotFound : StatusCodes.Status403Forbidden; // can't delete a virtual root/repository listing
            return;
        }

        // Reached through a REFERENCE: delete the APPEARANCE, never the document (#769). This is the one that
        // loses data if guessed wrong — a user tidying a working folder on a mounted drive would otherwise
        // destroy the document itself, which is still filed somewhere they were not looking.
        //
        // Gated on the FOLDER's right, not the target's, for the same reason the API gates it there: removing
        // a shortcut changes the contents of the folder holding it and nothing about the document.
        if (node.ViaReferenceId is { } referenceId)
        {
            var folder = await WebDavPathResolver.ResolveAsync(db, user, segments[..^1]);
            if (folder?.Document is not { } holder || !(await WebDavMiddleware.RightsAsync(services, user, holder.Id)).CanCreateSubItems)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            var reference = await db.DocumentReferences.FirstOrDefaultAsync(r => r.Id == referenceId, context.RequestAborted);
            if (reference is not null)
            {
                db.DocumentReferences.Remove(reference);
                await db.SaveChangesAsync(context.RequestAborted);
            }

            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        if (!(await WebDavMiddleware.RightsAsync(services, user, document.Id)).CanDelete)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // Soft-delete the subtree (the same cascade as the API's DELETE).
        foreach (var d in await WebDavPathResolver.CollectSubtreeAsync(db, document.Id))
        {
            d.DeletedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }
}
