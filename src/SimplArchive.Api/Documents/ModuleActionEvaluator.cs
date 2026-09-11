using SimplArchive.Infrastructure.Modules;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.ModuleAbi;

namespace SimplArchive.Api.Documents;

/// <summary>
/// Collects the actions active modules offer on a document (ABI 0.20, ADR 0786), for the document resource
/// to carry — the same shape and the same gating as <see cref="MachineStatusEvaluator"/> beside it.
/// </summary>
/// <remarks>
/// <para>
/// Asked only of modules that declared this document's mask in <c>ActionSubjectMasks</c>, so a module is not
/// consulted on every document read in the tenant. Within that, the module decides per document — which is
/// what lets a vertical offer an action on a CORE mask (a booking) without speaking for every document
/// wearing it.
/// </para>
/// <para>
/// Runs as the MODULE, like every other module read (ADR 0736): the decision is the module's judgement about
/// its own documents, and as the reading caller it would answer differently per caller — the bug ADR 0781
/// records for the booking review, which is the same shape.
/// </para>
/// </remarks>
public sealed class ModuleActionEvaluator(
    SimplArchiveDbContext dbContext, IServiceProvider services, ModuleIdentityAccessor? identity = null)
{
    public async Task<IReadOnlyList<ModuleActionResource>> EvaluateAsync(
        Guid documentId, Guid? maskId, DateTimeOffset asOf, CancellationToken cancellationToken)
    {
        if (maskId is not { } subjectMaskId)
        {
            return [];
        }

        var modules = (services.GetService(typeof(IReadOnlyList<ModuleLoader.LoadedModule>))
            as IReadOnlyList<ModuleLoader.LoadedModule> ?? [])
            .Where(m => m.Module.DocumentActions is not null && m.Module.ActionSubjectMasks.Contains(subjectMaskId))
            .ToList();
        if (modules.Count == 0)
        {
            return [];
        }

        var result = new List<ModuleActionResource>();
        foreach (var loaded in modules)
        {
            if (!await ModuleActivationCheck.IsActiveAsync(dbContext, loaded.Module.ModuleId, asOf, cancellationToken))
            {
                continue; // an inactive module's actions do not exist (ADR 0543), like its transitions
            }

            var archive = (IModuleArchiveFacade)services.GetService(typeof(IModuleArchiveFacade))!;
            var restore = identity?.ModuleId;
            if (identity is not null)
            {
                identity.ModuleId = loaded.Module.ModuleId;
            }

            try
            {
                var offered = await loaded.Module.DocumentActions!(
                    new ModuleDocumentActionContext(documentId, maskId, archive, services));
                result.AddRange(offered.Select(a => new ModuleActionResource
                {
                    Rel = a.Rel,
                    Label = a.Label,
                    OptionsHref = a.OptionsPath,
                    CommitHref = a.CommitPath,
                    ValueField = a.ValueField,
                    Prompt = a.Prompt,
                }));
            }
            finally
            {
                if (identity is not null)
                {
                    identity.ModuleId = restore;
                }
            }
        }

        return result;
    }
}

/// <summary>
/// One module action as the document resource carries it (ADR 0786): what to call it, where its choices come
/// from, and where the chosen one goes.
/// </summary>
/// <remarks>
/// Deliberately NOT a <c>Link</c>. A link is one address reached by one method (ADR 0719); this is a pair —
/// a GET for the options and a POST for the commit — and splitting it into two links would leave the client
/// pairing them by naming convention, which is exactly the implicit coupling rel names exist to avoid.
/// </remarks>
public sealed class ModuleActionResource
{
    public string Rel { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public string OptionsHref { get; set; } = string.Empty;

    public string CommitHref { get; set; } = string.Empty;

    public string ValueField { get; set; } = string.Empty;

    public string Prompt { get; set; } = string.Empty;
}
