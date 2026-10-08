using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Filters;
using SimplArchive.Api.Errors.Exceptions.Modules;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Modules;

/// <summary>
/// Applies the per-tenant activation gate to every controller a MODULE assembly contributes (ADR 0737):
/// the convention keys on the controller's assembly, so a module author cannot forget the gate — it is not
/// theirs to write. An inactive tenant's request answers 404 <c>MODULE_NOT_ACTIVE</c> (ADR 0543's absence
/// semantics with the reason named for an administrator reading the wire).
/// </summary>
public sealed class ModuleControllerGateConvention : IControllerModelConvention
{
    private readonly IReadOnlyDictionary<System.Reflection.Assembly, string> _moduleByAssembly;

    public ModuleControllerGateConvention(IReadOnlyList<ModuleLoader.LoadedModule> modules) =>
        _moduleByAssembly = modules.ToDictionary(m => m.Module.GetType().Assembly, m => m.Module.ModuleId);

    public void Apply(ControllerModel controller)
    {
        if (_moduleByAssembly.TryGetValue(controller.ControllerType.Assembly, out var moduleId))
        {
            controller.Filters.Add(new ModuleActivationGateFilter(moduleId));
            controller.Filters.Add(new RevealOnceNoStoreFilter());
        }
    }
}

/// <summary>The gate itself: resolves the ambient tenant's activation and refuses an inactive one.</summary>
/// <remarks>
/// A resource filter — after authorization (so the tenant accessors are populated), before model binding
/// (so an inactive tenant's payload is never even parsed). Services come from the request scope because
/// the filter instance is created per controller at startup, long before any container exists.
/// </remarks>
internal sealed class ModuleActivationGateFilter : IAsyncResourceFilter
{
    private readonly string _moduleId;

    public ModuleActivationGateFilter(string moduleId) => _moduleId = moduleId;

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var services = context.HttpContext.RequestServices;
        if (services.GetRequiredService<ICurrentTenantAccessor>().TenantId is null)
        {
            // No tenant, no activation to consult — a platform administrator or an unauthenticated probe.
            // The module does not exist here either way.
            throw new ModuleNotActiveException(_moduleId);
        }

        // The tenant query filter scopes the row; the DERIVED active answer (ADR 0740) is the gate —
        // a lapsed license past grace reads exactly like no license at all.
        var dbContext = services.GetRequiredService<SimplArchiveDbContext>();
        if (!await ModuleActivationCheck.IsActiveAsync(dbContext, _moduleId, DateTimeOffset.UtcNow, context.HttpContext.RequestAborted))
        {
            throw new ModuleNotActiveException(_moduleId);
        }

        // From here the scope runs AS the module (ADR 0736): its facade reads are gated by its own
        // principal's consented grants, not by any implicit whole-tenant view.
        services.GetRequiredService<ModuleIdentityAccessor>().ModuleId = _moduleId;

        await next();
    }
}

/// <summary>
/// A module action's value revealed once (ABI 1.8, ADR 0899) is a secret on the wire: its response is marked
/// <c>Cache-Control: no-store</c> so no cache along the way, and no browser history, keeps a copy. Applied by the
/// host, because a module author forgetting it fails silently. The outcome is answered in the core's
/// <see cref="ModuleActionResponse"/>, which draws a revealed scan address as a QR code (ABI 1.13, ADR 0913).
/// </summary>
internal sealed class RevealOnceNoStoreFilter : IAsyncResultFilter
{
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.Result is ObjectResult { Value: SimplArchive.ModuleAbi.ModuleActionResult { RevealOnce: not null } result } answer)
        {
            answer.Value = ModuleActionResponse.From(result);
            answer.DeclaredType = typeof(ModuleActionResponse);
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            context.HttpContext.Response.Headers.Pragma = "no-cache";
        }

        await next();
    }
}
