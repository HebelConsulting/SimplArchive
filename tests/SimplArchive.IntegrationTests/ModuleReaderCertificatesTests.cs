using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.ModuleAbi;

namespace SimplArchive.IntegrationTests;

// Which certificates a reader is addressed by, asked of the Module (ADR 0842, #1424).
//
// THE SEMANTIC UNDER TEST IS null-versus-empty, and it is the whole reason this class exists rather than a
// method returning a list. Null means no active module declares the capability, so the core resolves
// certificates the way it always did. Empty means a module ANSWERED and this reader has none — and the read is
// then refused rather than served plaintext (ADR 0825).
//
// Collapsing the two would make an unlicensed module indistinguishable from a reader who never enrolled, which
// is one of the four refusals ADR 0842 requires to stay apart — and the one whose fix is "file the licence".
public class ModuleReaderCertificatesTests
{
    /// <summary>A module that DECLARES the capability — so being skipped can only be the activation gate.</summary>
    private sealed class EnrollingModule : IIndustryModule
    {
        public Func<ReaderCertificateContext, Task<IReadOnlyList<ReaderCertificate>>>? ReaderCertificates =>
            _ => Task.FromResult<IReadOnlyList<ReaderCertificate>>(
                [new ReaderCertificate("-----BEGIN CERTIFICATE-----", "card", DateTimeOffset.MaxValue)]);

        public string ModuleId => "enc";
        public string DisplayName => "Encryption";
        public int AbiMajorVersion => ModuleAbiVersion.Major;
        public int AbiMinorVersion => ModuleAbiVersion.Minor;
        public string LicenseVerifyKeyPem => string.Empty;
        public IReadOnlyList<ModuleMaskSeed> Masks => [];
        public void ConfigureServices(Microsoft.Extensions.DependencyInjection.IServiceCollection services) { }
    }

    private static SimplArchiveDbContext Context(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options,
            new CurrentTenantAccessor());

    private static async Task<SimplArchiveDbContext> FreshAsync()
    {
        var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var context = Context(connection);
        await context.Database.EnsureCreatedAsync();
        return context;
    }

    [Fact]
    public async Task No_module_declaring_the_capability_answers_NULL_so_the_core_resolves_as_before()
    {
        await using var db = await FreshAsync();
        var services = new StubServices();

        var answer = await new ModuleReaderCertificates(
            db, services, NullLogger<ModuleReaderCertificates>.Instance).ForAsync(Guid.NewGuid());

        Assert.Null(answer);
    }

    [Fact]
    public async Task A_module_that_declares_it_but_is_NOT_ACTIVE_answers_EMPTY_so_the_read_is_refused()
    {
        // The licence case, and the one that must not look like "no module". The tenant is in an enveloping
        // mode and nothing can envelope — that is a refusal, not a fallback to a weaker path.
        await using var db = await FreshAsync();
        var module = new EnrollingModule();
        var services = new StubServices
        {
            Modules = [new ModuleLoader.LoadedModule(module, "test://enc")],
        };

        var answer = await new ModuleReaderCertificates(
            db, services, NullLogger<ModuleReaderCertificates>.Instance).ForAsync(Guid.NewGuid());

        Assert.NotNull(answer);
        Assert.Empty(answer!);
    }

    /// <summary>The host provider, reduced to what this seam actually resolves from it.</summary>
    private sealed class StubServices : IServiceProvider
    {
        public IReadOnlyList<ModuleLoader.LoadedModule> Modules { get; init; } = [];

        public object? GetService(Type serviceType) =>
            serviceType == typeof(IReadOnlyList<ModuleLoader.LoadedModule>) ? Modules : null;
    }
}
