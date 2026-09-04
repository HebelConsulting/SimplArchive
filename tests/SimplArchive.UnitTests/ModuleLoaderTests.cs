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
            // Three masks since the fixture's entries earned their own (ADR 0738's shadowing lesson);
            // the certificate is the one whose typed fields prove the boundary.
            Assert.Equal(3, module.Masks.Count);
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
