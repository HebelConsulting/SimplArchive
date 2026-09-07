using System.Globalization;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.ModuleAbi;

namespace SimplArchive.UnitTests;

// The catalog lookup (ABI 0.10, ADR 0767): culture → en → null; "CODE.arg0" before "CODE" (the
// role-dependent-sentence trick); args formatted {0}-style, tolerant of surplus slots.
public class ModuleTextResolverTests
{
    private sealed class CatalogModule : IIndustryModule
    {
        public string ModuleId => "fs";
        public string DisplayName => "FS";
        public int AbiMajorVersion => ModuleAbiVersion.Major;
        public string LicenseVerifyKeyPem => string.Empty;
        public IReadOnlyList<ModuleMaskSeed> Masks => [];
        public void ConfigureServices(Microsoft.Extensions.DependencyInjection.IServiceCollection services) { }
        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> LocalizedTexts { get; } =
            new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["en"] = new Dictionary<string, string>
                {
                    ["FLIGHT_X"] = "X happened: {0}.",
                    ["FLIGHT_SIGNER.pilot"] = "Only the pilot.",
                    ["FLIGHT_SIGNER.instructor"] = "Only the instructor.",
                },
                ["de"] = new Dictionary<string, string> { ["FLIGHT_X"] = "X passierte: {0}." },
            };
    }

    private static ModuleLoader.LoadedModule[] Modules() => [new ModuleLoader.LoadedModule(new CatalogModule(), "test://catalog")];

    [Fact]
    public void The_request_culture_wins_and_falls_back_to_english()
    {
        Assert.Equal("X passierte: {0}.",
            ModuleTextResolver.Resolve(Modules(), "fs", "FLIGHT_X", null, new CultureInfo("de-CH"))?.Template);
        Assert.Equal("X happened: {0}.",
            ModuleTextResolver.Resolve(Modules(), "fs", "FLIGHT_X", null, new CultureInfo("it"))?.Template);
        Assert.Null(ModuleTextResolver.Resolve(Modules(), "fs", "FLIGHT_UNKNOWN", null, new CultureInfo("de")));
    }

    [Fact]
    public void The_first_arg_selects_the_role_variant_before_the_bare_code()
    {
        Assert.Equal("Only the instructor.",
            ModuleTextResolver.Resolve(Modules(), "fs", "FLIGHT_SIGNER", "instructor", new CultureInfo("en"))?.Template);
    }

    [Fact]
    public void An_unknown_acting_module_is_found_by_its_code()
    {
        var resolved = ModuleTextResolver.Resolve(Modules(), null, "FLIGHT_X", null, new CultureInfo("en"));
        Assert.Equal("fs", resolved?.ModuleId);
    }

    [Fact]
    public void Formatting_substitutes_in_order_and_survives_surplus_slots()
    {
        Assert.Equal("a and b, then {2}", ModuleTextResolver.Format("{0} and {1}, then {2}", ["a", "b"]));
    }
}
