using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SimplArchive.Api.Errors.Exceptions.Encryption;
using SimplArchive.Api.Hypermedia;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.Infrastructure.Storage;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// KEK rotation for the installation's at-rest encryption (ADR 0014): rotate, watch the re-wrap sweep,
/// retire a spent generation. Platform-administrator only — the KEK is an INSTALLATION property (the
/// encryption service is per installation, its ADR 0001), so no tenant admin may rotate keys that every
/// other tenant's objects are wrapped by.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/encryption/keys")]
[Authorize]
public class EncryptionKeysController : ControllerBase
{
    private readonly AtRestKeyService _keys;
    private readonly KekRotationSweep _sweep;
    private readonly ICurrentPlatformAdministratorAccessor _platformAdministrator;
    private readonly SimplArchiveDbContext _dbContext;
    private readonly IAuditRecorder _audit;
    private readonly ILogger<EncryptionKeysController> _logger;

    public EncryptionKeysController(
        AtRestKeyService keys,
        KekRotationSweep sweep,
        ICurrentPlatformAdministratorAccessor platformAdministrator,
        SimplArchiveDbContext dbContext,
        IAuditRecorder audit,
        ILogger<EncryptionKeysController> logger)
    {
        _keys = keys;
        _sweep = sweep;
        _platformAdministrator = platformAdministrator;
        _dbContext = dbContext;
        _audit = audit;
        _logger = logger;
    }

    public class KekStatusResource : HypermediaResource
    {
        /// <summary>At-rest encryption is configured at all — false means every field below is empty and
        /// the client says so rather than offering a rotation that cannot happen.</summary>
        public bool Available { get; set; }

        public string? Current { get; set; }

        public List<string> Generations { get; set; } = [];

        public bool SweepRunning { get; set; }

        public int Rewrapped { get; set; }

        /// <summary>Objects still wrapped by an older generation — the honest answer to "may I retire
        /// yet?", recomputed from the objects rather than remembered.</summary>
        public int Remaining { get; set; }

        public int Failed { get; set; }
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        if (!IsPlatformAdministrator)
        {
            return Forbid();
        }

        if (!_keys.Enabled)
        {
            return Ok(new KekStatusResource { Available = false, Links = [Self()] });
        }

        var (current, generations) = await _keys.GenerationsAsync(cancellationToken);
        var status = _sweep.Status();
        return Ok(new KekStatusResource
        {
            Available = true,
            Current = current,
            Generations = [.. generations],
            SweepRunning = status.Running,
            Rewrapped = status.Rewrapped,
            // Only counted on demand when no sweep is running: the count walks every object's metadata,
            // which is cheap per object and not free in aggregate.
            Remaining = status.Running ? status.Remaining : await _sweep.CountRemainingAsync(cancellationToken),
            Failed = status.Failed,
        });
    }

    [HttpHead]
    public IActionResult Head() => IsPlatformAdministrator ? NoContent() : Forbid();

    /// <summary>
    /// Mints the next generation and starts the re-wrap sweep. Safe at any moment: old generations stay
    /// on the token, so nothing becomes unreadable — only the population of old-generation objects stops
    /// growing (ADR 0014).
    /// </summary>
    [HttpPost("rotate")]
    public async Task<IActionResult> Rotate(CancellationToken cancellationToken)
    {
        if (!IsPlatformAdministrator)
        {
            return Forbid();
        }

        if (!_keys.Enabled)
        {
            return NotFound();
        }

        var generation = await _keys.RotateAsync(cancellationToken);
        await RecordForEveryGatedTenantAsync(AuditActions.EncryptionKeyRotated, generation, cancellationToken);

        // Fire-and-forget by design: the sweep walks every tenant's bucket and must not hold the request
        // open. Its progress is observable through GET, which is why it needs no completion callback.
        _ = Task.Run(async () =>
        {
            try
            {
                await _sweep.RunAsync(CancellationToken.None);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "The KEK re-wrap sweep failed; the old generation keeps its "
                    + "references and cannot be retired until a later sweep succeeds.");
            }
        }, CancellationToken.None);

        return Accepted(new { generation });
    }

    /// <summary>Re-runs the sweep without rotating — how an admin finishes a sweep that was interrupted.</summary>
    [HttpPost("sweep")]
    public IActionResult Sweep()
    {
        if (!IsPlatformAdministrator)
        {
            return Forbid();
        }

        if (!_keys.Enabled)
        {
            return NotFound();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _sweep.RunAsync(CancellationToken.None);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "The KEK re-wrap sweep failed.");
            }
        }, CancellationToken.None);

        return Accepted();
    }

    /// <summary>
    /// Retires a spent generation — IRREVERSIBLE. Refused while any object still references it, because
    /// that is the one check the service cannot make for itself (ADR 0014): only the core owns the objects.
    /// </summary>
    [HttpDelete("{generation}")]
    public async Task<IActionResult> Retire(string generation, CancellationToken cancellationToken)
    {
        if (!IsPlatformAdministrator)
        {
            return Forbid();
        }

        if (!_keys.Enabled)
        {
            return NotFound();
        }

        var remaining = await _sweep.CountRemainingAsync(cancellationToken);
        if (remaining > 0)
        {
            throw new KekRetirementRefusedException(generation, remaining);
        }

        await _keys.RetireAsync(generation, cancellationToken);
        await RecordForEveryGatedTenantAsync(AuditActions.EncryptionKeyRetired, generation, cancellationToken);

        _logger.LogWarning("KEK generation {Generation} was retired by a platform administrator.", generation);
        return NoContent();
    }

    /// <summary>
    /// A KEK act is INSTALLATION-level, but <c>AuditEvent</c> is tenant-scoped — so it is recorded once
    /// per ENCRYPTION-GATED tenant, which is precisely the set whose documents that key protects. The
    /// alternative (a single event on some arbitrary tenant, or none) would leave an auditor asking "why
    /// did our documents stop opening?" with nothing in their own log to find, which is the one question
    /// a retirement has to be able to answer.
    /// </summary>
    private async Task RecordForEveryGatedTenantAsync(string action, string generation, CancellationToken cancellationToken)
    {
        var administratorId = _platformAdministrator.PlatformAdministratorId!.Value;
        foreach (var tenantId in await _dbContext.Tenants.IgnoreQueryFilters(["TenantFilter"])
                     .Select(t => t.Id).ToListAsync(cancellationToken))
        {
            if (!await _keys.GatedAsync(ObjectKeyPrefixes.Tenant(tenantId) + "probe", cancellationToken))
            {
                continue;
            }

            await _audit.RecordForActorAsync(
                SimplArchive.Domain.Audit.AuditActorType.PlatformAdministrator, administratorId,
                "Platform administrator", tenantId, action,
                targetType: "EncryptionKey", targetId: null, targetName: generation,
                cancellationToken: cancellationToken);
        }
    }

    private bool IsPlatformAdministrator => _platformAdministrator.PlatformAdministratorId is not null;

    private static Link Self() => new("self", "/api/encryption/keys", "GET");
}
