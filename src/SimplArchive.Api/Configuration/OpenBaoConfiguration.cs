namespace SimplArchive.Api.Configuration;

// A configuration provider that sources the app's secrets from OpenBao at startup (ADR "Secrets management with
// OpenBao"). Added early in the config pipeline so AddInfrastructure/AddAuthServer read the OpenBao-provided
// values (ConnectionStrings:Default from a dynamic Postgres credential, ObjectStorage/Smtp/Bootstrap secrets
// from KV). Fail-closed: if OpenBao is configured but unreachable/misprovisioned, startup throws rather than
// silently falling back to dev defaults.
public sealed class OpenBaoConfigurationProvider : ConfigurationProvider
{
    private readonly OpenBaoOptions _options;

    public OpenBaoConfigurationProvider(OpenBaoOptions options) => _options = options;

    public override void Load()
    {
        using var http = new HttpClient { BaseAddress = new Uri(_options.Address.TrimEnd('/') + "/") };
        var reader = new OpenBaoSecretsReader(http, _options);
        try
        {
            Data = reader.ReadAsync().GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            throw new InvalidOperationException(
                $"Failed to read secrets from OpenBao at '{_options.Address}'. Check the OpenBao service, AppRole credentials, and provisioning.", e);
        }
    }
}

public sealed class OpenBaoConfigurationSource : IConfigurationSource
{
    private readonly OpenBaoOptions _options;

    public OpenBaoConfigurationSource(OpenBaoOptions options) => _options = options;

    public IConfigurationProvider Build(IConfigurationBuilder builder) => new OpenBaoConfigurationProvider(_options);
}

public static class OpenBaoConfigurationExtensions
{
    // Adds the OpenBao secrets provider, reading its own coordinates (address + AppRole ids + DB template) from
    // the already-loaded configuration. A no-op when OpenBao:Address is empty, so tests and non-OpenBao
    // deployments are unaffected.
    public static IConfigurationBuilder AddOpenBaoSecrets(this IConfigurationManager configuration)
    {
        var options = new OpenBaoOptions();
        configuration.GetSection(OpenBaoOptions.SectionName).Bind(options);
        if (string.IsNullOrWhiteSpace(options.Address))
        {
            return configuration;
        }

        ((IConfigurationBuilder)configuration).Add(new OpenBaoConfigurationSource(options));
        return configuration;
    }
}
