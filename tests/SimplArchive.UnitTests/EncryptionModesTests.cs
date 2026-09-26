using Microsoft.Extensions.Configuration;
using SimplArchive.Infrastructure.Encryption;

namespace SimplArchive.UnitTests;

// Which encryption mode a tenant is in (ADR 0825, epic #1351).
//
// This replaced two lists — Encryption:Tenants and Encryption:StrictTenants — that looked identical and
// behaved oppositely on "empty": the first meant EVERY tenant, the second meant NO tenant. Each default was
// individually right for its own tier, which is exactly what made the pair dangerous: a reader infers the
// second from the first and is wrong, and nothing says so. One setting cannot be asymmetric with itself.
public class EncryptionModesTests
{
    private static IConfiguration Config(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

    private static EncryptionModes Modes(params (string Key, string Value)[] settings) => new(Config(settings));

    [Fact]
    public void Nothing_configured_means_no_encryption()
    {
        Assert.Equal(EncryptionMode.None, Modes().ModeFor("Demo"));
        Assert.False(Modes().Applies("Demo"));
        Assert.False(Modes().IsStrict("Demo"));
    }

    // The installation default is what keeps a NEWLY PROVISIONED tenant encrypted. Without it, per-tenant
    // entries alone would mean every new tenant starts unencrypted until somebody edits configuration — and
    // the cost of forgetting is silent.
    [Fact]
    public void The_default_mode_covers_every_tenant()
    {
        var modes = Modes(("Encryption:DefaultMode", "Storage"));

        Assert.True(modes.Applies("Demo"));
        Assert.True(modes.Applies("a-tenant-created-tomorrow"));
        Assert.False(modes.IsStrict("Demo"));
    }

    [Fact]
    public void A_per_tenant_entry_overrides_the_default_in_both_directions()
    {
        var up = Modes(("Encryption:DefaultMode", "Storage"), ("Encryption:Modes:Ministry", "Strict"));
        Assert.True(up.IsStrict("Ministry"));
        Assert.False(up.IsStrict("Demo"));

        // Downwards too: a named tenant can be taken OUT of an installation-wide mode.
        var down = Modes(("Encryption:DefaultMode", "Storage"), ("Encryption:Modes:Public", "None"));
        Assert.False(down.Applies("Public"));
        Assert.True(down.Applies("Demo"));
    }

    // Strict IS at-rest-plus-envelope, so the incoherent state the two lists allowed — strict but not
    // encrypted at rest — cannot be written down any more. That is why the startup check for it is gone.
    [Fact]
    public void Strict_implies_at_rest_encryption_by_construction()
    {
        var modes = Modes(("Encryption:Modes:Ministry", "Strict"));

        Assert.True(modes.IsStrict("Ministry"));
        Assert.True(modes.Applies("Ministry"));
    }

    [Fact]
    public void Tenant_names_match_case_insensitively()
    {
        var modes = Modes(("Encryption:Modes:CryptoDemo", "Storage"));
        Assert.True(modes.Applies("cryptodemo"));
    }

    // An unrecognised mode is refused, never silently treated as None — which would switch encryption off
    // for a typo.
    [Theory]
    [InlineData("Encryption:DefaultMode")]
    [InlineData("Encryption:Modes:Demo")]
    public void An_unrecognised_mode_is_refused_rather_than_ignored(string key)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Modes((key, "Strong")).ModeFor("Demo"));

