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
    };

    [Fact]
    public void Development_skips_all_checks_even_with_dev_grade_settings()
    {
        var config = Config(new()
        {
            ["App:ApplyMigrationsAtStartup"] = "true",
            ["ObjectStorage:AccessKey"] = "minioadmin",
            ["Bootstrap:PlatformAdministrator:ClientSecret"] = "dev-bootstrap-secret",
            ["Demo:Administrator:Password"] = "demo1234",
        });

        Assert.Empty(ProductionReadinessValidator.Validate(config, new StubEnvironment { EnvironmentName = "Development" }));
    }

    [Fact]
    public void Production_with_a_clean_config_passes()
    {
        Assert.Empty(ProductionReadinessValidator.Validate(Config(CleanProduction), new StubEnvironment()));
        ProductionReadinessValidator.ThrowIfNotProductionReady(Config(CleanProduction), new StubEnvironment()); // no throw
    }

    [Fact]
    public void Production_with_dev_grade_settings_reports_every_violation()
    {
        var config = Config(new()
        {
            // No OpenIddict cert PEMs → dev-cert fallback.
            ["App:ApplyMigrationsAtStartup"] = "true",
            ["Demo:Administrator:Password"] = "demo1234",
            ["Bootstrap:PlatformAdministrator:ClientSecret"] = "dev-bootstrap-secret",
            ["ObjectStorage:AccessKey"] = "minioadmin",
            ["ObjectStorage:SecretKey"] = "minioadmin",
            ["ConnectionStrings:Default"] = "Host=db;Port=5432;Database=simplarchive;Username=postgres;Password=postgres",
            // OpenBao:Address unset → the dev Postgres password check applies.
        });

        var violations = ProductionReadinessValidator.Validate(config, new StubEnvironment());

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

        Assert.DoesNotContain(ProductionReadinessValidator.Validate(config, new StubEnvironment()), v => v.Contains("Postgres password"));
    }
}
