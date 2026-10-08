using System.Globalization;
using System.Security.Cryptography;
using SimplArchive.Application.Abstractions;
using SimplArchive.ModuleAbi;

namespace SimplArchive.Infrastructure.Modules;

/// <summary>
/// A module's presigned uploads (ABI 1.12, ADR 0910): handed out, read back, filed or discarded. The facade forwards
/// here; filing itself goes through the facade's one content-write path.
/// </summary>
/// <remarks>
/// <para>
/// <b>No row: the object is the state</b>, as for the intray. An upload lives at
/// <c>tenants/{t}/module-uploads/{module}/{id}</c> until it is filed (moved into the archive keyspace) or discarded,
/// and <see cref="ModuleUploadSweepWorker"/> deletes what is left after a day. A refused upload never becomes a
/// document, so it leaves nothing in the archive or the audit trail.
/// </para>
/// <para>
/// <b>The id carries the limit</b> (<c>{guid}.{maxBytes}</c>), because there is no row to keep it in. It is the module's
/// own guard against its own clients, so a module reading its own id back is not a trust boundary. The MODULE segment
/// is: the key is built from the request's module identity, never from the id, so one module cannot open another's.
/// </para>
/// <para>
/// <b>Plaintext while staged.</b> The PUT goes straight to storage, unencrypted, like every presigned staging upload.
/// Filing copies it into the archive keyspace through the storage seam, which encrypts it there for a tenant that
/// wraps at rest; the hash and size describe the plaintext, as everywhere.
/// </para>
/// </remarks>
public sealed class ModuleUploads(IObjectStorageClient storage, ICurrentTenantAccessor tenant, ModuleIdentityAccessor identity)
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(1);

    public async Task<ModuleUpload> BeginAsync(long maxBytes, TimeSpan? lifetime, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        var valid = lifetime is not { } asked || asked <= TimeSpan.Zero ? DefaultLifetime : asked > MaxLifetime ? MaxLifetime : asked;
        var uploadId = $"{Guid.NewGuid():N}.{maxBytes.ToString(CultureInfo.InvariantCulture)}";
        var url = await storage.GetPresignedUploadUrlAsync(KeyOf(uploadId, out _), valid, cancellationToken);
        return new ModuleUpload(uploadId, url, DateTimeOffset.UtcNow.Add(valid), maxBytes);
    }

    /// <summary>The upload spooled to a temporary file deleted on dispose: seekable, and never held in memory.</summary>
    public async Task<Stream> OpenAsync(string uploadId, CancellationToken cancellationToken)
    {
        var key = await CheckedKeyAsync(uploadId, cancellationToken);
        var spool = new FileStream(Path.Combine(Path.GetTempPath(), $"sa-upload-{Guid.NewGuid():N}"), FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            await using (var source = await storage.GetObjectAsync(key, cancellationToken))
            {
                await source.CopyToAsync(spool, cancellationToken);
            }

            spool.Position = 0;
            return spool;
        }
        catch
        {
            await spool.DisposeAsync();
            throw;
        }
    }

    /// <summary>Moves the upload to <paramref name="destinationKey"/> and answers the plaintext's SHA-256 and size.</summary>
    public async Task<(string Sha256, long Size)> MoveAsync(string uploadId, string destinationKey, CancellationToken cancellationToken)
    {
        var key = await CheckedKeyAsync(uploadId, cancellationToken);
        string sha256;
        long size;
        await using (var source = await storage.GetObjectAsync(key, cancellationToken))
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buffer = new byte[81920];
            size = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                size += read;
            }

            sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
        }

        await storage.CopyObjectAsync(key, destinationKey, cancellationToken);
        await storage.DeleteObjectAsync(key, cancellationToken);
        return (sha256, size);
    }

    public Task DiscardAsync(string uploadId, CancellationToken cancellationToken) =>
        storage.DeleteObjectAsync(KeyOf(uploadId, out _), cancellationToken);

    /// <summary>The key, after checking the upload exists and is within its limit; a too-large one is discarded.</summary>
    private async Task<string> CheckedKeyAsync(string uploadId, CancellationToken cancellationToken)
    {
        var key = KeyOf(uploadId, out var maxBytes);
        if (!await storage.ExistsAsync(key, cancellationToken))
        {
            throw new ModuleUploadMissingException(uploadId);
        }

        var size = await storage.GetObjectSizeAsync(key, cancellationToken);
        if (size > maxBytes)
        {
            await storage.DeleteObjectAsync(key, cancellationToken);
            throw new ModuleUploadTooLargeException(size, maxBytes);
        }

        return key;
    }

    private string KeyOf(string uploadId, out long maxBytes)
    {
        var parts = uploadId.Split('.');
        if (parts is not [var id, var max] || !Guid.TryParseExact(id, "N", out _)
            || !long.TryParse(max, NumberStyles.None, CultureInfo.InvariantCulture, out maxBytes) || maxBytes <= 0)
        {
            throw new ArgumentException("Not an upload id this host issued.", nameof(uploadId));
        }

        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("An upload needs the request's tenant.");
        var moduleId = identity.ModuleId ?? throw new InvalidOperationException("Only a module may use uploads.");
        return $"{ObjectKeyPrefixes.ModuleUploads(tenantId, moduleId)}{id}";
    }
}
