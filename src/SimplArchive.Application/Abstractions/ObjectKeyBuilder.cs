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
