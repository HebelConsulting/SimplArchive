namespace SimplArchive.Api.Configuration;

// Fail-fast production hardening (ADR "Fail-fast production hardening"): refuses to start outside Development
// when a dev-grade setting is present. This check is WHY the product can be called production software rather
// than merely hoped to be one (ADR 0714): the dev-grade defaults exist for the demo stack, and this is what
// stops them reaching anywhere else — prose turned into enforced
// checks. Runs only when the environment isn't Development, so local dev + the (Development-hosted) test suites
// are unaffected. Every violation is a real misconfiguration — there is deliberately no bypass flag.
public static class ProductionReadinessValidator
{
    private const string DevBootstrapSecret = "dev-bootstrap-secret";
    private const string DevMinioCredential = "minioadmin";

    /// <param name="serverVersion">
    /// The running build's version; defaults to this assembly's. A PARAMETER rather than a direct read of
    /// <see cref="ServerBuildInfo.Version"/> so the check is testable: a test assembly is never stamped, so a
    /// static read would make every "clean production config" case fail for a reason the test is not about.
    /// </param>
    public static IReadOnlyList<string> Validate(IConfiguration configuration, IHostEnvironment environment, string? serverVersion = null)
    {
        var violations = new List<string>();
        if (environment.IsDevelopment())
        {
            return violations; // dev + tests: no checks
        }

        // OpenIddict would fall back to development certificates when the signing/encryption PEMs aren't sourced
        // (from OpenBao, ADR 0339, or provided directly). Dev certs are ephemeral and must never sign real tokens.
        if (string.IsNullOrWhiteSpace(configuration["OpenIddict:SigningCertificatePem"])
            || string.IsNullOrWhiteSpace(configuration["OpenIddict:EncryptionCertificatePem"]))
        {
            violations.Add("OpenIddict signing/encryption certificates are not configured — the app would fall back to development certificates. Source them from OpenBao (ADR 0339) or provide the PEMs.");
        }

        // An unstamped build cannot say which code is running. That makes the desktop's "are you behind this
        // deployment?" check meaningless (ADR 0512) and makes an incident report ambiguous about what was
        // deployed — the one question every post-mortem starts with. Same family as the dev-cert refusal below:
        // the deployment is missing something a production one must have (issue #425).
        if (string.Equals(serverVersion ?? ServerBuildInfo.Version, ServerBuildInfo.UnstampedVersion, StringComparison.Ordinal))
        {
            violations.Add($"This build is unstamped (serverVersion = {ServerBuildInfo.UnstampedVersion}) — build with -p:Version=<tag>, or pass --build-arg VERSION=<tag> to the Dockerfile, so the deployment can say which build it is.");
        }

        // Startup migration races across replicas; run migrations as a one-off step (the Helm chart defaults false).
        if (configuration.GetValue<bool>("App:ApplyMigrationsAtStartup"))
        {
            violations.Add("App:ApplyMigrationsAtStartup is true — run migrations as a one-off step instead (it races across replicas).");
        }

        // A plaintext IMAP port ships credentials unencrypted — dev only; production uses the implicit-TLS
        // port with a real certificate (ADR "IMAP endpoint (read-only, first slice)").
        if (configuration.GetValue<bool>("Imap:Enabled") && configuration.GetValue<int>("Imap:Port") != 0)
        {
            violations.Add("Imap:Port (plaintext IMAP) is configured — production must use Imap:TlsPort with a certificate only.");
        }

        // Demo-data seeding provisions a tenant admin with a known password — never in production.
        if (!string.IsNullOrWhiteSpace(configuration["Demo:Administrator:Password"]))
        {
            violations.Add("Demo-data seeding is configured (Demo:*) — remove it from a production deployment.");
        }

        // The known development bootstrap platform-admin secret.
        if (string.Equals(configuration["Bootstrap:PlatformAdministrator:ClientSecret"], DevBootstrapSecret, StringComparison.Ordinal))
        {
            violations.Add("Bootstrap:PlatformAdministrator:ClientSecret is the known development value — use a real secret (e.g. from OpenBao).");
        }

        // The known development MinIO credentials.
        if (string.Equals(configuration["ObjectStorage:AccessKey"], DevMinioCredential, StringComparison.Ordinal)
            || string.Equals(configuration["ObjectStorage:SecretKey"], DevMinioCredential, StringComparison.Ordinal))
        {
            violations.Add("ObjectStorage:AccessKey/SecretKey are the known development MinIO credentials — use real object-storage credentials.");
        }

        // The development Postgres password, unless a credential source (OpenBao) is composing the connection.
        var connectionString = configuration["ConnectionStrings:Default"] ?? "";
        if (string.IsNullOrWhiteSpace(configuration["OpenBao:Address"])
            && connectionString.Contains("Password=postgres", StringComparison.OrdinalIgnoreCase))
        {
            violations.Add("ConnectionStrings:Default uses the development Postgres password — use a real credential (or source it from OpenBao).");
        }

        return violations;
    }

    public static void ThrowIfNotProductionReady(IConfiguration configuration, IHostEnvironment environment, string? serverVersion = null)
    {
        var violations = Validate(configuration, environment, serverVersion);
        if (violations.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Refusing to start in the '{environment.EnvironmentName}' environment with development-grade settings:{Environment.NewLine}"
            + string.Join(Environment.NewLine, violations.Select(v => $"  - {v}"))
            + $"{Environment.NewLine}Fix these, or run locally with ASPNETCORE_ENVIRONMENT=Development. See docs/deploy/README.md.");
    }
}
