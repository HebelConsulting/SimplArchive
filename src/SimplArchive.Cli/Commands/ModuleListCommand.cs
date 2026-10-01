using SimplArchive.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SimplArchive.Cli.Commands;

/// <summary>
/// Names the modules this installation has, and whether each is active for this tenant (#1504).
/// </summary>
/// <remarks>
/// <b>It was already promised and did not exist.</b> <c>module rebuild</c>'s own refusal says
/// <i>"List them: saconsole module list"</i> — a command that was never registered, so an operator who
/// mistyped a module id was sent to a second error. Every other command here needs a module id too, and
/// nothing told anybody what the ids are.
/// </remarks>
public sealed class ModuleListCommand(IAnsiConsole console) : AsyncCommand<UserSessionSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, UserSessionSettings settings, CancellationToken cancellationToken)
    {
        using var http = CertificateEndpoint.Client(settings);
        var api = new SimplArchiveApi(http);

        var listing = await ModuleSurface.ListingAsync(api, cancellationToken);
        var rows = ModuleSurface.Rows(listing).ToList();

        if (rows.Count == 0)
        {
            console.MarkupLine("[grey]This installation has no modules installed.[/]");
            return 0;
        }

        var table = new Table().Border(TableBorder.None);
        table.AddColumn("id");
        table.AddColumn("name");
        table.AddColumn("state");
        table.AddColumn("configurable");

        foreach (var row in rows)
        {
            var id = row.TryGetProperty("moduleId", out var m) ? m.GetString() ?? string.Empty : string.Empty;
            var name = row.TryGetProperty("displayName", out var d) ? d.GetString() ?? string.Empty : string.Empty;
            var active = row.TryGetProperty("isActive", out var a)
                && a.ValueKind == System.Text.Json.JsonValueKind.True;

            // CONFIGURABLE IS READ OFF THE RELS, not guessed from the module id: the `settings` rel is
            // advertised exactly where `module settings` will work, so this column cannot promise something
            // the next command then refuses.
            var configurable = Hypermedia.LinksOf(row).ContainsKey("settings");

            table.AddRow(
                Markup.Escape(id),
                Markup.Escape(name),
                active ? "[green]active[/]" : "[grey]not active[/]",
                configurable ? "yes" : "[grey]—[/]");
        }

        console.Write(table);
        return 0;
    }
}
