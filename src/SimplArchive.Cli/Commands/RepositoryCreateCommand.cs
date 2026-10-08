using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SimplArchive.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SimplArchive.Cli.Commands;

public sealed class RepositoryCreateSettings : TenantSessionSettings
{
    [CommandOption("--name <NAME>")]
    [Description("The repository's name. An existing repository of exactly this name is reused, not duplicated.")]
    public required string Name { get; init; }
}

/// <summary>
/// Creates a repository (a root document, ADR 0200), or finds the one already there — and prints its id on stdout.
/// </summary>
/// <remarks>
/// <para>
/// <b>Find-or-create, so a script can run twice.</b> A bootstrap that dies half-way is run again, and "create"
/// answering a name conflict on the second run would turn every retry into a manual cleanup (the trap
/// <see cref="LicenceFiling"/> already fell into, #1613). The match is exact and ordinal: a near miss is a
/// different repository, and guessing would file into the wrong one.
/// </para>
/// <para>
/// <b>The id alone goes to stdout</b>, so <c>id=$(saconsole repository create --name …)</c> works; what a person
/// reads goes to stderr, the split <c>login</c> established.
/// </para>
/// <para>
/// Reached through the root's <c>repositories</c> rel, which serves the listing and the create on one address
/// (ADR 0719). The listing is paged, and every page is read before deciding a name is free.
/// </para>
/// </remarks>
public sealed class RepositoryCreateCommand(ILogger<RepositoryCreateCommand> logger) : AsyncCommand<RepositoryCreateSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, RepositoryCreateSettings settings, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = new Uri(settings.ResolvedUrl.TrimEnd('/') + "/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.ResolvedToken);
        var api = new SimplArchiveApi(http);
        var (id, created) = await FindOrCreateAsync(api, settings.Name, cancellationToken);
        if (created)
        {
            logger.LogInformation("Created repository {Name}.", settings.Name);
        }
        else
        {
            logger.LogInformation("Repository {Name} already exists.", settings.Name);
        }

        // Data output (ADR 0906): the id alone on stdout, for `id=$(saconsole repository create …)`.
        Console.Out.WriteLine(id.ToString("D"));
        return 0;
    }

    public static async Task<(Guid Id, bool Created)> FindOrCreateAsync(
        SimplArchiveApi api, string name, CancellationToken cancellationToken)
    {
        var repositoriesHref = await new Hypermedia(api).RootHrefAsync("repositories", cancellationToken);
        if (await FindAsync(api, repositoriesHref, name, cancellationToken) is { } existing)
        {
            return (existing, false);
        }

        var created = await api.PostAsync(repositoriesHref, new { name }, cancellationToken);
        return (created.GetProperty("id").GetGuid(), true);
    }

    /// <summary>The visible repository named exactly <paramref name="name"/>, following <c>next</c> to the end.</summary>
    internal static async Task<Guid?> FindAsync(
        SimplArchiveApi api, string repositoriesHref, string name, CancellationToken cancellationToken)
    {
        for (string? href = repositoriesHref; href is not null;)
        {
            var page = await api.GetAsync(href, cancellationToken);
            if (page.TryGetProperty("repositories", out var rows) && rows.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in rows.EnumerateArray())
                {
                    if (row.GetProperty("name").GetString() == name)
                    {
                        return row.GetProperty("id").GetGuid();
                    }
                }
            }

            href = Hypermedia.LinksOf(page).TryGetValue("next", out var next) ? next : null;
        }

        return null;
    }
}
