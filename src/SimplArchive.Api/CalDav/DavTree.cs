using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.CalDav;

/// <summary>A typed folder the caller may subscribe to, with the display name the client shows.</summary>
internal sealed record DavCollection(Guid FolderId, string DisplayName, bool Writable);

/// <summary>An item inside such a folder: the document, its current version, and its DAV resource name.</summary>
internal sealed record DavItem(Guid DocumentId, string ResourceName, string ObjectKey, string ETag, DateTimeOffset LastModified);

/// <summary>
/// Resolves what a CalDAV/CardDAV caller can see (#564, ADR 0619): the typed folders they hold CanSee on,
/// wherever those sit in the archive tree, and the items inside one. The home set is FLAT by decision — a
/// client picks collections from a list, so tree position rides in the display name instead of the URL.
/// </summary>
/// <remarks>
/// Addressing one resource never enumerates the rest. A syncing client fetches items one at a time, so a
/// per-item "list everything and filter" would be quadratic on the hottest path there is: 500 contacts would
/// mean 500 scans of every typed folder in the archive, each with an ACL check per folder. Hence the single-
/// collection and single-item lookups below, which the listing paths reuse rather than the other way round.
/// </remarks>
internal static class DavTree
{
    /// <summary>
    /// Every ACL-visible folder wearing the protocol's folder mask, "My Calendar"/"My Contacts" first (the
    /// personal defaults a client should see at the top), then alphabetically by display name.
    /// </summary>
    internal static async Task<List<DavCollection>> CollectionsAsync(
        SimplArchiveDbContext db, IEffectiveRightsCalculator rights, Guid userId, DavProtocol protocol, CancellationToken cancellationToken)
    {
        var candidates = await FolderQuery(db, protocol)
            .Select(d => new FolderRow(d.Id, d.Name, d.ParentId))
            .ToListAsync(cancellationToken);

        // Parent rows for the display name — one lookup for the whole page rather than one per collection.
        var parentIds = candidates.Where(c => c.ParentId is not null).Select(c => c.ParentId!.Value).Distinct().ToList();
        var parents = await db.Documents
            .Where(d => parentIds.Contains(d.Id))
            .Select(d => new ParentRow(d.Id, d.Name, d.PersonalOfUserId))
            .ToDictionaryAsync(p => p.Id, p => p, cancellationToken);

        var collections = new List<(DavCollection Collection, bool IsPersonalDefault)>();
        foreach (var candidate in candidates)
        {
            var effective = await rights.GetEffectiveRightsAsync(userId, candidate.Id);
            if (!effective.CanSee)
            {
                continue;
            }

            var parent = candidate.ParentId is { } parentId ? parents.GetValueOrDefault(parentId) : null;
            collections.Add((
                new DavCollection(candidate.Id, DisplayName(candidate, parent), effective.CanEditContent),
                // The caller's own personal defaults sort first; another user's personal folder shared with
                // the caller is a normal collection.
                parent?.PersonalOfUserId == userId));
        }

        return collections
            .OrderByDescending(c => c.IsPersonalDefault)
            .ThenBy(c => c.Collection.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(c => c.Collection)
            .ToList();
    }

    /// <summary>
    /// The collection with this id, if the caller may see it and it really wears the folder mask — resolved
    /// DIRECTLY (two small queries and one rights check), because every item request goes through here.
    /// </summary>
    internal static async Task<DavCollection?> CollectionAsync(
        SimplArchiveDbContext db, IEffectiveRightsCalculator rights, Guid userId, DavProtocol protocol, Guid folderId, CancellationToken cancellationToken)
    {
        var folder = await FolderQuery(db, protocol)
            .Where(d => d.Id == folderId)
            .Select(d => new FolderRow(d.Id, d.Name, d.ParentId))
            .FirstOrDefaultAsync(cancellationToken);
        if (folder is null)
        {
            return null;
        }

        var effective = await rights.GetEffectiveRightsAsync(userId, folder.Id);
        if (!effective.CanSee)
        {
            return null;
        }

        var parent = folder.ParentId is { } parentId
            ? await db.Documents.Where(d => d.Id == parentId)
                .Select(d => new ParentRow(d.Id, d.Name, d.PersonalOfUserId))
                .FirstOrDefaultAsync(cancellationToken)
            : null;

        return new DavCollection(folder.Id, DisplayName(folder, parent), effective.CanEditContent);
    }

    /// <summary>
    /// The items of a collection: every child wearing the protocol's item mask that has a current version.
    /// The resource name is UID-derived (decided) — <c>{uid}{extension}</c>, which is the shape a client uses
    /// when it creates one itself, so server-side and client-side items are indistinguishable.
    /// </summary>
    internal static async Task<List<DavItem>> ItemsAsync(
        SimplArchiveDbContext db, DavProtocol protocol, Guid folderId, CancellationToken cancellationToken)
    {
        var documents = await ItemQuery(db, protocol, folderId)
            .Select(d => new ItemRow(d.Id, d.CurrentVersionId, d.ConcurrencyToken))
            .ToListAsync(cancellationToken);

        // The UID values for the whole collection in one query — the resource name comes from them.
        var uidFieldIds = await UidFieldIdsAsync(db, protocol, cancellationToken);
        var documentIds = documents.Select(d => d.Id).ToList();
        var uids = await db.FieldValues
            .Where(fv => documentIds.Contains(fv.DocumentId) && uidFieldIds.Contains(fv.FieldDefinitionId))
            .Select(fv => new { fv.DocumentId, fv.Value })
            .ToDictionaryAsync(fv => fv.DocumentId, fv => fv.Value, cancellationToken);

        var items = new List<DavItem>();
        foreach (var document in documents)
        {
            if (await ToItemAsync(db, protocol, document, uids.GetValueOrDefault(document.Id), cancellationToken) is { } item)
            {
                items.Add(item);
            }
        }

        return items;
    }

    /// <summary>
    /// One item by its DAV resource name, resolved without listing the collection — the path a syncing client
    /// hits once per item. Matches on the UID field, falling back to the document id (which is what an item
    /// with no UID is named after).
    /// </summary>
    internal static async Task<DavItem?> ItemAsync(
        SimplArchiveDbContext db, DavProtocol protocol, Guid folderId, string resourceName, CancellationToken cancellationToken)
    {
        if (!resourceName.EndsWith(protocol.Extension, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var uid = resourceName[..^protocol.Extension.Length];
        var uidFieldIds = await UidFieldIdsAsync(db, protocol, cancellationToken);
        var fallbackId = Guid.TryParse(uid, out var parsed) ? parsed : (Guid?)null;

        var document = await ItemQuery(db, protocol, folderId)
            .Where(d => db.FieldValues.Any(fv => fv.DocumentId == d.Id && uidFieldIds.Contains(fv.FieldDefinitionId) && fv.Value == uid)
                || (fallbackId != null && d.Id == fallbackId))
            .Select(d => new ItemRow(d.Id, d.CurrentVersionId, d.ConcurrencyToken))
            .FirstOrDefaultAsync(cancellationToken);

        return document is null ? null : await ToItemAsync(db, protocol, document, uid, cancellationToken);
    }

    // ---- Shared shapes + queries ---------------------------------------------------------------------

    private sealed record FolderRow(Guid Id, string Name, Guid? ParentId);

    private sealed record ParentRow(Guid Id, string Name, Guid? PersonalOfUserId);

    private sealed record ItemRow(Guid Id, Guid? CurrentVersionId, Guid ConcurrencyToken);

    // Parent-qualified display name (decided): two folders named "Deadlines" under different parents must be
    // distinguishable in a client's collection list, and the full path is too long for one.
    private static string DisplayName(FolderRow folder, ParentRow? parent) =>
        parent is null ? folder.Name : $"{parent.Name} / {folder.Name}";

    private static IQueryable<Document> FolderQuery(SimplArchiveDbContext db, DavProtocol protocol) =>
        db.Documents.Where(d => d.MaskVersionId != null
            && db.MaskVersions.Any(v => v.Id == d.MaskVersionId && v.MaskId == protocol.FolderMaskId));

    private static IQueryable<Document> ItemQuery(SimplArchiveDbContext db, DavProtocol protocol, Guid folderId) =>
        db.Documents.Where(d => d.ParentId == folderId && d.MaskVersionId != null
            && db.MaskVersions.Any(v => v.Id == d.MaskVersionId && v.MaskId == protocol.ItemMaskId));

    private static async Task<List<Guid>> UidFieldIdsAsync(SimplArchiveDbContext db, DavProtocol protocol, CancellationToken cancellationToken) =>
        await db.FieldDefinitions
            .Where(f => f.Name == protocol.UidFieldName
                && db.MaskVersions.Any(v => v.Id == f.MaskVersionId && v.MaskId == protocol.ItemMaskId))
            .Select(f => f.Id)
            .ToListAsync(cancellationToken);

    private static async Task<DavItem?> ToItemAsync(
        SimplArchiveDbContext db, DavProtocol protocol, ItemRow document, string? uid, CancellationToken cancellationToken)
    {
        var version = await CurrentVersion.ResolveAsync(db.DocumentVersions, document.Id, document.CurrentVersionId, cancellationToken);
        if (version?.ObjectKey is not { Length: > 0 } objectKey)
        {
            return null;
        }

        // A UID is guaranteed by classification (it falls back to the document id), but an item filed by some
        // other path might lack one — the document id keeps the resource addressable either way.
        var resourceUid = uid is { Length: > 0 } value ? value : document.Id.ToString();

        return new DavItem(
            DocumentId: document.Id,
            ResourceName: resourceUid + protocol.Extension,
            ObjectKey: objectKey,
            // The document's concurrency token is already the API's ETag for it (ADR 0188); reusing it means a
            // DAV If-Match and an API If-Match are the same value for the same resource.
            ETag: document.ConcurrencyToken.ToString(),
            LastModified: version.CreatedAt);
    }
}
