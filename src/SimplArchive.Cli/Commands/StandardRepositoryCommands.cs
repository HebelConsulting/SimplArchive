using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SimplArchive.Cli.Infrastructure;
using Spectre.Console.Cli;

namespace SimplArchive.Cli.Commands;

/// <summary>
/// Shows the tenant's standard repository (ADR 0892) — where SimplArchive files what it brings, its manuals first.
/// </summary>
/// <remarks>Followed from the root's <c>tenantSettings</c> rel, which a tenant administrator holds.</remarks>
public sealed class StandardRepositoryShowCommand(ILogger<StandardRepositoryShowCommand> logger) : AsyncCommand<TenantSessionSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, TenantSessionSettings settings, CancellationToken cancellationToken)
    {
        using var http = CertificateEndpoint.Client(settings);
        var api = new SimplArchiveApi(http);
        var (resource, _) = await StandardRepositorySurface.ReadAsync(api, cancellationToken);

        if (StandardRepositorySurface.Current(resource) is { } id)
        {
            // Data output (ADR 0906): the id alone on stdout, for `id=$(saconsole tenant standard-repository show)`.
            Console.Out.WriteLine(id);
        }
        else
        {
            logger.LogInformation("No standard repository is set; nothing is filed until one is chosen.");
        }

        return 0;
    }
}

public sealed class StandardRepositorySetSettings : TenantSessionSettings
{
    [CommandArgument(0, "<repository-id>")]
    [Description("The repository to make standard: a shared, top-level folder of this tenant.")]
    public required Guid Repository { get; init; }
}

/// <summary>
/// Makes another repository the tenant's standard one (ADR 0892); the server moves the manuals folder with it.
/// </summary>
/// <remarks>
/// Reads the settings resource first for its ETag and its <c>settings-standard-repository</c> rel, then writes with
/// that ETag as <c>If-Match</c> — a colleague's change in between is refused rather than overwritten.
/// </remarks>
public sealed class StandardRepositorySetCommand(ILogger<StandardRepositorySetCommand> logger) : AsyncCommand<StandardRepositorySetSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, StandardRepositorySetSettings settings, CancellationToken cancellationToken)
    {
        using var http = CertificateEndpoint.Client(settings);
        var api = new SimplArchiveApi(http);
        var (resource, etag) = await StandardRepositorySurface.ReadAsync(api, cancellationToken);

        var target = Hypermedia.Href(Hypermedia.LinksOf(resource), StandardRepositorySurface.Rel, "The tenant settings");
        var written = await api.PutWithETagAsync(target, new { standardRepositoryId = settings.Repository }, etag, cancellationToken);

        logger.LogInformation("Standard repository is now {Repository}.", StandardRepositorySurface.Current(written));
        return 0;
    }
}

internal static class StandardRepositorySurface
{
    internal const string Rel = "settings-standard-repository";

    internal static async Task<(JsonElement Resource, string? ETag)> ReadAsync(SimplArchiveApi api, CancellationToken cancellationToken) =>
        await api.GetWithETagAsync(await new Hypermedia(api).RootHrefAsync("tenantSettings", cancellationToken), cancellationToken);

    internal static Guid? Current(JsonElement resource) =>
        resource.TryGetProperty("standardRepositoryId", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetGuid()
            : null;
}
