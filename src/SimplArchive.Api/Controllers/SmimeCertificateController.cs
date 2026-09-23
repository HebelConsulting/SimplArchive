using System.Security.Cryptography.X509Certificates;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Encryption;
using SimplArchive.Api.Errors.Exceptions.Encryption;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// The caller's self-service S/MIME certificate (#1332, ADR 0816): one certificate, set by uploading the
/// public half or by generating a complete self-signed identity, deletable, and consumed by the IMAP
/// funnel in-process — no encryption sidecar involved. One rel, the method says which action (ADR 0719):
/// GET reads the state, PUT uploads, POST generates, DELETE clears. On installations where the encryption
/// service provisions certificates (ADR 0813's gate), self-service is reported unavailable and every
/// mutation refuses — those identities come from an outside source (the sidecar's ADR 0009).
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/me/smime-certificate")]
[Authorize]
public class SmimeCertificateController : ControllerBase
{
    /// <summary>A year, the identity tool's own default: long enough that re-enrollment is rare, short
    /// enough that a forgotten identity expires rather than lingering.</summary>
    private static readonly TimeSpan GeneratedLifetime = TimeSpan.FromDays(365);

    private readonly SimplArchiveDbContext _dbContext;
    private readonly Concurrency.UserVerbs _users;
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly ICurrentTenantAccessor _currentTenantAccessor;
    private readonly MessageEnvelopeClient _envelopeClient;
    private readonly Encryption.TenantIngestKeyService _ingestKeys;
    private readonly IAuditRecorder _audit;

    public SmimeCertificateController(
        SimplArchiveDbContext dbContext,
        Concurrency.UserVerbs users,
        ICurrentUserAccessor currentUserAccessor,
        ICurrentTenantAccessor currentTenantAccessor,
        MessageEnvelopeClient envelopeClient,
        Encryption.TenantIngestKeyService ingestKeys,
        IAuditRecorder audit)
    {
        _dbContext = dbContext;
        _users = users;
        _currentUserAccessor = currentUserAccessor;
        _currentTenantAccessor = currentTenantAccessor;
        _envelopeClient = envelopeClient;
        _ingestKeys = ingestKeys;
        _audit = audit;
    }

    public class SmimeStatusResource : HypermediaResource
    {
        /// <summary>Whether the self-service controls apply here at all — false when the encryption
        /// service provisions this tenant's certificates (ADR 0813), and the dialog says so instead of
        /// offering dead buttons.</summary>
        public bool SelfService { get; set; }

        /// <summary>A certificate is set.</summary>
        public bool Enabled { get; set; }

        public string? Subject { get; set; }

        public DateTimeOffset? NotAfter { get; set; }
    }

    public class SmimeGeneratedResource : SmimeStatusResource
    {
        // The identity artifacts — returned ONCE at generation; the private key is never stored.
        public string Pkcs12 { get; set; } = string.Empty;

        public string MobileConfig { get; set; } = string.Empty;

        /// <summary>What the client names the downloaded files (the email with filesystem-hostile
        /// characters replaced), so both clients agree without re-deriving it.</summary>
        public string FileNameStem { get; set; } = string.Empty;
    }

    public class SmimeGenerateRequest
    {
        /// <summary>The PKCS#12 password the user typed — re-typed on the device at import, never stored.</summary>
        public string P12Password { get; set; } = string.Empty;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken) =>
        await LoadUserAsync(cancellationToken) is not { } user
            ? Forbid()
            : Ok(await StatusAsync<SmimeStatusResource>(user, cancellationToken));

    [HttpHead]
    public async Task<IActionResult> Head(CancellationToken cancellationToken) =>
        await LoadUserAsync(cancellationToken) is null ? Forbid() : NoContent();

    // Upload the certificate — the PUBLIC half only, PEM or DER.
    [HttpPut]
    public async Task<IActionResult> Upload(CancellationToken cancellationToken)
    {
        if (await LoadUserAsync(cancellationToken) is not { } user)
        {
            return Forbid();
        }

        await RequireSelfServiceAsync(cancellationToken);
        RequireUnset(user);

        using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer, cancellationToken);
        user.SmimeCertificatePem = ParsePublicCertificate(buffer.ToArray());
        await _users.MutateAsync(Request, user, apply: () => Task.CompletedTask, cancellationToken: cancellationToken);

