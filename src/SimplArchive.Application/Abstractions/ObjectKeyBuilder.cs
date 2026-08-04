namespace SimplArchive.Application.Abstractions;

// Builds the object key structure decided by ADR "Object storage key structure" and completed by ADR
// "Object storage client abstraction (foundation slice)": tenants/{tenantId}/{filingYear}/{guid}/content{ext}.
// A fresh GUID per version keeps the storage layer opaque to document identity — no business identifier
// (documentId) leaks into infrastructure paths. The GUID is its **own directory segment** so a document's
// content plus every derived artifact (preview renditions, text-layout, …) group under one folder instead of
// piling up as flat siblings at the {year}/ level (issue #338). The original file extension rides on the inner
// `content` filename so the stored object (and the presigned download it produces) carries the correct type
// (ADR "Object key file extension") and type-sniffing keeps working; an extension is a file type, not an
// identity, so it doesn't break that opacity. Pure functions, no I/O, so not part of IObjectStorageClient.
public static class ObjectKeyBuilder
{
    // The content object of a fresh version: tenants/{tenantId}/{filingYear}/{guid}/content{ext}.
    public static string Build(Guid tenantId, DateTimeOffset filingDate, string? extension = null)
    {
        var suffix = string.IsNullOrWhiteSpace(extension)
            ? string.Empty
            : extension.StartsWith('.') ? extension : $".{extension}";

        return $"tenants/{tenantId}/{filingDate.Year}/{Guid.NewGuid()}/content{suffix}";
    }

    // A derived-artifact key that lives **next to** a content key (same {guid}/ directory), its inner filename's
    // extension replaced by `suffix` — e.g. "…/{guid}/content.pdf" + ".preview.png" → "…/{guid}/content.preview.png".
    // The single scheme every derived-artifact service shares (renditions, per-page images, text-layout), so all of
    // a document's files stay grouped under its GUID folder. Keeping the inner stem (`content`) means the same helper
    // is collision-safe for the name-based inbox staging keys too ("inbox/{name}.tif" → "inbox/{name}.preview.png").
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
