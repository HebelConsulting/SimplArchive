namespace SimplArchive.Application.Abstractions;

// Builds the object key structure decided by ADR "Object storage key structure", refined to group by document
// (ADR 0530): tenants/{tenantId}/{versionFilingYear}/{storageFolderId}/{versionId}{ext}. The storageFolderId is
// the document's opaque folder twin (Document.StorageFolderId — a random per-document GUID, not the business id,
// preserving ADR 0064 opacity), so a document's versions + their derived artifacts (preview renditions,
// text-layout, …) group under one folder per year. The versionId names the content file; the original file
// extension rides on it so the stored object (and the presigned download it produces) carries the correct type
// (ADR "Object key file extension") and type-sniffing keeps working. The {year} is the VERSION's filing year (ADR
// 0520): versions filed in one year share a folder (a backdated version buckets under its own filing year), which
// also keeps backup partitioning by year (immutable WORM objects, so an incremental backup copies each once). Pure
// functions, no I/O, so not part of IObjectStorageClient.
public static class ObjectKeyBuilder
{
    // The content object of a version, grouped in its document's folder, bucketed by the version's filing year:
    // tenants/{tenantId}/{filingDate.Year}/{storageFolderId}/{versionId}{ext}.
    public static string Build(Guid tenantId, DateTimeOffset filingDate, Guid storageFolderId, Guid versionId, string? extension = null)
    {
        var suffix = string.IsNullOrWhiteSpace(extension)
            ? string.Empty
            : extension.StartsWith('.') ? extension : $".{extension}";

        return $"tenants/{tenantId}/{filingDate.Year}/{storageFolderId}/{versionId}{suffix}";
    }

    // The EPHEMERAL key of a delivered message, under the per-user mail prefix rather than the archive's
    // year buckets: tenants/{tenantId}/users/{userId}/mail/{storageFolderId}/{versionId}{ext} (ADR 0628, #633).
    //
    // The point is not the path but what the path MEANS. An inbox is not an archive: deleting there is just
    // deleting, with no retention schedule and no disposition review — an archive that makes you dispose of
    // spam is worse than no inbox at all. Keeping those bytes somewhere the archive's rules do not reach is
    // what makes that true of the storage and not merely of the folder's mask.
    //
    // The {storageFolderId}/{versionId} tail is deliberately the same shape as the archive key's, so
    // <see cref="DerivedKey"/> groups a message's preview rendition beside it exactly as it does for a filed
    // document, and nothing downstream needs to know which kind of key it holds.
    public static string EphemeralMailKey(Guid tenantId, Guid userId, Guid storageFolderId, Guid versionId, string? extension = null)
    {
        var suffix = string.IsNullOrWhiteSpace(extension)
            ? string.Empty
            : extension.StartsWith('.') ? extension : $".{extension}";

        return $"tenants/{tenantId}/users/{userId}/mail/{storageFolderId}/{versionId}{suffix}";
    }

    // The department-mailbox counterpart (#703 PR 4): a message delivered to a claimed mailbox with no
    // personal-space owner has no user to file under, and writing `users/{mailboxId}` would be a path that
    // lies. Same lifecycle as the personal key — ephemeral until the user files it out.
    public static string DepartmentMailKey(Guid tenantId, Guid mailboxDocumentId, Guid storageFolderId, Guid versionId, string? extension = null)
    {
        var suffix = string.IsNullOrWhiteSpace(extension)
            ? string.Empty
            : extension.StartsWith('.') ? extension : $".{extension}";

        return $"tenants/{tenantId}/mailboxes/{mailboxDocumentId}/mail/{storageFolderId}/{versionId}{suffix}";
    }

    // Whether a key names ephemeral mail storage — the question "has this document's content crossed into the
    // archive yet?", asked of the key rather than of the folder, because the folder is what a move is changing.
    //
    // Matched on the two fixed segments rather than on a prefix built from ids the caller may not have: a
    // caller holding only a version's key (the move seam does) can still answer it. `users/` alone would be
    // too loose the day anything else lives under a user.
    public static bool IsEphemeralMailKey(string objectKey) =>
        (objectKey.Contains("/users/", StringComparison.Ordinal)
            || objectKey.Contains("/mailboxes/", StringComparison.Ordinal))
        && objectKey.Contains("/mail/", StringComparison.Ordinal);

    // A sibling content key in the SAME document folder as an existing version's key, for a *new version* of that
    // document (e.g. the OCR searchable-PDF successor) — keeps the same tenants/{t}/{year}/{storageFolderId}/
    // directory, with a fresh {versionId}{ext} leaf (ADR 0530). Used when the caller has an existing version's key
    // rather than the document, so the successor lands in the document's folder without re-deriving the segments.
    public static string SiblingVersionKey(string existingVersionKey, Guid versionId, string? extension = null)
    {
        var suffix = string.IsNullOrWhiteSpace(extension)
            ? string.Empty
            : extension.StartsWith('.') ? extension : $".{extension}";

        var lastSlash = existingVersionKey.LastIndexOf('/');
        var directory = lastSlash >= 0 ? existingVersionKey[..(lastSlash + 1)] : string.Empty;
        return $"{directory}{versionId}{suffix}";
    }

    // A derived-artifact key that lives **next to** a content key (same {guid}/ directory), its inner filename's
    // extension replaced by `suffix` — e.g. "…/{guid}/content.pdf" + ".preview.png" → "…/{guid}/content.preview.png".
    // The single scheme every derived-artifact service shares (renditions, per-page images, text-layout), so all of
    // a document's files stay grouped under its GUID folder. Keeping the inner stem (`content`) means the same helper
    // is collision-safe for the name-based intray staging keys too ("inbox/{name}.tif" → "inbox/{name}.preview.png").
    public static string DerivedKey(string baseKey, string suffix)
    {
        var lastSlash = baseKey.LastIndexOf('/');
        var directory = lastSlash >= 0 ? baseKey[..(lastSlash + 1)] : string.Empty;
        var fileName = lastSlash >= 0 ? baseKey[(lastSlash + 1)..] : baseKey;

        var lastDot = fileName.LastIndexOf('.');
        var stem = lastDot >= 0 ? fileName[..lastDot] : fileName;

        return $"{directory}{stem}{suffix}";
    }
}
