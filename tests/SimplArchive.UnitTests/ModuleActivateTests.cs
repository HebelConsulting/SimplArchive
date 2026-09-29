using System.Text;
using System.Text.Json;
using SimplArchive.Cli.Commands;
using SimplArchive.Cli.Infrastructure;

namespace SimplArchive.UnitTests;

/// <summary>
/// `saconsole module activate` (#1474): what it refuses before filing anything, and how it splits a licence's
/// modules against the ones an installation actually carries.
/// </summary>
/// <remarks>
/// The split is the part worth testing rather than the HTTP. A licence names a LIST (ADR 0845), so the
/// outcome that matters is PARTIAL activation — some modules activated, some entitled but not deployed — and
/// that is the state hardest to notice, because each missing module reports its own absence as though the
/// licence were at fault.
/// </remarks>
public sealed class ModuleActivateTests
{
    private static byte[] Licence(string moduleIds, string end = "2027-09-30") =>
        Encoding.UTF8.GetBytes($$"""
            {"moduleIds":{{moduleIds}},"tenantId":"746a22de-2d1c-5b70-8888-ea12c0c8ffec",
             "supportContractEnd":"{{end}}","abiMajorVersion":1,"signature":"ZmFrZQ=="}
            """);

    private static JsonElement Modules(params string[] moduleIds)
    {
        var items = moduleIds.Select(id =>
            $$"""{"moduleId":"{{id}}","links":[{"rel":"license","href":"/api/modules/{{id}}/license","method":"PUT"}]}""");

        return JsonDocument.Parse($$"""{"items":[{{string.Join(",", items)}}]}""").RootElement.Clone();
    }

    [Fact]
    public void A_file_that_is_not_json_is_refused_as_not_a_licence()
    {
        var refusal = Assert.Throws<CliException>(
            () => LicenceClaims.Read(Encoding.UTF8.GetBytes("not json"), "/tmp/x.json"));

        Assert.Contains("not JSON", refusal.Message);
    }

    [Fact] // a SERVICE licence is a different artefact and is not filed in a tenant (ADR 0845)
    public void Json_without_moduleIds_is_refused_and_says_which_artefact_it_might_be()
    {
        var refusal = Assert.Throws<CliException>(
            () => LicenceClaims.Read(Encoding.UTF8.GetBytes("""{"installationId":"x"}"""), "/tmp/svc.json"));

        Assert.Contains("moduleIds", refusal.Message);
        Assert.Contains("SERVICE licence", refusal.Message);
    }

    [Fact]
    public void A_licence_naming_no_modules_is_refused_before_anything_is_filed()
    {
        var refusal = Assert.Throws<CliException>(() => LicenceClaims.Read(Licence("[]"), "/tmp/empty.json"));

        Assert.Contains("names no modules", refusal.Message);
    }

    [Fact]
    public void The_claims_read_are_the_module_list_and_the_expiry()
    {
        var claims = LicenceClaims.Read(Licence("""["flight-school","encryption"]"""), "/tmp/l.json");

        Assert.Equal(["flight-school", "encryption"], claims.ModuleIds);
        Assert.Equal("2027-09-30", claims.SupportContractEnd);
    }

    [Fact]
    public void Every_module_the_licence_names_and_the_installation_carries_is_activated()
    {
        var claims = LicenceClaims.Read(Licence("""["flight-school","encryption"]"""), "/tmp/l.json");

        var targets = ModuleTargets.Resolve(Modules("flight-school", "encryption"), claims.ModuleIds, only: null);

        Assert.Equal(["flight-school", "encryption"], targets.Activatable.Select(t => t.ModuleId));
        Assert.Empty(targets.NotInstalled);
    }

    [Fact] // the quiet half of a partial activation
    public void A_module_entitled_but_not_deployed_is_reported_rather_than_attempted()
    {
        var claims = LicenceClaims.Read(Licence("""["flight-school","encryption"]"""), "/tmp/l.json");

        var targets = ModuleTargets.Resolve(Modules("flight-school"), claims.ModuleIds, only: null);

        Assert.Equal(["flight-school"], targets.Activatable.Select(t => t.ModuleId));
        Assert.Equal(["encryption"], targets.NotInstalled);
    }

    [Fact]
    public void Narrowing_to_one_module_activates_only_that_one()
    {
        var claims = LicenceClaims.Read(Licence("""["flight-school","encryption"]"""), "/tmp/l.json");

        var targets = ModuleTargets.Resolve(Modules("flight-school", "encryption"), claims.ModuleIds, only: "encryption");

        Assert.Equal(["encryption"], targets.Activatable.Select(t => t.ModuleId));
        Assert.Empty(targets.NotInstalled);
    }

    [Fact]
    public void Narrowing_to_a_module_the_licence_does_not_name_says_what_it_does_entitle()
    {
        var claims = LicenceClaims.Read(Licence("""["flight-school"]"""), "/tmp/l.json");

        var refusal = Assert.Throws<CliException>(
            () => ModuleTargets.Resolve(Modules("flight-school"), claims.ModuleIds, only: "warehouse"));

        Assert.Contains("does not name 'warehouse'", refusal.Message);
        Assert.Contains("flight-school", refusal.Message);
    }

    [Fact] // an id is a KEY, not a display name — a case-insensitive match would activate what nobody named
    public void Module_ids_are_matched_ordinally()
    {
        var claims = LicenceClaims.Read(Licence("""["Flight-School"]"""), "/tmp/l.json");

        var targets = ModuleTargets.Resolve(Modules("flight-school"), claims.ModuleIds, only: null);

        Assert.Empty(targets.Activatable);
        Assert.Equal(["Flight-School"], targets.NotInstalled);
    }

    [Fact]
    public void A_module_that_advertises_no_license_rel_is_not_activatable()
    {
        // ADR 0543: a missing rel means "not available to you, here, now". For this surface the cause is
        // almost always that the caller is not a tenant administrator.
        var modules = JsonDocument.Parse("""{"items":[{"moduleId":"encryption","links":[]}]}""").RootElement;
        var claims = LicenceClaims.Read(Licence("""["encryption"]"""), "/tmp/l.json");

        var targets = ModuleTargets.Resolve(modules, claims.ModuleIds, only: null);

        Assert.Empty(targets.Activatable);
        Assert.Equal(["encryption"], targets.Installed);
        Assert.Contains("tenant-administrator", Describe.NothingToActivate(claims, targets));
    }

    [Fact]
    public void Nothing_to_activate_names_both_lists_and_says_nothing_was_filed()
    {
        var claims = LicenceClaims.Read(Licence("""["warehouse"]"""), "/tmp/l.json");
        var targets = ModuleTargets.Resolve(Modules("flight-school"), claims.ModuleIds, only: null);

        var message = Describe.NothingToActivate(claims, targets);

        Assert.Contains("warehouse", message);
        Assert.Contains("flight-school", message);
        Assert.Contains("Nothing filed", message);
    }

    [Fact]
    public void The_default_document_name_names_the_modules_and_the_expiry()
    {
        var claims = LicenceClaims.Read(Licence("""["flight-school","encryption"]"""), "/tmp/l.json");

        Assert.Equal("Licence — flight-school, encryption — through 2027-09-30", Describe.DefaultName(claims));
    }
}
