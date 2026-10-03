using System.ComponentModel;
using System.Text.Json;
using SimplArchive.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SimplArchive.Cli.Commands;

public sealed class ModuleRebuildSettings : TenantSessionSettings
{
    [CommandArgument(0, "<module>")]
    [Description("The module whose projection to rebuild, by id — e.g. encryption.")]
    public required string Module { get; init; }

    [CommandArgument(1, "[projection]")]
    [Description("Which projection. Omitted rebuilds every projection the module declares.")]
    public string? Projection { get; init; }
}

/// <summary>
/// Rebuilds a module's read model from the documents it derives from (#1504).
/// </summary>
/// <remarks>
/// <para>
/// The operator guarantee ADR 0738 rests on, as a command: a read model is never the only copy of anything,
/// so it can always be re-derived. It is the first answer to a support case where a module's answers look
/// stale — and until #1507 it had no address a conforming client could reach, so it could only be run with a
/// hand-written <c>POST</c>.
/// </para>
/// <para>
/// <b>The projection name is optional, and that is the point of the listing.</b> A caller should not have to
/// know that the encryption module's projection is called <c>reader-certificates</c>; the module says what it
/// has, and this rebuilds one or all of them.
/// </para>
/// </remarks>
public sealed class ModuleRebuildCommand(IAnsiConsole console) : AsyncCommand<ModuleRebuildSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, ModuleRebuildSettings settings, CancellationToken cancellationToken)
    {
        using var http = CertificateEndpoint.Client(settings);
        var api = new SimplArchiveApi(http);
        var hypermedia = new Hypermedia(api);

        // root → tenant settings → modules → this module's row → its projections. Every hop a rel.
        var tenantSettings = await hypermedia.RootHrefAsync("tenantSettings", cancellationToken);
        var modules = Hypermedia.Href(
            await hypermedia.LinksOfAsync(tenantSettings, cancellationToken), "modules", "The tenant settings");

        var row = Row(await api.GetAsync(modules, cancellationToken), settings.Module)
            ?? throw new CliException(
                $"This installation has no module '{settings.Module}'. List them: saconsole module list");

        // A MISSING REL IS THE ANSWER (ADR 0543): projections are advertised only where the module is active,
        // which is the same gate the rebuild itself applies. Saying "not active" beats letting the POST 404.
        var projections = Hypermedia.LinksOf(row).TryGetValue("projections", out var href)
            ? href
            : throw new CliException(
                $"'{settings.Module}' is installed but not active for this tenant, so nothing can be rebuilt. "
                + "File its licence first: saconsole module activate.");

        var available = Projections(await api.GetAsync(projections, cancellationToken)).ToList();
        if (available.Count == 0)
        {
            console.MarkupLine($"'{Markup.Escape(settings.Module)}' declares no projections to rebuild.");
            return 0;
        }

        var chosen = settings.Projection is { Length: > 0 } name
            ? available.Where(p => string.Equals(p.Name, name, StringComparison.Ordinal)).ToList()
            : available;

        if (chosen.Count == 0)
        {
            throw new CliException(
                $"'{settings.Module}' has no projection '{settings.Projection}'. It declares: "
                + string.Join(", ", available.Select(p => p.Name)));
        }

        foreach (var projection in chosen)
        {
            await api.PostAsync(projection.RebuildHref, new { }, cancellationToken);
            console.MarkupLine($"[green]Rebuilt[/] {Markup.Escape(settings.Module)} / {Markup.Escape(projection.Name)}");
        }

        return 0;
    }

    private static JsonElement? Row(JsonElement listing, string moduleId)
    {
        // `items`, as the modules listing spells it — read off the resource rather than guessed.
        if (!listing.TryGetProperty("items", out var rows))
        {
            return null;
        }

        foreach (var row in rows.EnumerateArray())
        {
            if (row.TryGetProperty("moduleId", out var id)
                && string.Equals(id.GetString(), moduleId, StringComparison.Ordinal))
            {
                return row;
            }
        }

        return null;
    }

    private static IEnumerable<(string Name, string RebuildHref)> Projections(JsonElement resource)
    {
        if (!resource.TryGetProperty("projections", out var rows))
        {
            yield break;
        }

        foreach (var row in rows.EnumerateArray())
        {
            if (row.TryGetProperty("name", out var name)
                && Hypermedia.LinksOf(row).TryGetValue("rebuild", out var href))
            {
                yield return (name.GetString() ?? string.Empty, href);
            }
        }
    }
}
