using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using SimplArchive.Api.Configuration;

namespace SimplArchive.IntegrationTests;

// Verifies the fail-fast production hardening checks (ADR "Fail-fast production hardening"): Development skips
// them; a Production config with dev-grade settings is refused (listing every violation); a clean Production
// config passes.
public class ProductionReadinessValidatorTests
{
    private sealed class StubEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static readonly Dictionary<string, string?> CleanProduction = new()
    {
        ["OpenIddict:SigningCertificatePem"] = "-----BEGIN CERTIFICATE-----...",
        ["OpenIddict:EncryptionCertificatePem"] = "-----BEGIN CERTIFICATE-----...",
        ["App:ApplyMigrationsAtStartup"] = "false",
        ["Bootstrap:PlatformAdministrator:ClientSecret"] = "a-real-secret",
        ["ObjectStorage:AccessKey"] = "AKIAREAL",
        ["ObjectStorage:SecretKey"] = "realsecretkey",
        ["ConnectionStrings:Default"] = "Host=pg;Port=5432;Database=simplarchive;Username=app;Password=s3cret",
        // A clean production config now INCLUDES OpenBao (#847, A02). Without it the transit encryptor is a
        // pass-through and every secret it protects is stored in plaintext, so its absence is a dev-grade
        // setting like the others in this dictionary rather than an optional extra.
        ["OpenBao:Address"] = "https://openbao.internal:8200",
    };

    [Fact]
    public void Development_skips_all_checks_even_with_dev_grade_settings()
    {
        var config = Config(new()
        {
            ["App:ApplyMigrationsAtStartup"] = "true",
            ["ObjectStorage:AccessKey"] = "minioadmin",
            ["Bootstrap:PlatformAdministrator:ClientSecret"] = "dev-bootstrap-secret",
            ["Demo:Administrator:Password"] = "SimplDemo2026!",
        });

        Assert.Empty(ProductionReadinessValidator.Validate(config, new StubEnvironment { EnvironmentName = "Development" }));
    }

    // A real release build stamps its version; the test assembly never does, so it must be supplied here or
    // every "clean production" case would fail on the unstamped check rather than on what it is testing.
    private const string StampedVersion = "0.1.6";

    // A deployment that cannot say which build it is has no business in production: it makes the desktop's
    // "are you behind this deployment?" check meaningless (ADR 0512) and leaves a post-mortem unable to answer
    // the first question it will ask (issue #425).
    [Fact]
    public void Production_refuses_an_unstamped_build()
    {
        var violations = ProductionReadinessValidator.Validate(
            Config(CleanProduction), new StubEnvironment(), SimplArchive.Api.ServerBuildInfo.UnstampedVersion);

        Assert.Contains(violations, v => v.Contains("unstamped", StringComparison.OrdinalIgnoreCase));

        // …and it is the ONLY complaint, so this really is about the stamp rather than the rest of the config.
        Assert.Single(violations);
    }

    [Fact]
    public void Production_with_a_clean_config_passes()
    {
        Assert.Empty(ProductionReadinessValidator.Validate(Config(CleanProduction), new StubEnvironment(), StampedVersion));
        ProductionReadinessValidator.ThrowIfNotProductionReady(Config(CleanProduction), new StubEnvironment(), StampedVersion); // no throw
    }

    [Fact]
    public void Production_with_dev_grade_settings_reports_every_violation()
    {
        var config = Config(new()
        {
            // No OpenIddict cert PEMs → dev-cert fallback.
            ["App:ApplyMigrationsAtStartup"] = "true",
            ["Demo:Administrator:Password"] = "SimplDemo2026!",
            ["Bootstrap:PlatformAdministrator:ClientSecret"] = "dev-bootstrap-secret",
            ["ObjectStorage:AccessKey"] = "minioadmin",
            ["ObjectStorage:SecretKey"] = "minioadmin",
            ["ConnectionStrings:Default"] = "Host=db;Port=5432;Database=simplarchive;Username=postgres;Password=postgres",
            // OpenBao:Address unset → the dev Postgres password check applies.
        });

        var violations = ProductionReadinessValidator.Validate(config, new StubEnvironment(), StampedVersion);

        Assert.Contains(violations, v => v.Contains("OpenIddict"));
        Assert.Contains(violations, v => v.Contains("ApplyMigrationsAtStartup"));
        Assert.Contains(violations, v => v.Contains("Demo"));
        Assert.Contains(violations, v => v.Contains("Bootstrap"));
        Assert.Contains(violations, v => v.Contains("MinIO"));
        Assert.Contains(violations, v => v.Contains("Postgres password"));

        var ex = Assert.Throws<InvalidOperationException>(() => ProductionReadinessValidator.ThrowIfNotProductionReady(config, new StubEnvironment()));
        Assert.Contains("Refusing to start", ex.Message);
    }

    [Fact]
    public void OpenBao_configured_connection_isnt_flagged_for_the_dev_postgres_password()
    {
        // With OpenBao composing the connection, the "dev Postgres password" heuristic doesn't apply.
        var config = Config(new(CleanProduction)
        {
            ["OpenBao:Address"] = "https://openbao:8200",
            ["ConnectionStrings:Default"] = "Host=db;Port=5432;Database=simplarchive;Username=postgres;Password=postgres",
        });

        Assert.DoesNotContain(ProductionReadinessValidator.Validate(config, new StubEnvironment(), StampedVersion), v => v.Contains("Postgres password"));
    }

    // ---- Trust-any-proxy (#847, A05) -----------------------------------------------------------------

