using Microsoft.Extensions.Logging;
using SimplArchive.Cli.Infrastructure;
using Spectre.Console.Cli;

namespace SimplArchive.Cli.Commands;

/// <summary>
/// Clears the caller's registered certificate (#1353, ADR 0833).
/// </summary>
/// <remarks>
/// Included even though the decision was "register and inspect", because without it the pair is unusable: the
/// server refuses to replace a certificate that is already set, so a user whose card is reissued could
/// register the first one from here and would have to finish the job in a different client. A verb that can
/// only be performed once is not a verb.
/// </remarks>
public sealed class CertificateDeleteCommand(ILogger<CertificateDeleteCommand> logger) : AsyncCommand<TenantSessionSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, TenantSessionSettings settings, CancellationToken cancellationToken)
    {
        using var http = CertificateEndpoint.Client(settings);
        var api = new SimplArchiveApi(http);
        await api.DeleteAsync(await CertificateEndpoint.AddressAsync(api, cancellationToken), cancellationToken);

        logger.LogInformation(
            "Removed. Content addressed to this user is no longer enveloped. On an installation that serves content "
            + "only as an envelope, this user can no longer open documents until another certificate is registered.");

        return 0;
    }
}
