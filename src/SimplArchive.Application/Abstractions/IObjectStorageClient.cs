using SimplArchive.Domain.Tenants;

namespace SimplArchive.Application.Abstractions;

// One object listed under a prefix (ADR "S3-backed inbox") — its full key, byte size, and last-modified time.
public sealed record StorageObject(string Key, long Size, DateTimeOffset LastModified);

// The S3 Object Lock state of an object (ADR "WORM / immutable document versions"): a retain-until date (null
// = no retention lock) and whether an object legal hold is on. An object is immutable while RetainUntil is in
// the future OR LegalHold is on.
public sealed record ObjectLockStatus(DateTimeOffset? RetainUntil, bool LegalHold)
{
    public bool IsLocked(DateTimeOffset now) => LegalHold || (RetainUntil is { } until && until > now);
}

// A storage abstraction over an S3-compatible object store — see ADR "Document storage model", ADR
// "File upload / download API design". The API never proxies file bytes; it issues short-lived
// presigned URLs and the client talks to storage directly. Implemented in SimplArchive.Infrastructure,
// where the actual S3 client SDK usage lives — see ADR "Object storage client abstraction
// (foundation slice)".
public interface IObjectStorageClient
{
    // Idempotently creates a tenant's own object-storage bucket (ADR "Per-tenant object-storage bucket") —
    // object-lock-enabled (for WORM) + browser CORS + ops tags (ADR "Per-tenant bucket policy knobs"). Called at
    // tenant provisioning; every object key is tenant-scoped, so all other methods route to the right bucket.
    Task EnsureTenantBucketAsync(Guid tenantId, CancellationToken cancellationToken = default);

    // Applies (or, when 0, removes) the tenant bucket's lifecycle policy (ADR "Per-tenant bucket policy knobs"):
    // abort incomplete multipart uploads after incompleteUploadCleanupDays. Called at provisioning + whenever the
    // tenant setting changes. Best-effort against a backend that doesn't run lifecycle.
    Task SetBucketLifecycleAsync(Guid tenantId, int incompleteUploadCleanupDays, CancellationToken cancellationToken = default);

    Task<Uri> GetPresignedUploadUrlAsync(string objectKey, TimeSpan expiry, CancellationToken cancellationToken = default);

    // downloadFileName (optional): overrides the filename the browser saves the download as, via the S3
    // response-content-disposition parameter (signed into the URL) — see ADR "Download filename from Short
    // Description". When null, the object key's own last segment is used, as before.
    Task<Uri> GetPresignedDownloadUrlAsync(string objectKey, TimeSpan expiry, string? downloadFileName = null, CancellationToken cancellationToken = default);

    // Like the download URL but with Content-Disposition: inline so the browser renders the content in place
    // (used by the document preview) rather than forcing a download — see ADR "Repositories workbench UI".
    // contentType (optional) overrides the response Content-Type via the S3 response-content-type parameter
    // — used to force text/plain for .txt so the browser renders it inline regardless of the stored type
    // (ADR "Plain-text inline preview").
    Task<Uri> GetPresignedPreviewUrlAsync(string objectKey, TimeSpan expiry, string? fileName = null, string? contentType = null, CancellationToken cancellationToken = default);

    // Used only at finalize-upload time to compute the SHA256 hash server-side rather than trusting a
    // client-supplied value — see ADR "Document version upload/download endpoints (pragmatic slice)".
    // Not part of the direct-to-storage bulk-transfer path; the caller is expected to read and dispose
    // the stream promptly.
    Task<Stream> GetObjectAsync(string objectKey, CancellationToken cancellationToken = default);

    // Reads an inclusive byte range [from, to] of an object (server-side range request) — backs WebDAV Range
    // GET / 206 Partial Content (ADR "WebDAV hardening"). Default: read the whole object and slice; the S3
    // client overrides this with a real range request. (A default method so test fakes needn't implement it.)
    async Task<Stream> GetObjectRangeAsync(string objectKey, long from, long to, CancellationToken cancellationToken = default)
    {
        await using var full = await GetObjectAsync(objectKey, cancellationToken);
        var buffer = new MemoryStream();
        await full.CopyToAsync(buffer, cancellationToken);
        var length = (int)Math.Max(0, Math.Min(to, buffer.Length - 1) - from + 1);
        return new MemoryStream(buffer.GetBuffer(), (int)from, length, writable: false);
    }

    // Server-side existence check (a HEAD) — used to see whether a cached preview rendition already exists
    // before regenerating it. See ADR "Server-side preview renditions for non-browser-viewable images".
    Task<bool> ExistsAsync(string objectKey, CancellationToken cancellationToken = default);

    // The object's size in bytes (a HEAD) — used for storage-quota accounting/enforcement at finalize (ADR
    // "Per-tenant storage quota").
    Task<long> GetObjectSizeAsync(string objectKey, CancellationToken cancellationToken = default);

    // Server-side write — the Api itself uploads bytes (not the direct-to-storage presigned path), used to
    // store a generated preview rendition. See ADR "Server-side preview renditions for non-browser-viewable
    // images".
    Task PutObjectAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken = default);

    // Lists every object under a key prefix — the S3-backed intray (`{tenantId}/users/{userId}/inbox/`) enumerates
    // itself this way. See ADR "S3-backed inbox".
    Task<IReadOnlyList<StorageObject>> ListObjectsAsync(string prefix, CancellationToken cancellationToken = default);

    // Server-side copy within the bucket (no bytes leave storage) — filing an intray item moves its object to
    // a document key with a copy + delete. See ADR "S3-backed inbox".
    Task CopyObjectAsync(string sourceKey, string destinationKey, CancellationToken cancellationToken = default);

    Task DeleteObjectAsync(string objectKey, CancellationToken cancellationToken = default);

    // ---- WORM / S3 Object Lock (ADR "WORM / immutable document versions") ----
    // The bucket must be object-lock-enabled (versioning + object lock) for these to have effect.

    // Sets (or extends) an Object Lock retention on the object until retainUntil, in the given mode. Retention
    // can only be extended — attempting to shorten it (Compliance always; Governance without bypass) fails at
    // the storage layer; the caller treats that as a no-op.
    Task SetRetentionAsync(string objectKey, DateTimeOffset retainUntil, WormLockMode mode, CancellationToken cancellationToken = default);

    // Turns the object's mode-less legal hold on or off. A legal hold blocks deletion independently of, and
    // with no expiry unlike, a retention lock.
    Task SetLegalHoldAsync(string objectKey, bool held, CancellationToken cancellationToken = default);

    // Reads the object's current lock state (retain-until + legal hold). Used to refuse a purge of a still-locked
    // blob. Returns an all-null/false status when the object has no lock configuration.
    Task<ObjectLockStatus> GetLockStatusAsync(string objectKey, CancellationToken cancellationToken = default);
}