    [Fact]
    public void Trusting_forwarded_headers_without_naming_a_proxy_is_refused()
    {
        var config = Config(new(CleanProduction) { ["App:TrustProxyHeaders"] = "true" });

        var violations = ProductionReadinessValidator.Validate(config, new StubEnvironment(), "1.2.3");

        Assert.Contains(violations, v => v.Contains("ANY peer", StringComparison.Ordinal));
    }

    [Fact]
    public void Naming_a_network_or_an_address_satisfies_it()
    {
        foreach (var named in new[]
                 {
                     ("App:KnownProxyNetworks", "10.0.0.0/8"),
                     ("App:KnownProxies", "10.1.2.3"),
                 })
        {
            var config = Config(new(CleanProduction)
            {
                ["App:TrustProxyHeaders"] = "true",
                [named.Item1] = named.Item2,
            });

            Assert.Empty(ProductionReadinessValidator.Validate(config, new StubEnvironment(), "1.2.3"));
        }
    }

    [Fact]
    public void The_flag_being_OFF_needs_no_named_proxy()
    {
        // The refusal is on the COMBINATION. The chart turns the flag on with its Ingress, which is the normal
        // production topology — refusing the flag itself would stop every such deployment from starting.
        var config = Config(new(CleanProduction) { ["App:TrustProxyHeaders"] = "false" });

        Assert.Empty(ProductionReadinessValidator.Validate(config, new StubEnvironment(), "1.2.3"));
    }

    [Fact]
    public void An_entry_that_cannot_be_read_is_named_and_does_not_count_as_naming_a_proxy()
    {
        // The case worth a test of its own: a typo parses to nothing, which is INDISTINGUISHABLE from having
        // named none — so without its own line an administrator fixing the typo would see the same refusal and
        // conclude the fix had not worked.
        var config = Config(new(CleanProduction)
        {
            ["App:TrustProxyHeaders"] = "true",
            ["App:KnownProxyNetworks"] = "10.0.0.0/999",
        });

        var violations = ProductionReadinessValidator.Validate(config, new StubEnvironment(), "1.2.3");

        Assert.Contains(violations, v => v.Contains("10.0.0.0/999", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("ANY peer", StringComparison.Ordinal));
    }

    [Fact]
    public void A_prefix_too_wide_for_its_family_is_rejected_rather_than_widening_the_trusted_set()
    {
        // /64 is a legitimate IPv6 prefix and a nonsense IPv4 one. Accepting it would trust far more than the
        // administrator wrote, which is the one parsing mistake here that fails OPEN.
        var config = Config(new(CleanProduction)
        {
            ["App:TrustProxyHeaders"] = "true",
            ["App:KnownProxyNetworks"] = "10.0.0.0/64",
        });

        Assert.Contains(
            ProductionReadinessValidator.Validate(config, new StubEnvironment(), "1.2.3"),
            v => v.Contains("10.0.0.0/64", StringComparison.Ordinal));
        Assert.Empty(ProxyTrust.KnownNetworks(config));
    }

    [Fact]
    public void Several_entries_are_read_and_the_valid_ones_survive_an_invalid_neighbour()
    {
        // A bad entry must not discard its neighbours: the deployment would then trust nothing it named while
        // the refusal above pointed only at the typo.
        var config = Config(new()
        {
            ["App:KnownProxyNetworks"] = "10.0.0.0/8, not-a-network, fd00::/8",
            ["App:KnownProxies"] = "10.1.2.3,::1",
        });

        Assert.Equal(2, ProxyTrust.KnownNetworks(config).Count());
        Assert.Equal(2, ProxyTrust.KnownProxies(config).Count());
        Assert.True(ProxyTrust.NamesAnyProxy(config));
    }

    // ---- Plaintext secrets at rest (#847, A02) -------------------------------------------------------

    [Fact]
    public void Production_refuses_to_run_without_a_transit_encryptor()
    {
        var config = Config(new(CleanProduction) { ["OpenBao:Address"] = null });

        var violations = ProductionReadinessValidator.Validate(config, new StubEnvironment(), "1.2.3");

        Assert.Contains(violations, v => v.Contains("PLAINTEXT", StringComparison.Ordinal));
    }

    [Fact]
    public void The_refusal_names_every_secret_kind_that_would_be_plaintext()
    {
        // NOT a formatting assertion. This stayed small in everybody's head as "the TOTP thing" — the comment
        // at NullTransitEncryptor still names only the MFA ADR — while the list grew underneath it to include
        // the tenant SMTP password, the audit webhook's signing secret, module settings and the mail ingest
        // key. An administrator reading this violation has to learn which secrets they have been storing in
        // the clear, so the message enumerating them IS the fix.
        var config = Config(new(CleanProduction) { ["OpenBao:Address"] = null });

        var violation = Assert.Single(
            ProductionReadinessValidator.Validate(config, new StubEnvironment(), "1.2.3"),
            v => v.Contains("PLAINTEXT", StringComparison.Ordinal));

        foreach (var kind in new[] { "TOTP", "SMTP", "webhook", "module settings", "ingest key" })
        {
            Assert.Contains(kind, violation, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Development_is_untouched_by_it()
    {
        // The demo stack, Compose and every test host run without OpenBao by design — the pass-through is what
        // makes those work at all. The gate returns early in Development, and this pins that it stays that way.
        var config = Config(new() { ["OpenBao:Address"] = null });

        Assert.Empty(ProductionReadinessValidator.Validate(
            config, new StubEnvironment { EnvironmentName = "Development" }));
    }
}
