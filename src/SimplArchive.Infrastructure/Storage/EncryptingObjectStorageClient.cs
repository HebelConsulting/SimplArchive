using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Tenants;

namespace SimplArchive.Infrastructure.Storage;

/// <summary>
/// The at-rest encryption decorator (ADR 0818): wraps the real S3 client at the ONE DI seam every
/// server-side storage call crosses, so every write to a gated tenant stores ciphertext with its wrapped
/// DEK riding as object metadata, and every read hands plaintext back — sidecars, stashes, mail, WORM
/// segments, all of it, with S3's server-side COPY carrying the metadata through the re-keying flows.
/// An object WITHOUT the metadata is plaintext and passes through untouched — the mixed state that makes
/// rollout additive (every pre-existing object) rather than a migration.
/// </summary>
public sealed class EncryptingObjectStorageClient(
    S3ObjectStorageClient inner,
    AtRestKeyService keys,
    IEncryptedContentUrlIssuer? urlIssuer,
    ILogger<EncryptingObjectStorageClient> logger) : IObjectStorageClient
{
    /// <summary>Object-metadata keys — the wrapped DEK and its KEK generation (ADR 0818).</summary>
    public const string WrappedDekKey = "sa-wrapped-dek";

    public const string KekGenerationKey = "sa-kek-generation";

    private static readonly TimeSpan ProxyUrlExpiry = TimeSpan.FromMinutes(15);

    // ---- Writes ---------------------------------------------------------------------------------------

    public Task PutObjectAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken = default) =>
        PutObjectAsync(objectKey, content, contentType, new Dictionary<string, string>(), cancellationToken);

    public async Task PutObjectAsync(string objectKey, Stream content, string contentType,
        IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default)
    {
        if (!await keys.GatedAsync(objectKey, cancellationToken))
        {
            await inner.PutObjectAsync(objectKey, content, contentType, metadata, cancellationToken);
            return;
        }

        // Whole-object encrypt: AES-GCM authenticates the blob as one unit. Server-side writes are
        // renditions, protocol bodies and staged copies — bounded sizes; the multi-GB direct-to-storage
        // path is the PRESIGNED upload, which never passes through here (client-side encryption, slice B2).
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        var (dek, wrappedDek, generation) = await keys.MintDekAsync(cancellationToken);
        var ciphertext = AtRestBlobCipher.Encrypt(dek, buffer.GetBuffer().AsSpan(0, (int)buffer.Length));

        var enriched = new Dictionary<string, string>(metadata)
        {
            [WrappedDekKey] = wrappedDek,
            [KekGenerationKey] = generation,
        };
        await inner.PutObjectAsync(objectKey, new MemoryStream(ciphertext), contentType, enriched, cancellationToken);
    }

    // ---- Reads ----------------------------------------------------------------------------------------

    public async Task<Stream> GetObjectAsync(string objectKey, CancellationToken cancellationToken = default) =>
        (await GetObjectWithMetadataAsync(objectKey, cancellationToken)).Content;

    /// <summary>Refuses for a strict tenant; an ordinary read otherwise (#1394, ADR 0829).</summary>
    /// <remarks>
    /// The refusal is per TENANT, not per caller — which is why it can live here at all, while the ENVELOPE
    /// for download and preview cannot (an envelope is addressed to a person, and this seam has no idea who
    /// is asking; ADR 0828).
    /// </remarks>
    public async Task<Stream> GetObjectForClientAsync(string objectKey, string door, CancellationToken cancellationToken = default)
    {
        if (await keys.StrictAsync(objectKey, cancellationToken))
        {
            throw new PlaintextContentRefusedException(door);
        }

        return await GetObjectAsync(objectKey, cancellationToken);
    }

    /// <summary>The range form of the same refusal — a seeking client is still a client.</summary>
    public async Task<Stream> GetObjectRangeForClientAsync(
        string objectKey, long from, long to, string door, CancellationToken cancellationToken = default)
    {
        if (await keys.StrictAsync(objectKey, cancellationToken))
        {
            throw new PlaintextContentRefusedException(door);
        }

        return await GetObjectRangeAsync(objectKey, from, to, cancellationToken);
    }

    public async Task<StoredObject> GetObjectWithMetadataAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        var stored = await inner.GetObjectWithMetadataAsync(objectKey, cancellationToken);
        if (!stored.Metadata.TryGetValue(WrappedDekKey, out var wrappedDek))
        {
            return stored;
        }

        await using var ciphertext = stored.Content;
        using var buffer = new MemoryStream();
        await ciphertext.CopyToAsync(buffer, cancellationToken);
        var dek = await keys.UnwrapDekAsync(wrappedDek, stored.Metadata[KekGenerationKey], cancellationToken);
        var plaintext = AtRestBlobCipher.Decrypt(dek, buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        return stored with { Content = new MemoryStream(plaintext), Length = plaintext.Length };
    }

    public async Task<Stream> GetObjectRangeAsync(string objectKey, long from, long to, CancellationToken cancellationToken = default)
    {
        // Mixed state means the probe is per object, not per tenant: a real range read stays a real S3
        // range for plaintext objects; an encrypted one is fetched whole, decrypted, sliced — GCM
        // authenticates the blob as one unit, so there is no honest partial decrypt.
        var info = await inner.GetObjectInfoAsync(objectKey, cancellationToken);
        if (!info.Metadata.ContainsKey(WrappedDekKey))
        {
            return await inner.GetObjectRangeAsync(objectKey, from, to, cancellationToken);
        }

        await using var full = await GetObjectAsync(objectKey, cancellationToken);
        var buffer = (MemoryStream)full;
        var length = (int)Math.Max(0, Math.Min(to, buffer.Length - 1) - from + 1);
        return new MemoryStream(buffer.GetBuffer(), (int)from, length, writable: false);
    }

    public async Task<long> GetObjectSizeAsync(string objectKey, CancellationToken cancellationToken = default) =>
        (await GetObjectInfoAsync(objectKey, cancellationToken)).Size;

    public async Task<StoredObjectInfo> GetObjectInfoAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        // Callers use the size for quotas, listings and content-length claims about what a GET will
        // serve — and a GET serves PLAINTEXT, so the size must describe it (a 28-byte lie breaks WebDAV
        // clients that compare getcontentlength against the download).
        var info = await inner.GetObjectInfoAsync(objectKey, cancellationToken);
        return info.Metadata.ContainsKey(WrappedDekKey)
            ? info with { Size = info.Size - AtRestBlobCipher.Overhead }
            : info;
    }

    // ---- Presigned URLs -------------------------------------------------------------------------------

    public Task<Uri> GetPresignedUploadUrlAsync(string objectKey, TimeSpan expiry, CancellationToken cancellationToken = default) =>
        // Uploads stay presigned even on gated tenants: the app clients encrypt CLIENT-side (B2 — the
        // initiate response carries the KEK instruction, finalize attaches the wrapped DEK); a client
        // that doesn't (an older or third-party API caller) lands plaintext-without-metadata, which
        // reads fine by the mixed-state contract. Server-side writers are already covered above.
        inner.GetPresignedUploadUrlAsync(objectKey, expiry, cancellationToken);

    public async Task<Uri?> GetPresignedDownloadUrlAsync(string objectKey, TimeSpan expiry, string? downloadFileName = null, CancellationToken cancellationToken = default) =>
        await RefusedByStrictTierAsync(objectKey, cancellationToken) ? null
        : await SwapIfEncryptedAsync(objectKey, expiry, downloadFileName, contentType: null, inline: false, cancellationToken)
            ?? await inner.GetPresignedDownloadUrlAsync(objectKey, expiry, downloadFileName, cancellationToken);

    public async Task<Uri?> GetPresignedPreviewUrlAsync(string objectKey, TimeSpan expiry, string? fileName = null, string? contentType = null, CancellationToken cancellationToken = default) =>
        await RefusedByStrictTierAsync(objectKey, cancellationToken) ? null
        : await SwapIfEncryptedAsync(objectKey, expiry, fileName, contentType, inline: true, cancellationToken)
            ?? await inner.GetPresignedPreviewUrlAsync(objectKey, expiry, fileName, contentType, cancellationToken);

    /// <summary>
    /// True when this object belongs to a strict-tier tenant, whose content never leaves as plaintext.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Checked BEFORE the encrypted swap, and that order is the point. For an ordinary encrypted tenant the
    /// swap hands the browser the token door, which serves DECRYPTED bytes over HTTPS — exactly the plaintext
    /// door the strict tier must not have. Letting the swap run first would replace one plaintext door with
    /// another and look like it had done something.
    /// </para>
    /// <para>
    /// Answering NULL rather than throwing, because presigning happens while BUILDING resources: a thrown
    /// refusal would take the document's metadata with it, leaving a strict tenant able to see nothing at all.
    /// A caller omits the link instead, which is ADR 0543's own signal — a missing rel means "not available to
    /// you, here, now".
    /// </para>
    /// <para>
    /// READS only. An upload presign stays available: the client encrypts before it sends, so the bytes
    /// crossing that URL are already ciphertext (see GetPresignedUploadUrlAsync's own note).
    /// </para>
    /// </remarks>
    private Task<bool> RefusedByStrictTierAsync(string objectKey, CancellationToken cancellationToken) =>
        keys.StrictAsync(objectKey, cancellationToken);

    /// <summary>
    /// The token door for an object that carries a wrapped DEK, whatever its tenant's mode is TODAY.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The OBJECT is asked, never the gate.</b> This used to short-circuit on
    /// <see cref="AtRestKeyService.GatedAsync"/> — one HEAD saved per presign on tenants that encrypt nothing
    /// — and that made an encrypted object unreadable the moment its tenant's mode was turned OFF: the presign
    /// fell through to object storage, and the browser was handed CIPHERTEXT with a 200 and no error anywhere.
    /// Turning encryption off for a tenant must not make that tenant's existing content unreadable, and a
    /// mode is a decision about what to do with the NEXT write, not a claim about what is already stored.
    /// </para>
    /// <para>
    /// ADR 0818 always said the mixed state is by design, but only implemented one half of it — a plaintext
    /// object on a gated tenant. The other half is an encrypted object on a tenant that is no longer gated,
    /// and it is the half that loses data in practice rather than failing safe.
    /// </para>
    /// <para>
    /// <b>Not hypothetical:</b> the public kiosk's Demo tenant spent a day encrypted (its config named a key
    /// the running image did not read, so the absent legacy list meant "every tenant"), and the release that
    /// corrected the gating would, without this, have served ciphertext for every document in the demo.
    /// </para>
    /// <para>
    /// The price is one HEAD per presign in an installation that has encryption configured at all — a fully
    /// inert one never resolves this decorator, and a gated tenant already paid exactly this.
    /// </para>
    /// </remarks>
    private async Task<Uri?> SwapIfEncryptedAsync(string objectKey, TimeSpan expiry, string? fileName,
        string? contentType, bool inline, CancellationToken cancellationToken)
    {
        var info = await inner.GetObjectInfoAsync(objectKey, cancellationToken);
        if (!info.Metadata.ContainsKey(WrappedDekKey))
        {
            return null; // a plaintext object — presigned is correct, gated tenant or not (the mixed state)
        }

        if (urlIssuer is null)
        {
            // A host without the Api's issuer (no routes to serve the proxy) can only hand out the
            // ciphertext link — name it loudly rather than failing the whole listing.
            logger.LogWarning("Object {ObjectKey} is encrypted but no content-URL issuer is registered — "
                + "the presigned URL will serve ciphertext.", objectKey);
            return null;
        }

        return urlIssuer.Issue(objectKey, expiry == default ? ProxyUrlExpiry : expiry, fileName, contentType, inline);
    }

    // ---- Pass-throughs --------------------------------------------------------------------------------

    public Task EnsureTenantBucketAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        inner.EnsureTenantBucketAsync(tenantId, cancellationToken);

    public Task SetBucketLifecycleAsync(Guid tenantId, int incompleteUploadCleanupDays, CancellationToken cancellationToken = default) =>
        inner.SetBucketLifecycleAsync(tenantId, incompleteUploadCleanupDays, cancellationToken);

    public Task<bool> ExistsAsync(string objectKey, CancellationToken cancellationToken = default) =>
        inner.ExistsAsync(objectKey, cancellationToken);

    public Task<IReadOnlyList<StorageObject>> ListObjectsAsync(string prefix, CancellationToken cancellationToken = default) =>
        // Listing sizes stay STORED sizes (+28 for encrypted objects): S3 LIST carries no metadata, and a
        // per-object HEAD would turn one call into N+1. The only consumers are user-area scans; noted in
        // ADR 0818 as the accepted 28-byte skew there.
        inner.ListObjectsAsync(prefix, cancellationToken);

    public async Task CopyObjectAsync(string sourceKey, string destinationKey, CancellationToken cancellationToken = default)
    {
        // S3 COPY carries the metadata — the wrapped DEK travels with the bytes through every re-keying
        // flow (mail→archive, intray→document, stash→version), which is the whole reason the DEK lives
        // ON the object (ADR 0818). The one copy that must NOT stay a plain copy: a PLAINTEXT source
        // (a client's presigned staging upload) filed into a gated destination — that is the moment the
        // object becomes archive content, so it gets encrypted on the way (read, encrypt, put) rather
        // than copied plaintext into the claim's blind spot.
        if (await keys.GatedAsync(destinationKey, cancellationToken))
        {
            var info = await inner.GetObjectInfoAsync(sourceKey, cancellationToken);
            if (!info.Metadata.ContainsKey(WrappedDekKey))
            {
                await using var plaintext = await inner.GetObjectAsync(sourceKey, cancellationToken);
                await PutObjectAsync(destinationKey, plaintext,
                    info.ContentType ?? "application/octet-stream", cancellationToken);
                return;
            }
        }

        await inner.CopyObjectAsync(sourceKey, destinationKey, cancellationToken);
    }

    public Task<IReadOnlyList<StoredObjectVersion>> ListObjectVersionsAsync(string prefix, CancellationToken cancellationToken = default) =>
        // Metadata, not content — nothing to decrypt, and the wrapped DEK is exactly what the caller wants.
        inner.ListObjectVersionsAsync(prefix, cancellationToken);

    public Task SetObjectMetadataAsync(string objectKey, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default) =>
        // The attach path for client-encrypted uploads — the payload IS encryption metadata, so it must
        // reach the store verbatim, never pass through the encrypting write.
        inner.SetObjectMetadataAsync(objectKey, metadata, cancellationToken);

    public Task DeleteObjectAsync(string objectKey, CancellationToken cancellationToken = default) =>
        inner.DeleteObjectAsync(objectKey, cancellationToken);

    public Task SetRetentionAsync(string objectKey, DateTimeOffset retainUntil, WormLockMode mode, CancellationToken cancellationToken = default) =>
        inner.SetRetentionAsync(objectKey, retainUntil, mode, cancellationToken);

    public Task SetLegalHoldAsync(string objectKey, bool held, CancellationToken cancellationToken = default) =>
        inner.SetLegalHoldAsync(objectKey, held, cancellationToken);

    public Task<ObjectLockStatus> GetLockStatusAsync(string objectKey, CancellationToken cancellationToken = default) =>
        inner.GetLockStatusAsync(objectKey, cancellationToken);
}
