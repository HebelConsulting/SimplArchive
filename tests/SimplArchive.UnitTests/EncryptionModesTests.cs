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
        Assert.False(Modes().WrapsAtRest("Demo"));
        Assert.False(Modes().RefusesPlaintextDoors("Demo"));
    }

    // The installation default is what keeps a NEWLY PROVISIONED tenant encrypted. Without it, per-tenant
    // entries alone would mean every new tenant starts unencrypted until somebody edits configuration — and
    // the cost of forgetting is silent.
    [Fact]
    public void The_default_mode_covers_every_tenant()
    {
        var modes = Modes(("Encryption:DefaultMode", "Storage"));

        Assert.True(modes.WrapsAtRest("Demo"));
        Assert.True(modes.WrapsAtRest("a-tenant-created-tomorrow"));
        Assert.False(modes.RefusesPlaintextDoors("Demo"));
    }

    [Fact]
    public void A_per_tenant_entry_overrides_the_default_in_both_directions()
    {
        var up = Modes(("Encryption:DefaultMode", "Storage"), ("Encryption:Modes:Ministry", "Strict"));
        Assert.True(up.RefusesPlaintextDoors("Ministry"));
        Assert.False(up.RefusesPlaintextDoors("Demo"));

        // Downwards too: a named tenant can be taken OUT of an installation-wide mode.
        var down = Modes(("Encryption:DefaultMode", "Storage"), ("Encryption:Modes:Public", "None"));
        Assert.False(down.WrapsAtRest("Public"));
        Assert.True(down.WrapsAtRest("Demo"));
    }

    // Strict IS at-rest-plus-envelope, so the incoherent state the two lists allowed — strict but not
    // encrypted at rest — cannot be written down any more. That is why the startup check for it is gone.
    [Fact]
    public void Strict_implies_at_rest_encryption_by_construction()
    {
        var modes = Modes(("Encryption:Modes:Ministry", "Strict"));

        Assert.True(modes.RefusesPlaintextDoors("Ministry"));
        Assert.True(modes.WrapsAtRest("Ministry"));
    }

    [Fact]
    public void Tenant_names_match_case_insensitively()
    {
        var modes = Modes(("Encryption:Modes:CryptoDemo", "Storage"));
        Assert.True(modes.WrapsAtRest("cryptodemo"));
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

    // ---- The modes are not a severity scale (#1411) ---------------------------------------------------
    //
    // `Applies` read `>= Storage`, which is true of today's three values and a trap for the fourth. Storage
    // names WHAT is protected and Strict names a POSTURE, so a delivery-only tier is stronger on delivery
    // while encrypting nothing at rest — wherever it were inserted, one comparison would be wrong, and wrong
    // SILENTLY: above Storage it turns at-rest wrapping on for a mode whose definition is that content is not
    // wrapped, for exactly the tenants in that mode.
    //
    // This test is the half that matters. It enumerates the enum, so a new value cannot be added without
    // classifying it — failing at the moment of the change, loudly, rather than at runtime, quietly, on one
    // tenant's content.

    [Fact]
    public void Only_the_modes_that_wrap_at_rest_apply()
    {
        foreach (var mode in Enum.GetValues<EncryptionMode>())
        {
            var wrapsAtRest = mode is EncryptionMode.Storage or EncryptionMode.Strict;

            Assert.Equal(wrapsAtRest, Modes((EncryptionModes.DefaultSection, mode.ToString())).WrapsAtRest("Any"));
        }
    }

    // EVERY mode against EVERY question, in one table. The three questions were one ordered comparison and
    // two predicates that happened to agree; #1411's fourth tier is stronger on delivery and weaker at rest,
    // so they come apart, and the table is what stops a later reader re-merging them.
    //
    // Read down a column, not across: `Storage` wraps and envelopes mail but delivers no envelopes and shuts
    // no doors; `SealedDeliveryPermissive` is its exact opposite on all four.
    [Theory]
    //                                        wraps  doors  envelopes  mail
    [InlineData(EncryptionMode.None, false, false, false, false)]
    [InlineData(EncryptionMode.Storage, true, false, false, true)]
    [InlineData(EncryptionMode.Strict, true, true, true, true)]
    [InlineData(EncryptionMode.SealedDeliveryPermissive, false, false, true, false)]
    [InlineData(EncryptionMode.SealedDeliveryStrict, false, true, true, false)]
    public void Each_mode_answers_all_four_questions_explicitly(
        EncryptionMode mode, bool wraps, bool shutsDoors, bool envelopes, bool mail)
    {
        var modes = Modes((EncryptionModes.Section + ":T", mode.ToString()));

        Assert.Equal(wraps, modes.WrapsAtRest("T"));
        Assert.Equal(shutsDoors, modes.RefusesPlaintextDoors("T"));
        Assert.Equal(envelopes, modes.DeliversEnvelopes("T"));
        Assert.Equal(mail, modes.EnvelopesMail("T"));
    }

    // THE TWO QUESTIONS DISAGREE, and exactly one tier is where they do.
    //
    // This is not a curiosity: `StrictEnvelopeDelivery` had ONE predicate answering both, and the find-in-
    // document overlay asked it. On a permissive tenant that refused the overlay — the door whose word
    // coordinates reconstruct the whole document — while download, preview and presigned URLs all served.
    // The posture inverted for the one door that most needs to follow it.
    //
    // Found by the owner asking what a permissive tenant serves a reader with NO certificate. Pinned here
    // because the mistake is invisible: both predicates are bools, both read true of Strict, and a call site
    // that picks the wrong one compiles and passes every test written before the delivery tiers existed.
    [Fact]
    public void Delivering_envelopes_and_refusing_plaintext_doors_are_different_questions()
    {
        var modes = Modes(
            (EncryptionModes.Section + ":Permissive", nameof(EncryptionMode.SealedDeliveryPermissive)),
            (EncryptionModes.Section + ":Shut", nameof(EncryptionMode.SealedDeliveryStrict)));

        // The permissive tier envelopes for whoever has a certificate AND keeps serving every other door,
        // which is what "permissive" means and why a reader without one is served plaintext there.
        Assert.True(modes.DeliversEnvelopes("Permissive"));
        Assert.False(modes.RefusesPlaintextDoors("Permissive"));

        // Its strict sibling answers the same first question and the opposite second one.
        Assert.True(modes.DeliversEnvelopes("Shut"));
        Assert.True(modes.RefusesPlaintextDoors("Shut"));
    }

    // A delivery tier needs no encryption SERVICE -- that is the whole point of the split (ADR 0834), and the
    // refusal it must not trip is the one written as an ordering (`> None`), which would have caught it.
    [Theory]
    [InlineData(EncryptionMode.SealedDeliveryPermissive)]
    [InlineData(EncryptionMode.SealedDeliveryStrict)]
    public void A_delivery_tier_is_not_refused_for_lacking_an_encryption_service(EncryptionMode mode)
    {
        var configuration = Config((EncryptionModes.Section + ":T", mode.ToString()));

        EncryptionModes.ThrowIfModeHasNoService(configuration);   // does not throw
    }

    // ...but it IS refused while nothing performs its delivery. Same failure as #1406 and refused the same
    // way: a mode with nothing behind it does not misbehave, it silently has no guarantee.
    [Theory]
    [InlineData(EncryptionMode.SealedDeliveryPermissive)]
    [InlineData(EncryptionMode.SealedDeliveryStrict)]
    public void A_delivery_tier_is_refused_while_the_module_does_not_exist(EncryptionMode mode)
    {
        var configuration = Config((EncryptionModes.Section + ":T", mode.ToString()));

        var error = Assert.Throws<InvalidOperationException>(
            () => EncryptionModes.ThrowIfSealedDeliveryHasNoModule(configuration));

        Assert.Contains("Encryption Module", error.Message);
        Assert.Contains($"{EncryptionModes.Section}:T", error.Message);
    }

    [Theory]
    [InlineData(EncryptionMode.None)]
    [InlineData(EncryptionMode.Storage)]
    [InlineData(EncryptionMode.Strict)]
    public void The_module_refusal_says_nothing_about_the_modes_that_do_not_need_one(EncryptionMode mode)
    {
        var configuration = Config((EncryptionModes.ServiceUrlKey, "http://enc"),
                                   (EncryptionModes.Section + ":T", mode.ToString()));

        EncryptionModes.ThrowIfSealedDeliveryHasNoModule(configuration);   // does not throw
    }

    [Fact]
    public void A_NEW_mode_must_be_classified_rather_than_inheriting_a_position()
    {
        // The guard above compares against a list written here; this one states WHY that list may not be
        // replaced by a comparison. If a fourth value is ever added and someone "simplifies" Applies back to
        // an ordering, exactly one of these two assertions breaks — whichever way they sort it.
        var values = Enum.GetValues<EncryptionMode>();

        Assert.Equal(EncryptionMode.None, values.Min());
        Assert.True(values.Length == 5,
            "A mode was added. `Applies` lists the modes that wrap at rest and must be reviewed — NOT by "
            + "sorting the enum: these values are not a scale (Storage names what is protected, Strict names a "
            + "posture, a delivery-only tier is stronger on delivery and encrypts nothing at rest). Decide "
            + "whether the new mode wraps, add it to that list or leave it out deliberately, then update this "
            + "count. See ADR 0834 and #1411.");
    }
}
