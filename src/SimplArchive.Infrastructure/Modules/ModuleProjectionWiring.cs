using Microsoft.Extensions.DependencyInjection;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Modules;

/// <summary>
/// Registers the projection dispatch a module's read model depends on (ABI 1.1, ADR 0848).
/// </summary>
/// <remarks>
/// One line at the composition root, beside <see cref="ModuleReadModelWiring"/> and for the same reason: the
/// host's <c>Program.cs</c> was at 999 lines, one line from the 1000-line rule, so anything registered inline
/// there had to be registered somewhere else instead. It also keeps the two halves of ADR 0738's arrangement
/// together — the contexts and the thing that keeps them current.
/// </remarks>
public static class ModuleProjectionWiring
{
    public static void AddModuleProjections(
        IServiceCollection services, IReadOnlyList<ModuleLoader.LoadedModule> modules) =>
        services.AddSingleton<IModuleProjectionDispatcher>(sp =>
            new ModuleProjectionDispatcher(
                modules, sp.GetRequiredService<IServiceScopeFactory>(), sp.GetService<TimeProvider>()));
}
