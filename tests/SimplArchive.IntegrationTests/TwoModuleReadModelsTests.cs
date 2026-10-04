using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.ModuleAbi;

namespace SimplArchive.IntegrationTests;

// Two modules on one host, through the REAL runtime wiring (ModuleReadModelWiring.AddModuleReadModels).
//
// Every earlier test of module read models loaded ONE module, and with one module the documented non-generic
// constructor `XContext(DbContextOptions options)` works by luck: DI binds it to the non-generic DbContextOptions
// service, and there is only one module to have registered it. The first host to load two — the kiosk on v0.37.0,
// flight school + encryption — handed one module's context the other's options and EF threw ("must be a
// DbContextOptions<EncryptionReadModelContext>"), surfacing as a 500 on an unrelated flight-school transition.
public class TwoModuleReadModelsTests
{
    public sealed class FirstContext(DbContextOptions options) : ModuleDbContext(options);

    public sealed class SecondContext(DbContextOptions options) : ModuleDbContext(options);

    // The generic shape too, beside a non-generic one: a mixed pair must also resolve.
    public sealed class GenericContext(DbContextOptions<GenericContext> options) : ModuleDbContext(options);

    [Theory]
    [InlineData(typeof(FirstContext), typeof(SecondContext))]
    [InlineData(typeof(SecondContext), typeof(FirstContext))] // registration ORDER decided which one broke
    [InlineData(typeof(FirstContext), typeof(GenericContext))]
    public void Every_module_context_resolves_when_two_modules_are_loaded(Type first, Type second)
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddScoped(_ => new SimplArchiveDbContext(
            new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options,
            new CurrentTenantAccessor()));
        services.AddModuleReadModels(
        [
            new ModuleLoader.LoadedModule(new Module("module-one", first), "module-one.dll"),
            new ModuleLoader.LoadedModule(new Module("module-two", second), "module-two.dll"),
        ]);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();

        // Resolving is the whole regression: EF's DbContext constructor refuses options typed for another context.
        var one = (DbContext)scope.ServiceProvider.GetRequiredService(first);
        var two = (DbContext)scope.ServiceProvider.GetRequiredService(second);

        Assert.IsType(first, one);
        Assert.IsType(second, two);

        // And each sits on the CORE context's own connection (ADR 0738) — the same, single connection.
        Assert.Same(connection, one.Database.GetDbConnection());
        Assert.Same(connection, two.Database.GetDbConnection());
    }

    private sealed class Module(string moduleId, Type contextType) : IIndustryModule
    {
        public string ModuleId => moduleId;

        public string DisplayName => moduleId;

        public int AbiMajorVersion => ModuleAbiVersion.Major;

        public string LicenseVerifyKeyPem => string.Empty;

        public IReadOnlyList<ModuleMaskSeed> Masks => [];

        public IReadOnlyList<ModuleReadModelSet> ReadModels => [new ModuleReadModelSet(contextType)];

        public void ConfigureServices(IServiceCollection services)
        {
        }
    }
}