        Assert.Contains("Strong", error.Message, StringComparison.Ordinal);
        Assert.Contains("Strict", error.Message, StringComparison.Ordinal);
    }

    // The retired keys must not be silently ignored: an installation still listing tenants under
    // Encryption:Tenants would keep running with encryption switched OFF — every surface correct, the
    // guarantee absent.
    [Fact]
    public void The_retired_keys_refuse_to_start_and_name_the_replacement()
    {
        var configuration = Config(("Encryption:Tenants:0", "Crypto"));

        var error = Assert.Throws<InvalidOperationException>(
            () => EncryptionModes.ThrowIfLegacyConfigured(configuration));

        Assert.Contains("Encryption:Tenants", error.Message, StringComparison.Ordinal);
        // It translates rather than merely complaining — the named tenant appears in the suggested config.
        Assert.Contains("Crypto: Storage", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_legacy_list_still_refuses_and_suggests_the_default()
    {
        // The dev stack's old form: Encryption__Tenants__0 with an empty value, meaning "every tenant".
        var configuration = Config(("Encryption:Tenants:0", string.Empty));

        var error = Assert.Throws<InvalidOperationException>(
            () => EncryptionModes.ThrowIfLegacyConfigured(configuration));

        Assert.Contains("Encryption/DefaultMode: Storage", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_clean_configuration_starts()
    {
        EncryptionModes.ThrowIfLegacyConfigured(Config(
            ("Encryption:DefaultMode", "Storage"), ("Encryption:Modes:Ministry", "Strict")));
    }

    // The constraint that forced this shape, pinned so nobody "tidies" it back to a wildcard key: every
    // setting must be expressible as an environment variable, because the dev stack and the kiosk configure
    // through compose. `*` is not a legal env-var name and a tenant name cannot be interpolated into a key.
    [Fact]
    public void Every_setting_is_expressible_as_an_environment_variable()
    {
        foreach (var key in new[] { EncryptionModes.DefaultSection, $"{EncryptionModes.Section}:Ministry" })
        {
            var asEnvironmentVariable = key.Replace(':', '_').Replace("_", "__");

            Assert.True(asEnvironmentVariable.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'),
                $"'{key}' becomes '{asEnvironmentVariable}', which is not a legal environment variable name — "
                + "so it cannot be set from docker compose, which is how the dev stack and the kiosk are "
                + "configured. This is why the installation-wide mode is its own key rather than a '*' entry.");
        }
    }

    // ---- A mode nothing can perform (#1406) -----------------------------------------------------------
    //
    // MEASURED before this refusal existed: a tenant set to Strict with no Encryption:ServiceUrl advertised a
    // presigned object-storage URL, and fetching it with NO CREDENTIALS returned `BEGIN:VCALENDAR`. Readable
    // content, from a tenant configured Strict, to a caller with none.
    //
    // The cause is that the two halves are different settings — this class reads the mode map, while the
    // refusal to hand out readable bytes lives in EncryptingObjectStorageClient, which is registered only when
    // the service URL is set. So the mode said one thing, the doors did another, and nothing reported it.

    [Theory]
    [InlineData("Storage")]
    [InlineData("Strict")]
    public void A_default_mode_with_no_service_refuses_to_start(string mode)
    {
        var thrown = Assert.Throws<InvalidOperationException>(
            () => EncryptionModes.ThrowIfModeHasNoService(Config((EncryptionModes.DefaultSection, mode))));

        // The message must name BOTH halves: what was claimed, and the setting that is missing. Naming only
        // one sends the reader to change the wrong thing.
        Assert.Contains(mode, thrown.Message, StringComparison.Ordinal);
        Assert.Contains(EncryptionModes.ServiceUrlKey, thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_PER_TENANT_mode_with_no_service_refuses_too_and_names_the_tenant()
    {
        // The likelier shape in practice: the installation default is None and one tenant was raised — which
        // is exactly how the kiosk's CryptoDemo tenant is configured.
        var thrown = Assert.Throws<InvalidOperationException>(
            () => EncryptionModes.ThrowIfModeHasNoService(Config(($"{EncryptionModes.Section}:CryptoDemo", "Strict"))));

        Assert.Contains("CryptoDemo", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void With_the_service_configured_any_mode_is_fine()
    {
        EncryptionModes.ThrowIfModeHasNoService(Config(
            (EncryptionModes.ServiceUrlKey, "http://encryption:8080"),
            (EncryptionModes.DefaultSection, "Strict"),
            ($"{EncryptionModes.Section}:Acme", "Storage")));
    }

    [Fact]
    public void None_needs_no_service_and_that_is_the_ordinary_installation()
    {
        // The anti-vacuous half, and by far the commonest configuration: no service, no modes, nothing to
        // refuse. A guard that failed here would stop every installation that does not buy encryption.
        EncryptionModes.ThrowIfModeHasNoService(Config());
        EncryptionModes.ThrowIfModeHasNoService(Config((EncryptionModes.DefaultSection, "None")));
        EncryptionModes.ThrowIfModeHasNoService(Config(($"{EncryptionModes.Section}:Acme", "None")));
    }

    [Fact]
    public void An_empty_service_url_counts_as_absent()
    {
        // `Encryption__ServiceUrl:` in compose yields an EMPTY string, not a missing key — which is exactly
        // how the development stack spells "no encryption service" (ENCRYPTION_SERVICE_URL:-), so a
        // null-only check would have let the dev stack straight past this guard.
        Assert.Throws<InvalidOperationException>(() => EncryptionModes.ThrowIfModeHasNoService(Config(
            (EncryptionModes.ServiceUrlKey, string.Empty),
            (EncryptionModes.DefaultSection, "Strict"))));
    }
}
