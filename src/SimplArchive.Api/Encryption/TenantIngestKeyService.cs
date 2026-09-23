using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Encryption;

/// <summary>
/// The tenant's mail-ingest keypair (#1335, ADR 0820): minted lazily against the tenant's first VERIFIED
/// mail domain (`archive@{domain}` — the designated ingest address a sender's client finds the certificate
/// by), private half transit-protected at rest, decrypt served to the LMTP boundary. A tenant with no
/// verified domain has no ingest identity — the feature is dormant there, not broken.
/// </summary>
public sealed class TenantIngestKeyService(
    SimplArchiveDbContext dbContext,
    ITransitEncryptor transit,
    ILogger<TenantIngestKeyService> logger)
{
    /// <summary>A year, like the self-service identity — rotation is a later admin surface.</summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(365);

    /// <summary>The tenant's ingest key, minting it on first need. Null without a verified mail domain.</summary>
    public async Task<TenantIngestKey?> EnsureAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (await dbContext.TenantIngestKeys.IgnoreQueryFilters(["TenantFilter"])
                .SingleOrDefaultAsync(k => k.TenantId == tenantId, cancellationToken) is { } existing)
        {
            return existing;
        }

        var domain = await dbContext.TenantMailDomains.IgnoreQueryFilters(["TenantFilter"])
            .Where(d => d.TenantId == tenantId && d.VerifiedAt != null)
            .OrderBy(d => d.CreatedAt)
            .Select(d => d.Domain)
            .FirstOrDefaultAsync(cancellationToken);
        if (domain is null)
        {
            return null;
        }

        var address = $"archive@{domain}";
        using var key = RSA.Create(3072);
        using var certificate = SmimeIdentity.SelfSign(address, key, Lifetime);
        var ingestKey = new TenantIngestKey
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CertificatePem = certificate.ExportCertificatePem(),
            PrivateKeyProtected = await transit.EncryptAsync(key.ExportPkcs8PrivateKeyPem(), cancellationToken),
            IngestAddress = address,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        dbContext.TenantIngestKeys.Add(ingestKey);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Two firsts racing (a delivery and a profile generation): the unique TenantId index refuses
            // the second mint — take the winner's row instead.
            dbContext.Entry(ingestKey).State = EntityState.Detached;
            return await dbContext.TenantIngestKeys.IgnoreQueryFilters(["TenantFilter"])
                .SingleAsync(k => k.TenantId == tenantId, cancellationToken);
        }

        logger.LogInformation("Minted the mail-ingest keypair for tenant {TenantId} as {Address}.", tenantId, address);
        return ingestKey;
    }

    /// <summary>The certificate WITH its private key, for the LMTP boundary's decrypt. Null when the
    /// tenant has no ingest key yet (nothing was ever enveloped to it, then).</summary>
    public async Task<X509Certificate2?> ForDecryptAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var ingestKey = await dbContext.TenantIngestKeys.IgnoreQueryFilters(["TenantFilter"])
            .SingleOrDefaultAsync(k => k.TenantId == tenantId, cancellationToken);
        if (ingestKey is null)
        {
            return null;
        }

        using var key = RSA.Create();
        key.ImportFromPem(await transit.DecryptAsync(ingestKey.PrivateKeyProtected, cancellationToken));
        using var certificate = X509Certificate2.CreateFromPem(ingestKey.CertificatePem);
        return certificate.CopyWithPrivateKey(key);
    }
}