        await _audit.RecordAsync(AuditActions.SmimeCertificateChanged, "User", user.Id, user.Email,
            "Uploaded", cancellationToken: cancellationToken);
        return Ok(await StatusAsync<SmimeStatusResource>(user, cancellationToken));
    }

    // Generate a complete self-signed identity — sets the certificate and returns the artifacts once.
    [HttpPost]
    public async Task<IActionResult> Generate([FromBody] SmimeGenerateRequest request, CancellationToken cancellationToken)
    {
        if (await LoadUserAsync(cancellationToken) is not { } user)
        {
            return Forbid();
        }

        await RequireSelfServiceAsync(cancellationToken);
        RequireUnset(user);
        if (string.IsNullOrWhiteSpace(request.P12Password))
        {
            throw new SmimeCertificateInvalidException("the PKCS#12 password must not be empty.");
        }

        // The tenant's mail-ingest certificate rides the profile as a second payload (#1335) — minted
        // lazily here if this is the first need; null (no verified mail domain) simply omits the payload.
        var ingest = await _ingestKeys.EnsureAsync(user.TenantId, cancellationToken);
        var identity = SmimeIdentity.Generate(user.Email, request.P12Password, GeneratedLifetime, ingest?.CertificatePem);
        user.SmimeCertificatePem = identity.CertificatePem;
        await _users.MutateAsync(Request, user, apply: () => Task.CompletedTask, cancellationToken: cancellationToken);

        await _audit.RecordAsync(AuditActions.SmimeCertificateChanged, "User", user.Id, user.Email,
            "Generated", cancellationToken: cancellationToken);

        var resource = await StatusAsync<SmimeGeneratedResource>(user, cancellationToken);
        resource.Pkcs12 = Convert.ToBase64String(identity.Pkcs12);
        resource.MobileConfig = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(identity.MobileConfig));
        resource.FileNameStem = user.Email.Replace('@', '_');
        return Ok(resource);
    }

    // Delete the certificate — IMAP returns to plaintext, and the dialog's options re-enable.
    [HttpDelete]
    public async Task<IActionResult> Delete(CancellationToken cancellationToken)
    {
        if (await LoadUserAsync(cancellationToken) is not { } user)
        {
            return Forbid();
        }

        await RequireSelfServiceAsync(cancellationToken);

        user.SmimeCertificatePem = null;
        await _users.MutateAsync(Request, user, apply: () => Task.CompletedTask, cancellationToken: cancellationToken);

        await _audit.RecordAsync(AuditActions.SmimeCertificateChanged, "User", user.Id, user.Email,
            "Deleted", cancellationToken: cancellationToken);
        return NoContent();
    }

    /// <summary>Parses PEM or DER, refuses key material and expired certificates, returns canonical PEM.</summary>
    private static string ParsePublicCertificate(byte[] bytes)
    {
        var text = System.Text.Encoding.ASCII.GetString(bytes);
        if (text.Contains("PRIVATE KEY", StringComparison.Ordinal))
        {
            // The whole design point is that private keys stay on the user's devices — refuse loudly
            // rather than quietly storing more than the feature needs.
            throw new SmimeCertificateInvalidException("it contains private-key material; upload only the certificate.");
        }

        X509Certificate2 parsed;
        try
        {
            parsed = text.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal)
                ? X509Certificate2.CreateFromPem(text)
                : X509CertificateLoader.LoadCertificate(bytes);
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            throw new SmimeCertificateInvalidException("it does not parse as a PEM or DER certificate.");
        }

        using (parsed)
        {
            if (DateTimeOffset.UtcNow > parsed.NotAfter)
            {
                throw new SmimeCertificateInvalidException($"it expired {parsed.NotAfter:yyyy-MM-dd}.");
            }

            return parsed.ExportCertificatePem();
        }
    }

    private async Task RequireSelfServiceAsync(CancellationToken cancellationToken)
    {
        if (!await SelfServiceAsync(cancellationToken))
        {
            throw new SmimeSelfServiceUnavailableException();
        }
    }

    private static void RequireUnset(User user)
    {
        if (user.SmimeCertificatePem is not null)
        {
            throw new SmimeCertificateAlreadySetException();
        }
    }

    private async Task<bool> SelfServiceAsync(CancellationToken cancellationToken)
    {
        // The gate matches tenant NAMES (ADR 0813), so the tenant row answers it. Service-provisioned
        // tenants get their identities from outside; everywhere else self-service is on.
        var tenantName = await _dbContext.Tenants
            .Where(t => t.Id == _currentTenantAccessor.TenantId)
            .Select(t => t.Name)
            .SingleOrDefaultAsync(cancellationToken);
        return tenantName is null || !_envelopeClient.EnabledFor(tenantName);
    }

    private async Task<T> StatusAsync<T>(User user, CancellationToken cancellationToken) where T : SmimeStatusResource, new()
    {
        string? subject = null;
        DateTimeOffset? notAfter = null;
        if (user.SmimeCertificatePem is { } pem)
        {
            try
            {
                using var certificate = X509Certificate2.CreateFromPem(pem);
                subject = certificate.Subject;
                notAfter = certificate.NotAfter;
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                subject = "(stored certificate is unreadable — delete it)";
            }
        }

        return new T
        {
            SelfService = await SelfServiceAsync(cancellationToken),
            Enabled = user.SmimeCertificatePem is not null,
            Subject = subject,
            NotAfter = notAfter,
            Links =
            [
                // ONE rel for this address (ADR 0719): GET reads, PUT uploads, POST generates, DELETE
                // clears. The dialog gates its buttons on selfService + enabled above, not on rels.
                new Link("self", "/api/me/smime-certificate", "GET"),
            ],
        };
    }

    private async Task<User?> LoadUserAsync(CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId)
        {
            return null; // a ServiceAccount / platform admin has no mailbox and no certificate
        }

        return await _dbContext.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
    }
}
