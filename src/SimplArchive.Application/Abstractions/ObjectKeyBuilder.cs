namespace SimplArchive.Application.Abstractions;

// Builds the object key structure decided by ADR "Object storage key structure" and completed by ADR
// "Object storage client abstraction (foundation slice)": tenants/{tenantId}/{filingYear}/{guid}{ext}. A
// fresh GUID per version keeps the storage layer opaque to document identity — no business identifier
// (documentId) leaks into infrastructure paths. The original file extension is appended so the stored
// object (and the presigned download it produces) carries the correct type (ADR "Object key file
// extension"); an extension is a file type, not an identity, so it doesn't break that opacity. Pure
// function, no I/O, so it isn't part of IObjectStorageClient's own surface.
public static class ObjectKeyBuilder
{
    public static string Build(Guid tenantId, DateTimeOffset filingDate, string? extension = null)
    {
        var suffix = string.IsNullOrWhiteSpace(extension)
            ? string.Empty
            : extension.StartsWith('.') ? extension : $".{extension}";

        return $"tenants/{tenantId}/{filingDate.Year}/{Guid.NewGuid()}{suffix}";
    }
}
