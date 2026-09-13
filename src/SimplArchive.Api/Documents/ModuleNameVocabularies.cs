using Microsoft.Extensions.Logging;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Documents;

/// <summary>
/// Which masks name themselves from a module's vocabulary, and where that vocabulary lives (ABI 0.21).
/// </summary>
/// <remarks>
/// <para>
/// A module declares <see cref="ModuleAbi.ModuleMaskSeed.NameVocabularyRel"/> on a mask whose NAME is an
/// identifier rather than a label — a weather folder called <c>LSZH</c>. This resolves that rel to the
/// address the module actually serves it at, so the create dialog can complete as the user types instead of
/// asking them to remember four letters.
/// </para>
/// <para>
/// Resolved PER TENANT and gated on activation, because the module's routes are (ADR 0737): an inactive
/// module answers 404, so advertising its href would be an affordance that fails on click — precisely what
/// ADR 0543 says a missing rel must instead mean. The masks themselves survive deactivation (ADR 0740), so
/// "the mask exists" is not the same question as "the module will answer", and only the second one may
/// decide whether this is emitted.
/// </para>
/// </remarks>
public static class ModuleNameVocabularies
{
    /// <summary>Where a mask's name completes from, and whether the name may hold several such values.</summary>
    /// <param name="Href">The address whose <c>?q=</c> answers the vocabulary.</param>
    /// <param name="IsMultiple">
    /// True when the NAME is a list — a NOTAM briefing's "LSZH LSAS EDGG". The client then completes the
    /// value being typed and leaves the others alone; false means the whole box is one value.
    /// </param>
    public sealed record Vocabulary(string Href, bool IsMultiple);

    /// <summary>
    /// Mask id → where that mask's name completes from, for the ambient tenant. Empty when no module is
    /// loaded, none is active, or none declares one — the ordinary case.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, Vocabulary>> ForTenantAsync(
        IReadOnlyList<ModuleLoader.LoadedModule> modules,
        SimplArchiveDbContext dbContext,
        ILogger logger,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var declaring = modules
            .Where(m => m.Module.Masks.Any(mask => !string.IsNullOrWhiteSpace(mask.NameVocabularyRel)))
            .ToList();
        if (declaring.Count == 0)
        {
            return new Dictionary<Guid, Vocabulary>();
        }

        var active = await ModuleActivationCheck.ActiveIdsAsync(
            dbContext, declaring.Select(m => m.Module.ModuleId).ToList(), now, cancellationToken);

        var map = new Dictionary<Guid, Vocabulary>();
        foreach (var loaded in declaring.Where(m => active.Contains(m.Module.ModuleId)))
        {
            foreach (var mask in loaded.Module.Masks.Where(m => !string.IsNullOrWhiteSpace(m.NameVocabularyRel)))
            {
                var rel = mask.NameVocabularyRel!;
                var link = loaded.Module.RootLinks.FirstOrDefault(l => string.Equals(l.Rel, rel, StringComparison.Ordinal));
                if (link is null)
                {
                    // ADR 0626: we are silently declining to offer something the user cannot see the absence
                    // of — the dialog simply stays a plain box, exactly as it looks for a mask that declared
                    // nothing. Nobody finds out without reading this line, so it is a Warning and it names
                    // both halves of the mismatch.
                    logger.LogWarning(
                        "Module {ModuleId} declares mask {MaskName} ({MaskId}) as naming itself from rel {Rel}, but "
                        + "contributes no root link with that rel — the create dialog will offer NO completion for it. "
                        + "Declared rels: {DeclaredRels}",
                        loaded.Module.ModuleId, mask.Name, mask.MaskId, rel,
                        string.Join(", ", loaded.Module.RootLinks.Select(l => l.Rel)));
                    continue;
                }

                map[mask.MaskId] = new Vocabulary(link.Path, mask.NameVocabularyIsMultiple);
            }
        }

        return map;
    }
}
