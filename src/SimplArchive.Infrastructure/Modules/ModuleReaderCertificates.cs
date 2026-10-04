using Microsoft.Extensions.Logging;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.ModuleAbi;

namespace SimplArchive.Infrastructure.Modules;

/// <summary>
/// Asks the active module which certificates a reader is addressed by (ADR 0842, #1424).
/// </summary>
/// <remarks>
/// <para>
/// <b>Null and empty are different answers, and the distinction is the whole point.</b> Null means no active
/// module declares the capability, so the core resolves certificates as it did before. Empty means a module
/// answered and this reader has <i>no usable certificate</i> — the read is refused rather than served
/// plaintext (ADR 0825). Collapsing the two would make an unlicensed module look exactly like a reader who
/// never enrolled, which is one of the four refusals ADR 0842 requires to stay distinguishable.
/// </para>
/// <para>
/// <b>Activation is the licence.</b> The hook is only asked while the module is active for this tenant, so a
/// lapsed licence stops the answer and the tier refuses — which is how the delivery tiers are gated at all
/// (ADR 0834: the licence sets the ceiling; the mode chooses at or below it).
/// </para>
/// <para>
/// The call runs <b>as the module</b>, like fact providers and booking review (ADR 0736): certificates are the
/// module's own documents, isolated from most callers, so running as the asking reader would answer
/// differently depending on who is reading — and a certificate set that depends on the reader is not a set.
/// </para>
/// </remarks>
public sealed class ModuleReaderCertificates(
    SimplArchiveDbContext dbContext,
    IServiceProvider services,
    ILogger<ModuleReaderCertificates> logger)
{
    /// <summary>
    /// The reader's certificates and WHY the answer ended as it did (ADR 0859).
    /// </summary>
    /// <remarks>
    /// Returns an outcome rather than null-or-empty since #1411. The old shape collapsed four causes with
    /// four different fixes into one empty list — no certificate, all filtered out, licence lapsed, and the
    /// module throwing — against ADR 0842's requirement that they stay distinguishable. The two fail-open
    /// callers may still ignore the outcome; the strict content read must not.
    /// </remarks>
    public async Task<ReaderCertificateAnswer> ForAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var modules = (services.GetService(typeof(IReadOnlyList<ModuleLoader.LoadedModule>))
            as IReadOnlyList<ModuleLoader.LoadedModule> ?? [])
            .Where(m => m.Module.ReaderCertificates is not null)
            .ToList();

        if (modules.Count == 0)
        {
            // the ordinary case: nothing enrols certificates, so nothing is asked
            return ReaderCertificateAnswer.NoModule;
        }

        logger.LogDebug(
            "{Count} loaded module(s) can answer which certificates a reader is addressed by: {Modules}.",
            modules.Count, string.Join(", ", modules.Select(m => m.Module.ModuleId)));

        var now = DateTimeOffset.UtcNow;
        var identity = services.GetService(typeof(ModuleIdentityAccessor)) as ModuleIdentityAccessor;

        foreach (var loaded in modules)
        {
            if (!await ModuleActivationCheck.IsActiveAsync(dbContext, loaded.Module.ModuleId, now, cancellationToken))
            {
                continue;
            }

            // Resolved per call rather than injected, for the reason the booking reviewer gives: this sits on
            // a path the facade can re-enter, and a constructor edge would be a startup cycle instead.
            var archive = (IModuleArchiveFacade)services.GetService(typeof(IModuleArchiveFacade))!;
            var restore = identity?.ModuleId;
            if (identity is not null)
            {
                identity.ModuleId = loaded.Module.ModuleId;
            }

            try
            {
                var answer = await loaded.Module.ReaderCertificates!(
                    new ReaderCertificateContext(userId, archive, services));

                // WHICH MODULE ANSWERED, AND WITH HOW MANY. A module that answers is the ONLY source
                // (ADR 0842), so this one number decides whether a strict tenant can serve the reader
                // anything at all — and an empty answer is indistinguishable, from outside, from a module
                // that was never asked (#1498).
                logger.LogDebug(
                    "Module {ModuleId} answered with {Count} certificate(s) for user {UserId}.",
                    loaded.Module.ModuleId, answer.Count, userId);

                return ReaderCertificateAnswer.Answered(answer);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // REFUSE, never fall through to the core's own sources. A module that throws has not said
                // "this reader has none"; it has said nothing, and treating silence as an answer is how a
                // tenant whose module is broken would quietly start being served by a weaker path.
                logger.LogError(exception,
                    "Module {ModuleId} failed to answer which certificates user {UserId} is addressed by, "
                    + "so the read is refused. Turn Trace on for the exchange.",
                    loaded.Module.ModuleId, userId);

                // NOT "none". The reader is told something different for this than for an empty answer,
                // because the fix is an administrator's rather than theirs (ADR 0859).
                return ReaderCertificateAnswer.AskFailed;
            }
            finally
            {
                if (identity is not null)
                {
                    identity.ModuleId = restore;
                }
            }
        }

        // Installed but not active for this tenant. What that MEANS depends on the tenant's mode (ADR 0890,
        // narrowing ADR 0859): where the tenant delivers envelopes, nothing can envelope, so the read refuses —
        // LicenceLapsed, its own outcome among ADR 0842's four refusals, and it closes the column too. Where the
        // tenant does NOT envelope, the module simply has nothing to say about it: answering LicenceLapsed there
        // shut off the reader's own self-service certificate on every tenant of an installation that licensed the
        // module for only one — the kiosk's public demo would have lost self-service S/MIME the moment the
        // Encryption Module was loaded for its Crypto tenant.
        if (await TenantDeliversEnvelopesAsync(cancellationToken) is false)
        {
            logger.LogDebug(
                "A module that answers reader certificates is loaded but not active for this tenant, whose mode "
                + "does not deliver envelopes; the core's own sources answer for user {UserId}.", userId);
            return ReaderCertificateAnswer.NoModule;
        }

        // Warning, not Debug: this tenant's mode delivers envelopes and no module is active for it, so every
        // enveloped read refuses until an administrator files a licence. Nothing else in the system says so.
        logger.LogWarning(
            "This tenant's encryption mode delivers envelopes, but no module that answers reader certificates is "
            + "ACTIVE for it, so an enveloped read for user {UserId} must refuse. Installed and able to answer: "
            + "{Modules}. File or renew the licence.",
            userId, string.Join(", ", modules.Select(m => m.Module.ModuleId)));

        return ReaderCertificateAnswer.LicenceLapsed;
    }

    /// <summary>
    /// Whether the ambient tenant's mode delivers envelopes — null when it cannot be told (no tenant in scope, or no
    /// mode map registered), which the caller treats as the old, SAFE answer: refuse.
    /// </summary>
    private async Task<bool?> TenantDeliversEnvelopesAsync(CancellationToken cancellationToken)
    {
        if (services.GetService(typeof(Application.Abstractions.ICurrentTenantAccessor))
                is not Application.Abstractions.ICurrentTenantAccessor { TenantId: { } tenantId }
            || services.GetService(typeof(Encryption.EncryptionModes)) is not Encryption.EncryptionModes modes)
        {
            return null;
        }

        var name = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleOrDefaultAsync(
            dbContext.Tenants.Where(t => t.Id == tenantId).Select(t => t.Name), cancellationToken);

        return name is null ? null : modes.DeliversEnvelopes(name);
    }
}
