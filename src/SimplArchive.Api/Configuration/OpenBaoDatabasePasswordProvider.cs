using SimplArchive.Application.Abstractions;

namespace SimplArchive.Api.Configuration;

/// <summary>
/// Reads the current password of the runtime's fixed database login from OpenBao's database static role, each
/// time Npgsql asks for a refresh.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="OpenBaoSecretsReader"/>, which runs ONCE as a configuration provider at startup.
/// That is precisely the distinction this class exists to draw: configuration is read once, but a rotating
/// credential has to be re-read for as long as the process lives, and conflating the two is what left a
/// 24h-lease credential in a process that ran for days.
/// </para>
/// <para>
/// It logs in with AppRole per refresh rather than caching a token, because the AppRole token has its own TTL
/// (4h in the dev provisioning) — shorter than the process lifetime, so a cached one would expire and reproduce
/// this bug one level up.
/// </para>
/// </remarks>
public sealed class OpenBaoDatabasePasswordProvider : IDatabasePasswordProvider
{
    private readonly OpenBaoOptions _options;
    private readonly ILogger<OpenBaoDatabasePasswordProvider> _logger;

    public OpenBaoDatabasePasswordProvider(OpenBaoOptions options, ILogger<OpenBaoDatabasePasswordProvider> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async ValueTask<string> GetPasswordAsync(CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = new Uri(_options.Address.TrimEnd('/') + "/") };

        try
        {
            var reader = new OpenBaoSecretsReader(http, _options);
            var credential = await reader.ReadRuntimeDatabasePasswordAsync(cancellationToken)
                ?? throw new InvalidOperationException(
                    $"OpenBao returned no static credential for database role '{_options.DatabaseRuntimeStaticRole}'.");

            return credential;
        }
        catch (Exception e)
        {
            // Warning, not Error: Npgsql keeps the password it already holds and retries, so the app is still
            // serving — this is "an administrator should look", not "the request failed". Naming the switch is
            // part of the rule (ADR 0626): a warning that does not say which knob reveals more is half a
            // finding, and the exchange with OpenBao is only recoverable at Trace.
            _logger.LogWarning(e,
                "Could not refresh the database password from OpenBao at {Address} (static role {Role}). The "
                + "current password is still in use and the refresh will be retried; if it keeps failing, new "
                + "connections will be refused once the password rotates. Enable Trace on "
                + "SimplArchive.Api.Configuration to see the full exchange.",
                _options.Address, _options.DatabaseRuntimeStaticRole);
            throw;
        }
    }
}
