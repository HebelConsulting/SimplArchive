using System.Reflection;
using SimplArchive.Infrastructure.Modules;

namespace SimplArchive.UnitTests;

// A loaded module must be able to say WHICH BUILD it is (#1247).
//
// WHY. A stale module is invisible: it loads, seeds its masks, registers its controllers and serves requests,
// and only features added after the deployed build are missing — which reads as "never implemented" rather than
// "not deployed". The kiosk ran a module build four releases old, and establishing that took comparing file
// mtimes and SHA-256 sums off the host, because the process itself could not answer the question (#1242).
//
// WHAT IS ACTUALLY USABLE, and the reason this test exists rather than a glance at the field: the version
// NUMBER is worthless. Every module builds as 1.0.0 — no module declares a <Version>, so that is the SDK
// default and it is identical for all of them. The identity is the "+<sha>" suffix SourceLink stamps. A UI that
// renders "v1.0.0" and stops has surfaced nothing, and this asserts the part that discriminates.
public class ModuleBuildIsVisibleTests
{
    [Fact]
    public void A_module_assembly_carries_its_source_commit_so_a_stale_build_can_be_identified()
    {
        // The TestModule stands in for any module: same build pipeline, same stamping.
        var assembly = typeof(SimplArchive.TestModule.TestModule).Assembly;
        var build = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        Assert.False(string.IsNullOrWhiteSpace(build),
            "A module assembly carries no AssemblyInformationalVersionAttribute, so nothing can report which "
            + "build is loaded and #1247's admin surface would show a blank for every module. If module "
            + "projects stopped being built with source stamping, fix the build rather than this assertion.");

        // THE PART THAT DISCRIMINATES. Without the sha every module reports the same "1.0.0" and the surface is
        // decorative — which is exactly the "a guard that passes by seeing nothing" failure this codebase keeps
        // paying for. Asserting the shape here means a build that loses stamping fails LOUDLY rather than
        // quietly rendering an identical number for every module.
        Assert.Contains("+", build!, StringComparison.Ordinal);

        var sha = build!.Split('+', 2)[1];
        Assert.True(sha.Length >= 7 && sha.All(Uri.IsHexDigit),
            $"The build metadata after '+' is '{sha}', which is not a commit sha. The number before it is the "
            + "SDK default 1.0.0 for every module, so the sha is the only part that identifies a build.");
    }

    [Fact]
    public void The_loaded_module_record_carries_the_build_through_to_callers()
    {
        // The record is what the API and the admin surface read. Optional by design — a module whose assembly
        // carries no stamp is a real case and must not throw — but it must be able to carry one.
        var loaded = new ModuleLoader.LoadedModule(new StubModule(), "/modules/x/x.dll", "1.2.3+deadbeef");

        Assert.Equal("1.2.3+deadbeef", loaded.Build);
        Assert.Null(new ModuleLoader.LoadedModule(new StubModule(), "/modules/x/x.dll").Build);
    }

    // The interface's required members only; everything else has a default implementation.
    private sealed class StubModule : SimplArchive.ModuleAbi.IIndustryModule
    {
        public string ModuleId => "stub";
        public string DisplayName => "Stub";
        public int AbiMajorVersion => SimplArchive.ModuleAbi.ModuleAbiVersion.Major;
        public string LicenseVerifyKeyPem => string.Empty;
        public IReadOnlyList<SimplArchive.ModuleAbi.ModuleMaskSeed> Masks => [];

        public void ConfigureServices(Microsoft.Extensions.DependencyInjection.IServiceCollection services)
        {
        }
    }
}
