using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.ModuleAbi;

namespace SimplArchive.UnitTests;

// The module loader (ADR 0741): discovers real module assemblies from a Modules/ directory, isolates each
// in its own context with ABI types shared, refuses an ABI-major mismatch cleanly, and shrugs at debris.
// The fixture is SimplArchive.TestModule — a REAL module referencing only the ABI, which makes these tests
// double as the standing proof that the ABI suffices to write one.
public class ModuleLoaderTests
{
    private static string StageModulesDirectory()
    {
        // The fixture dll is copied next to this test assembly by its project reference; staging it into a
        // fresh directory is exactly a deployment's `Modules/` mount.
        var dir = Path.Combine(Path.GetTempPath(), $"simplarchive-modules-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var source = Path.Combine(AppContext.BaseDirectory, "SimplArchive.TestModule.dll");
        File.Copy(source, Path.Combine(dir, "SimplArchive.TestModule.dll"));
        return dir;
    }

    [Fact]
    public void Loads_a_module_and_its_contract_is_shared()
    {
        var dir = StageModulesDirectory();
        try
        {
            var loaded = ModuleLoader.LoadAll(dir, NullLogger.Instance);

            var module = Assert.Single(loaded).Module;
            Assert.Equal("test-module", module.ModuleId);

            // The mask seed arrives typed — IIndustryModule and the seed records are ONE type identity on
            // both sides of the boundary, which is the whole point of resolving the ABI from the default
            // context (a private copy would make this cast throw).
            // FIVE masks: dossier, certificate and entry (ADR 0738's shadowing lesson), plus the pair the
            // module-declared CalDAV collection needs — Test Log and Test Log Entry (#1242). The certificate is
            // the one whose typed fields prove the boundary; the count is here to prove the boundary carries
            // ALL of them, so it moves whenever the fixture grows and that is the point.
            Assert.Equal(5, module.Masks.Count);
            var mask = module.Masks.Single(m => m.Name == "Test Certificate");
            Assert.False(mask.IsBookable);

            // The one registration call: the module's services land in the host's collection.
            var services = new ServiceCollection();
            module.ConfigureServices(services);
            Assert.Contains(services, d => d.ServiceType.Name == "TestModuleMarker");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void An_abi_major_mismatch_is_refused()
    {
        // The rule itself, testable without loading anything (major locks — ADR 0741).
        Assert.True(ModuleLoader.AbiCompatible(ModuleAbiVersion.Major));
        Assert.False(ModuleLoader.AbiCompatible(ModuleAbiVersion.Major + 1));
        Assert.False(ModuleLoader.AbiCompatible(-1));
    }

    // A Choice with no choices (ABI 0.29) is a module-declaration defect whose symptoms all point at the CORE:
    // the form offers a chooser with nothing in it, and every value the tenant tries is refused by the very
    // validation that compares against the empty list. So the host says whose fault it is — once, at load,
    // because the defect is a property of the build rather than of a request.
    [Fact]
    public void A_choice_declaring_no_values_is_named_at_load()
    {
        var logger = new CapturingLogger();

        ModuleLoader.WarnAboutUnusableSettings(new SettingsModule(
        [
            new ModuleSetting("fine", "A text setting"),
            new ModuleSetting("posture", "Posture") { Kind = ModuleSettingKind.Choice, Choices = ["strict"] },
            new ModuleSetting("empty", "Chooses nothing") { Kind = ModuleSettingKind.Choice },
        ]), logger);

        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("empty", warning);
        Assert.Contains("settings-module", warning);

        // And the module still loads: an unusable form control is no reason to withhold its masks, its
        // controllers and its state machines from a tenant that is licensed for them.
        Assert.DoesNotContain("NOT loaded", warning);
    }

    private sealed class SettingsModule(IReadOnlyList<ModuleSetting> settings) : IIndustryModule
    {
        public string ModuleId => "settings-module";

        public string DisplayName => "Settings declaration fixture";

        public int AbiMajorVersion => ModuleAbiVersion.Major;

        public string LicenseVerifyKeyPem => string.Empty;

        public IReadOnlyList<ModuleMaskSeed> Masks => [];

        public IReadOnlyList<ModuleSetting> Settings { get; } = settings;

        public void ConfigureServices(IServiceCollection services)
        {
        }
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }

    [Fact]
    public void A_missing_directory_means_no_modules_not_an_error()
    {
        Assert.Empty(ModuleLoader.LoadAll(Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid():N}"), NullLogger.Instance));
    }

    [Fact]
    public void Debris_in_the_modules_directory_is_skipped()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"simplarchive-modules-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "not-a-dotnet-assembly.dll"), [0x4D, 0x5A, 0x00, 0x01]);
            Assert.Empty(ModuleLoader.LoadAll(dir, NullLogger.Instance));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
