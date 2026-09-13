using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.ModuleAbi;

namespace SimplArchive.UnitTests;

/// <summary>
/// A broken module costs its own features and never the host (#1147).
/// </summary>
/// <remarks>
/// <para>
/// Written after a module built against ABI 0.20 met a 0.21 host, threw <c>MissingMethodException</c> from a
/// static initializer, and terminated the API — which then crash-looped, taking the public demo down for 94
/// minutes. The log said <c>Loaded module flight-school</c> one line above the fatal, because loading really
/// had succeeded: everything called AFTER the loader ran unguarded on the host's construction path.
/// </para>
/// <para>
/// ADR 0741 already promised the behaviour these tests assert. What was missing was anything that checked.
/// </para>
/// </remarks>
public class ModuleIsolationTests
{
    private sealed class ThrowingModule(string where) : IIndustryModule
    {
        public string ModuleId => "throwing-module";

        public string DisplayName => "Throwing Module";

        public int AbiMajorVersion => ModuleAbiVersion.Major;

        public string LicenseVerifyKeyPem => string.Empty;

        // The real failure was a TYPE INITIALIZER, which is why this throws from a property rather than a
        // method: a module's masks are usually a static field, and touching it is what detonated.
        public IReadOnlyList<ModuleMaskSeed> Masks =>
            where == "Masks" ? throw new MissingMethodException("Method not found: ModuleMaskSeed..ctor") : [];

        public void ConfigureServices(Microsoft.Extensions.DependencyInjection.IServiceCollection services)
        {
            if (where == "ConfigureServices")
            {
                throw new MissingMethodException("Method not found: ModuleMaskSeed..ctor");
            }
        }

        public void DefineStateMachines(IStateMachineDefinitions machines)
        {
            if (where == "DefineStateMachines")
            {
                throw new TypeInitializationException("Masks", new MissingMethodException("ctor"));
            }
        }
    }

    private static ModuleLoader.LoadedModule Loaded(string throwsIn) =>
        new(new ThrowingModule(throwsIn), "/app/Modules/throwing/Throwing.dll");

    [Theory]
    [InlineData("ConfigureServices")]
    [InlineData("DefineStateMachines")]
    [InlineData("Masks")]
    public void A_module_that_throws_at_startup_is_refused_rather_than_fatal(string seam)
    {
        var module = Loaded(seam);

        // The seam is invoked the way Program.cs invokes it. The assertion is that this RETURNS — before
        // #1147 the equivalent call propagated out of host construction and killed the process.
        var survived = ModuleStartup.TryRun(module, seam, NullLogger.Instance, () =>
        {
            module.Module.ConfigureServices(new Microsoft.Extensions.DependencyInjection.ServiceCollection());
            module.Module.DefineStateMachines(new SimplArchive.Infrastructure.Modules.StateMachineCatalog().ForModule("throwing-module"));
            _ = module.Module.Masks.Count;
        });

        Assert.False(survived, "a module that threw must be reported as failed, not silently treated as healthy");
    }

    [Fact]
    public void A_module_that_starts_cleanly_is_reported_as_healthy()
    {
        // The counterpart, so the guard cannot pass by refusing everything.
        Assert.True(ModuleStartup.TryRun(Loaded("nowhere"), "ConfigureServices", NullLogger.Instance, () => { }));
    }

    // ---- the version gate ---------------------------------------------------------------------------

    [Fact]
    public void A_module_built_against_a_NEWER_minor_is_refused()
    {
        // It may call members this host does not have. Unrefused, that arrives as a MissingMethodException
        // from somewhere unrelated — which is how this whole class of failure presents.
        Assert.False(ModuleLoader.MinorCompatible(ModuleAbiVersion.Minor + 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    public void A_module_built_against_an_OLDER_minor_is_ACCEPTED(int moduleMinor)
    {
        // "Minor floats" (ADR 0741), and this direction is the one that must keep working — it is what the
        // additive-ABI rule exists to protect. Note the gate could never have prevented the outage: the
        // broken case was an older module on a newer host, which this deliberately allows. Only
        // ModuleMaskSeed's shape makes that safe.
        Assert.True(ModuleLoader.MinorCompatible(moduleMinor));
    }

    [Fact]
    public void The_current_minor_is_accepted()
    {
        Assert.True(ModuleLoader.MinorCompatible(ModuleAbiVersion.Minor));
    }

    // ---- the additive rule --------------------------------------------------------------------------

    [Fact]
    public void ModuleMaskSeed_can_still_be_constructed_the_way_an_OLDER_module_does()
    {
        // THE REGRESSION TEST FOR THE OUTAGE. A module compiled against 0.20 calls this exact constructor
        // shape. If someone adds a parameter to the primary constructor again, old modules stop binding to
        // it — they load, then throw MissingMethodException from a static initializer and kill the host.
        //
        // This cannot fail at runtime the way the real thing did (the test compiles against today's ABI),
        // so what it really pins is the SIGNATURE: the call below must remain valid with exactly these
        // arguments and no more.
        var seed = new ModuleMaskSeed(
            Guid.NewGuid(),
            "Medical",
            IsFolderMask: false,
            IsBookable: false,
            Fields: [],
            AdmitsOnlyDeclaredChildren: false,
            AdmittedChildren: null,
            AllowedParents: null,
            RepresentsPrincipalField: null);

        Assert.Null(seed.NameVocabularyRel);

        // And the addition is reachable the additive way — an object initialiser, never a tenth parameter.
        Assert.Equal("x:y", (seed with { NameVocabularyRel = "x:y" }).NameVocabularyRel);
    }
}
