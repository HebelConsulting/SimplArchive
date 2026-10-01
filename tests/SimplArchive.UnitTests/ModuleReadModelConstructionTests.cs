using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.ModuleAbi;

namespace SimplArchive.UnitTests;

// How the host CONSTRUCTS a module's read-model context, and what it refuses (#1475, ADR 0738).
//
// The host builds a module context itself on exactly one path — the owner-connection migration (ADR 0721) —
// and that path runs only when ConnectionStrings:Migration is set, so no test suite ever reached it. The
// Encryption Module wrote the constructor EF documents, built clean, passed 47/47 of its own tests, and died
// in db-migrate with a MissingMethodException from inside Activator that read as a host defect. These tests
// drive the host's REAL helpers rather than a copy, because a copy is what let the contract go unwritten.
public class ModuleReadModelConstructionTests
{
    private const string AnyConnection = "Host=localhost;Database=whatever;Username=u;Password=p";

    // The shape EF documents and every tutorial writes — the one that used to be the only failing one.
    private sealed class GenericOptionsContext(DbContextOptions<GenericOptionsContext> options)
        : ModuleDbContext(options);

    // The shape the test module and flight school happened to write, which is why the contract held by
    // coincidence across the only two modules that existed.
    private sealed class NonGenericOptionsContext(DbContextOptions options) : ModuleDbContext(options);

    // What a module author reaching for an injected dependency writes. There is no container on the
    // migration path, so this cannot work — and must therefore be refused where the author is.
    private sealed class WantsADependencyContext(DbContextOptions options, TimeProvider clock)
        : ModuleDbContext(options)
    {
        public TimeProvider Clock { get; } = clock;
    }

    private sealed class NoPublicConstructorContext : ModuleDbContext
    {
        private NoPublicConstructorContext(DbContextOptions options) : base(options)
        {
        }
    }

    public static TheoryData<Type> EveryShape =>
    [
        typeof(GenericOptionsContext),
        typeof(NonGenericOptionsContext),
        typeof(WantsADependencyContext),
        typeof(NoPublicConstructorContext),
    ];

    [Theory]
    [InlineData(typeof(GenericOptionsContext))]
    [InlineData(typeof(NonGenericOptionsContext))]
    public void The_host_constructs_either_documented_shape(Type contextType)
    {
        // The host's own helper, not a reconstruction of it: the defect was IN this line.
        var options = ModuleReadModelWiring.OwnerOptions(contextType, AnyConnection, "some-module");

        using var context = (DbContext)Activator.CreateInstance(contextType, options)!;

        Assert.IsType(contextType, context);
    }

    // The anti-drift guard, and the reason this file exists in this shape: the load-time predicate and the
    // construction it stands in for must agree about every shape, or the loader refuses a module that would
    // have worked (or admits one that will die at migration). Asserting a particular constructor SIGNATURE
    // instead would pin a spelling of the contract rather than the contract.
    [Theory]
    [MemberData(nameof(EveryShape))]
    public void The_load_check_agrees_with_what_construction_actually_does(Type contextType)
    {
        var options = ModuleReadModelWiring.OwnerOptions(contextType, AnyConnection, "some-module");

        bool constructs;
        try
        {
            using var context = (DbContext)Activator.CreateInstance(contextType, options)!;
            constructs = true;
        }
        catch (MissingMethodException)
        {
            constructs = false;
        }

        Assert.Equal(constructs, ModuleReadModelWiring.CanHostConstruct(contextType));
    }

    [Fact]
    public void A_module_whose_context_cannot_be_constructed_is_refused_by_name()
    {
        var logger = new CapturingLogger();

        var refused = ModuleLoader.ReadModelsAreConstructible(
            new FakeModule(typeof(WantsADependencyContext)), "/modules/fake.dll", logger);

        Assert.False(refused);

        // Named, and actionable: which module, which context, and what constructor it needs. The message is
        // the whole point — the failure it replaces named none of the three.
        var said = Assert.Single(logger.Warnings);
        Assert.Contains("fake-module", said);
        Assert.Contains(nameof(WantsADependencyContext), said);
        Assert.Contains("DbContextOptions", said);
        Assert.Contains("NOT loaded", said);
    }

    [Theory]
    [InlineData(typeof(GenericOptionsContext))]
    [InlineData(typeof(NonGenericOptionsContext))]
    public void A_module_writing_either_documented_shape_loads_silently(Type contextType)
    {
        var logger = new CapturingLogger();

        Assert.True(ModuleLoader.ReadModelsAreConstructible(
            new FakeModule(contextType), "/modules/fake.dll", logger));

        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public void A_module_declaring_no_read_model_is_not_refused()
    {
        var logger = new CapturingLogger();

        Assert.True(ModuleLoader.ReadModelsAreConstructible(
            new FakeModule(contextType: null), "/modules/fake.dll", logger));

        Assert.Empty(logger.Warnings);
    }

    private sealed class FakeModule(Type? contextType) : IIndustryModule
    {
        public string ModuleId => "fake-module";

        public string DisplayName => "Fake";

        public int AbiMajorVersion => ModuleAbiVersion.Major;

        public string LicenseVerifyKeyPem => string.Empty;

        public IReadOnlyList<ModuleMaskSeed> Masks => [];

        public IReadOnlyList<ModuleReadModelSet> ReadModels =>
            contextType is null ? [] : [new ModuleReadModelSet(contextType)];

        public void ConfigureServices(IServiceCollection services)
        {
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
