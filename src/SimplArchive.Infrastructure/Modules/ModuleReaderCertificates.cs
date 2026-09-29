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
    /// <summary>The reader's certificates, or null when no active module answers this question.</summary>
    public async Task<IReadOnlyList<ReaderCertificate>?> ForAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var modules = (services.GetService(typeof(IReadOnlyList<ModuleLoader.LoadedModule>))
            as IReadOnlyList<ModuleLoader.LoadedModule> ?? [])
            .Where(m => m.Module.ReaderCertificates is not null)
            .ToList();

        if (modules.Count == 0)
        {
            return null;   // the ordinary case: nothing enrols certificates, so nothing is asked
        }

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
                return await loaded.Module.ReaderCertificates!(
                    new ReaderCertificateContext(userId, archive, services));
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
                return [];
            }
            finally
            {
                if (identity is not null)
                {
                    identity.ModuleId = restore;
                }
            }
        }

        // Installed but not active for this tenant — the licence has lapsed or was never filed. That is NOT
        // "no module": the tenant is in an enveloping mode and nothing can envelope, so it must refuse.
        return [];
    }
}
