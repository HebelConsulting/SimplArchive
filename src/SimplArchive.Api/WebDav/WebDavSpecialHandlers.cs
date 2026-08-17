using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Documents;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.WebDav;

// The HTTP verb handling for the per-user special areas (Personal ▸ Intray / Check-out) and their staging
// tiers — the half of the gateway that talks to S3 prefixes instead of the Document tree (issue #466 moved it
// out of the middleware; key derivation + listings live in WebDavUserAreas, the clutter rules in
// WebDavClutter). Stateless by construction: every method takes what it acts on, which is what let the whole
// family move without a constructor.
internal static class WebDavSpecialHandlers
{
    internal static async Task HandleSpecialPropFindAsync(HttpContext context, IServiceProvider services, SimplArchiveDbContext db, User user, List<string> segments, string depth)
    {
        var folder = segments[1];
        var storage = services.GetRequiredService<IObjectStorageClient>();

        if (segments.Count == 2)
        {
            // The Intray / Check-out collection itself, plus (Depth 1) its files (lock/owner sidecars stay hidden).
            var files = await WebDavUserAreas.SpecialFolderFilesAsync(storage, db, user, folder);
            var responses = new List<PropStatXml> { WebDavMiddleware.CollectionProp([segments[0], folder], folder) };
            if (depth != "0")
            {
                responses.AddRange(files.Select(f => WebDavMiddleware.FileProp([segments[0], folder, f.Name], f.Size, f.Modified, ContentTypes.ForExtension(Path.GetExtension(f.Name)))));
            }

            await WebDavXml.WriteMultiStatusAsync(context, responses);
            return;
        }

        // A single file inside the folder (flat — no deeper nesting), including a hidden lock/owner sidecar.
        if (segments.Count == 3 && await WebDavUserAreas.ResolveSpecialFileAsync(storage, db, user, folder, segments[2]) is { } file)
        {
            await WebDavXml.WriteMultiStatusAsync(context, [WebDavMiddleware.FileProp(segments, file.Size, file.Modified, ContentTypes.ForExtension(Path.GetExtension(file.Name)))]);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    internal static async Task HandleSpecialPutAsync(HttpContext context, IServiceProvider services, SimplArchiveDbContext db, User user, List<string> segments)
    {
        if (segments.Count != 3)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden; // the special folders are flat
            return;
        }

        var name = segments[2];

        // OS metadata junk is discarded even in the staging areas; transient files (.crdownload etc.) are allowed
        // here (unlike the repository) — ADR "WebDAV clutter filter".
        if (WebDavClutter.IsOsClutter(name))
        {
            context.Response.StatusCode = StatusCodes.Status201Created;
            return;
        }

        var storage = services.GetRequiredService<IObjectStorageClient>();
        await using var buffered = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffered, context.RequestAborted);
        buffered.Position = 0;
        var contentType = context.Request.ContentType ?? "application/octet-stream";

        if (segments[1] == WebDavMiddleware.IntrayName)
        {
            // Stage a raw object in the intray prefix — no Document is created (the staging semantics; it's filed
            // later from the Intray tab). ADR "S3-backed inbox" / "WebDAV Intray + Check-out folders".
            if (WebDavClutter.IsIntrayLitter(name))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            var key = WebDavUserAreas.IntrayPrefix(user) + name;
            var existed = await storage.ExistsAsync(key, context.RequestAborted);
            await storage.PutObjectAsync(key, buffered, contentType, context.RequestAborted);
            context.Response.StatusCode = existed ? StatusCodes.Status204NoContent : StatusCodes.Status201Created;
            return;
        }

        // Check-out: a PUT onto a checked-out document's name saves the working copy to that doc's stash (the
        // "Save to cloud" path). A PUT to any OTHER name is an atomic-save temp/lock/owner file — buffer it in the
        // per-user scratch area so the later rename MOVE can commit it (ADR 0508). Creating a check-out over WebDAV
        // is still not supported.
        var files = await WebDavUserAreas.CheckoutFilesAsync(storage, db, user);
        if (files.FirstOrDefault(f => f.Name == name) is { } file)
        {
            await storage.PutObjectAsync(CheckoutStashKey.Build(user.TenantId, user.Id, file.DocumentId), buffered, contentType, context.RequestAborted);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        var scratchKey = WebDavUserAreas.CheckoutScratchPrefix(user) + name;
        var scratchExisted = await storage.ExistsAsync(scratchKey, context.RequestAborted);
        await storage.PutObjectAsync(scratchKey, buffered, contentType, context.RequestAborted);
        context.Response.StatusCode = scratchExisted ? StatusCodes.Status204NoContent : StatusCodes.Status201Created;
    }

    // MOVE / COPY within a special folder — the rename/duplicate steps of an editor's atomic save (ADR 0508).
    // keepSource=false for MOVE (rename), true for COPY (duplicate). The destination must be the SAME special
    // folder (cross-folder moves aren't supported). In Check-out:
    //  • scratch temp → a checked-out document = the commit (write the bytes to that document's stash);
    //  • scratch temp → another name = duplicate/rename the temp;
    //  • a checked-out document → a scratch name = copy the document's CURRENT working bytes out to a scratch
    //    backup (macOS's replaceItemAtURL renames the original away before dropping the new file in) — the
    //    document itself stays checked out and in place.
    // In the Intray it renames/duplicates the staged object. So every combination office/PDF editors emit —
    // temp+rename, temp+copy, delete-then-rename, or the rename-original-to-backup dance — resolves correctly.
    internal static async Task HandleSpecialRenameAsync(HttpContext context, IServiceProvider services, SimplArchiveDbContext db, User user, List<string> segments, bool keepSource)
    {
        var destSegments = WebDavMiddleware.ParseDestination(context);
        if (segments.Count != 3 || destSegments is not { Count: 3 } || !WebDavMiddleware.IsSpecialPath(destSegments)
            || destSegments[0] != segments[0] || destSegments[1] != segments[1])
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden; // only same-special-folder renames are supported
            return;
        }

        var storage = services.GetRequiredService<IObjectStorageClient>();
        var (srcName, destName) = (segments[2], destSegments[2]);

        if (segments[1] == WebDavMiddleware.IntrayName)
        {
            var srcKey = WebDavUserAreas.IntrayPrefix(user) + srcName;
            if (!await storage.ExistsAsync(srcKey, context.RequestAborted))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var destKey = WebDavUserAreas.IntrayPrefix(user) + destName;
            var intrayDestExisted = await storage.ExistsAsync(destKey, context.RequestAborted);
            await storage.CopyObjectAsync(srcKey, destKey, context.RequestAborted);
            if (!keepSource) await storage.DeleteObjectAsync(srcKey, context.RequestAborted);
            context.Response.StatusCode = intrayDestExisted ? StatusCodes.Status204NoContent : StatusCodes.Status201Created;
            return;
        }

        // Check-out.
        var docs = await WebDavUserAreas.CheckoutFilesAsync(storage, db, user);
        var scratchSrcKey = WebDavUserAreas.CheckoutScratchPrefix(user) + srcName;
        var srcIsScratch = await storage.ExistsAsync(scratchSrcKey, context.RequestAborted);

        // Source is a checked-out document → copy its current working bytes out to a scratch backup; the document
        // stays put (a document is never renamed/removed over WebDAV — the check-out is a client action).
        if (!srcIsScratch && docs.FirstOrDefault(f => f.Name == srcName) is { } srcDoc)
        {
            if (docs.Any(f => f.Name == destName)) { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; } // doc → doc unsupported
            var backupKey = WebDavUserAreas.CheckoutScratchPrefix(user) + destName;
            var backupExisted = await storage.ExistsAsync(backupKey, context.RequestAborted);
            await storage.CopyObjectAsync(srcDoc.Key, backupKey, context.RequestAborted);
            context.Response.StatusCode = backupExisted ? StatusCodes.Status204NoContent : StatusCodes.Status201Created;
            return;
        }

        if (!srcIsScratch)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // Scratch → a checked-out document = the commit: write the scratch bytes to that document's stash.
        if (docs.FirstOrDefault(f => f.Name == destName) is { } targetDoc)
        {
            using var buffered = new MemoryStream();
            await using (var scratch = await storage.GetObjectAsync(scratchSrcKey, context.RequestAborted))
            {
                await scratch.CopyToAsync(buffered, context.RequestAborted);
            }

            buffered.Position = 0;
            await storage.PutObjectAsync(
                CheckoutStashKey.Build(user.TenantId, user.Id, targetDoc.DocumentId),
                buffered, ContentTypes.ForExtension(Path.GetExtension(destName)), context.RequestAborted);
            if (!keepSource) await storage.DeleteObjectAsync(scratchSrcKey, context.RequestAborted);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        // Scratch → scratch = duplicate/rename the temp.
        var scratchDestKey = WebDavUserAreas.CheckoutScratchPrefix(user) + destName;
        var scratchDestExisted = await storage.ExistsAsync(scratchDestKey, context.RequestAborted);
        await storage.CopyObjectAsync(scratchSrcKey, scratchDestKey, context.RequestAborted);
        if (!keepSource) await storage.DeleteObjectAsync(scratchSrcKey, context.RequestAborted);
        context.Response.StatusCode = scratchDestExisted ? StatusCodes.Status204NoContent : StatusCodes.Status201Created;
    }

    // Stage a browser in-progress download (.crdownload etc.) as an opaque object in the per-user temp area —
    // no Document is created; it's committed on the completing MOVE (ADR "WebDAV .crdownload staging").
    internal static async Task StageDownloadTempAsync(HttpContext context, IServiceProvider services, SimplArchiveDbContext db, User user, List<string> segments)
    {
        if (segments.Count < 2)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden; // can't PUT at the repository-list root
            return;
        }

        var parent = await WebDavPathResolver.ResolveAsync(db, user, segments[..^1]);
        if (parent is not { IsCollection: true, Document: { } parentDoc })
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict; // parent missing / not a collection
            return;
        }

        if (!(await WebDavMiddleware.RightsAsync(services, user, parentDoc.Id)).CanCreateSubItems)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await using var buffered = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffered, context.RequestAborted);
        buffered.Position = 0;

        if (!await services.GetRequiredService<IStorageQuotaService>().CanStoreAsync(user.TenantId, buffered.Length, context.RequestAborted))
        {
            context.Response.StatusCode = StatusCodes.Status507InsufficientStorage;
            return;
        }

        await services.GetRequiredService<IObjectStorageClient>().PutObjectAsync(
            WebDavUserAreas.TempKeyFor(user, segments), buffered, context.Request.ContentType ?? "application/octet-stream", context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status201Created;
    }

    // Commit a staged download-temp on the MOVE that renames it to the final name: materialize the real Document
    // from the staged bytes + finalize, then drop the temp copy (ADR "WebDAV .crdownload staging"). Returns false
    // (nothing committed) when there's no staged blob — the caller then does a normal move.
    internal static async Task<bool> TryCommitDownloadTempAsync(HttpContext context, IServiceProvider services, SimplArchiveDbContext db, User user, List<string> segments)
    {
        var storage = services.GetRequiredService<IObjectStorageClient>();
        var tempKey = WebDavUserAreas.TempKeyFor(user, segments);
        if (!await storage.ExistsAsync(tempKey, context.RequestAborted))
        {
            return false;
        }

        var destSegments = WebDavMiddleware.ParseDestination(context);
        if (destSegments is null || destSegments.Count < 2 || WebDavMiddleware.IsSpecialPath(destSegments))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return true;
        }

        var destParent = await WebDavPathResolver.ResolveAsync(db, user, destSegments[..^1]);
        if (destParent is not { IsCollection: true, Document: { } destParentDoc })
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return true;
        }

        if (!(await WebDavMiddleware.RightsAsync(services, user, destParentDoc.Id)).CanCreateSubItems)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return true;
        }

        var destName = destSegments[^1];
        var now = DateTimeOffset.UtcNow;
        // The key groups by the new document (ADR 0530): its filing year + a fresh storage folder, version id leaf.
        var storageFolderId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var objectKey = ObjectKeyBuilder.Build(user.TenantId, now, storageFolderId, versionId, Path.GetExtension(destName));

        // Server-side copy the staged blob to a real version key, then create the Document + finalize.
        await storage.CopyObjectAsync(tempKey, objectKey, context.RequestAborted);

        var document = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = user.TenantId,
            ParentId = destParentDoc.Id,
            Name = Path.GetFileNameWithoutExtension(destName),
            CreatedByUserId = user.Id,
            CreatedAt = now,
            StorageFolderId = storageFolderId,
        };
        db.Documents.Add(document);
        try { await db.SaveChangesAsync(context.RequestAborted); }
        catch (InvalidOperationException)
        {
            await storage.DeleteObjectAsync(objectKey, context.RequestAborted);
            context.Response.StatusCode = StatusCodes.Status409Conflict; // sibling-name clash
            return true;
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
        await services.GetRequiredService<DocumentFinalizer>().FinalizeAsync(version, context.RequestAborted);

        await storage.DeleteObjectAsync(tempKey, context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status201Created;
        return true;
    }

    // A rename whose source is a buffered temp and whose destination is an existing document: the save-by-rename
    // commit (ADR 0562). Returns false when this is an ordinary move, so the caller falls through.
    //
    // What it deliberately does NOT do: detect that the edit has finished, check in, or cancel. The document is
    // left checked out with the bytes in the stash, which is the accurate description of what happened — the
    // person editing decides when it is done, by checking in.
    internal static async Task<bool> TryCommitImplicitCheckoutAsync(
        HttpContext context, IServiceProvider services, SimplArchiveDbContext db, User user, List<string> segments)
    {
        var storage = services.GetRequiredService<IObjectStorageClient>();
        var scratchKey = WebDavUserAreas.CheckoutScratchPrefix(user) + segments[^1];
        if (!await storage.ExistsAsync(scratchKey, context.RequestAborted))
        {
            return false;
        }

        var destSegments = WebDavMiddleware.ParseDestination(context);
        if (destSegments is null || destSegments.Count < 2 || WebDavMiddleware.IsSpecialPath(destSegments))
        {
            return false;
        }

        var destination = await WebDavPathResolver.ResolveAsync(db, user, destSegments);
        if (destination?.Document is not { } document || destination.IsCollection)
        {
            return false; // renaming a temp onto a NEW name is an ordinary create, handled elsewhere
        }

        // Every refusal the API path applies, applied here too: a mount must not be a side door (ADR 0562).
        var rights = await WebDavMiddleware.RightsAsync(services, user, document.Id);
        if (!rights.CanEditContent)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return true;
        }

        var legalHold = services.GetRequiredService<ILegalHoldService>();
        if (await legalHold.IsFrozenAsync(document.Id, context.RequestAborted))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return true;
        }

        // Held by someone else: refuse the write rather than letting the editor believe it saved. 423 is what a
        // WebDAV client understands, and it is what the lock store already returns elsewhere.
        if (document.CheckedOutByUserId is { } holder && holder != user.Id)
        {
            context.Response.StatusCode = StatusCodes.Status423Locked;
            return true;
        }

        var isNewCheckout = document.CheckedOutByUserId is null;
        if (isNewCheckout)
        {
            document.CheckedOutByUserId = user.Id;
            document.CheckedOutAt = DateTimeOffset.UtcNow;
            document.CheckoutReminderSentAt = null;

            // Evidence of WHAT took the lock, for someone who never pressed "check out". Client-supplied, so it
            // is capped and never branched on (ADR 0562).
            var agent = context.Request.Headers.UserAgent.ToString();
            document.ImplicitCheckoutAgent = string.IsNullOrWhiteSpace(agent)
                ? "(unidentified WebDAV client)"
                : agent[..Math.Min(agent.Length, 256)];
        }

        // The bytes land in the same stash an explicit check-out uses, so check-in, discard, the Check-out tab
        // and the stale sweep all work on this exactly as they already do.
        await storage.CopyObjectAsync(scratchKey, CheckoutStashKey.Build(user.TenantId, user.Id, document.Id), context.RequestAborted);
        await storage.DeleteObjectAsync(scratchKey, context.RequestAborted);
        await db.SaveChangesAsync(context.RequestAborted);

        if (isNewCheckout)
        {
            var audit = services.GetRequiredService<IAuditRecorder>();
            await audit.RecordAsync(
                Controllers.AuditActions.DocumentCheckedOutImplicitly, "Document", document.Id, document.Name,
                $"checked out automatically by {document.ImplicitCheckoutAgent} saving over WebDAV",
                cancellationToken: context.RequestAborted);
        }

        context.Response.StatusCode = StatusCodes.Status204NoContent;
        return true;
    }

    // Per-user scratch area for the Check-out folder's in-flight atomic-save temp files (ADR 0508) — the same
    // tier as intray/ and checkout/ (ADR 0368). A temp is committed to the doc's stash on the rename MOVE.
    // Buffers an editor temp / owner sidecar written in the TREE into the per-user scratch area (ADR 0562).
    // Keyed by name only: the rename that commits it names the same file, and the scratch prefix is per user, so
    // two people editing different documents cannot collide unless they use the same temp name at the same
    // moment — in which case the loser's buffer is overwritten and their editor's rename fails, which is the
    // same outcome it would get from a local disk.
    internal static async Task StageTreeScratchAsync(HttpContext context, IServiceProvider services, string fileName, User user)
    {
        var storage = services.GetRequiredService<IObjectStorageClient>();
        await using var buffered = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffered, context.RequestAborted);
        buffered.Position = 0;

        var key = WebDavUserAreas.CheckoutScratchPrefix(user) + fileName;
        var existed = await storage.ExistsAsync(key, context.RequestAborted);
        await storage.PutObjectAsync(key, buffered, context.Request.ContentType ?? "application/octet-stream", context.RequestAborted);
        context.Response.StatusCode = existed ? StatusCodes.Status204NoContent : StatusCodes.Status201Created;
    }
}
