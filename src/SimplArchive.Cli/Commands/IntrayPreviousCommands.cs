using System.Text.Json;
using SimplArchive.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SimplArchive.Cli.Commands;

/// <summary>Reaching the bytes an intray overwrite set aside, by rels (#799).</summary>
/// <remarks>
/// root → <c>intray</c> → <c>previous</c>, then a row's own <c>restore</c>. Every hop a rel and no composed
/// address: <c>saconsole</c> is a client like the other two (ADR 0543), and a CLI is where composing breaks in
/// exactly the way the rule exists to prevent — rename a route and the application keeps working while the
/// tool starts answering 404.
/// </remarks>
internal static class IntrayPreviousSurface
{
    internal static async Task<JsonElement> ListingAsync(SimplArchiveApi api, CancellationToken cancellationToken)
    {
        var hypermedia = new Hypermedia(api);
        var intray = await hypermedia.RootHrefAsync("intray", cancellationToken);
        var previous = Hypermedia.Href(
            await hypermedia.LinksOfAsync(intray, cancellationToken), "previous", "The intray");

        return await api.GetAsync(previous, cancellationToken);
    }

    internal static IEnumerable<JsonElement> Rows(JsonElement listing) =>
        listing.TryGetProperty("items", out var rows) ? rows.EnumerateArray() : [];
}

/// <summary>
/// Lists what an intray overwrite set aside and how long is left to recover it (#799).
/// </summary>
public sealed class IntrayPreviousListCommand(IAnsiConsole console) : AsyncCommand<TenantSessionSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, TenantSessionSettings settings, CancellationToken cancellationToken)
    {
        using var http = CertificateEndpoint.Client(settings);
        var api = new SimplArchiveApi(http);

        var rows = IntrayPreviousSurface.Rows(
            await IntrayPreviousSurface.ListingAsync(api, cancellationToken)).ToList();

        if (rows.Count == 0)
        {
            // Not an error, and worth saying plainly: an empty list is the healthy state, and somebody running
            // this after losing a file needs to know the difference between "nothing was kept" and "the
            // command did not work".
            console.MarkupLine("[grey]Nothing has been set aside for this user — no intray item has been overwritten.[/]");
            return 0;
        }

        var table = new Table().Border(TableBorder.None);
        table.AddColumn("name");
        table.AddColumn("size");
        table.AddColumn("set aside");
        table.AddColumn("recoverable until");

        foreach (var row in rows)
        {
            table.AddRow(
                Markup.Escape(Text(row, "name")),
                Text(row, "size"),
                Text(row, "preservedAt"),
                Text(row, "expiresAt"));
        }

        console.Write(table);
        console.MarkupLine($"[grey]Restore one with:[/] saconsole intray previous restore <name>");
        return 0;
    }

    private static string Text(JsonElement row, string property) =>
        row.TryGetProperty(property, out var value)
            ? value.ValueKind == JsonValueKind.Number ? value.ToString() : value.GetString() ?? string.Empty
            : string.Empty;
}

/// <summary>Puts a set-aside copy back into the intray (#799).</summary>
public sealed class IntrayPreviousRestoreCommand(IAnsiConsole console) : AsyncCommand<IntrayPreviousRestoreCommand.Settings>
{
    public sealed class Settings : TenantSessionSettings
    {
        [CommandArgument(0, "<name>")]
        public string Name { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        using var http = CertificateEndpoint.Client(settings);
        var api = new SimplArchiveApi(http);

        var listing = await IntrayPreviousSurface.ListingAsync(api, cancellationToken);
        var row = IntrayPreviousSurface.Rows(listing)
            .FirstOrDefault(r => r.TryGetProperty("name", out var n)
                && string.Equals(n.GetString(), settings.Name, StringComparison.Ordinal));

        if (row.ValueKind != JsonValueKind.Object)
        {
            // Names the alternatives rather than just refusing: whoever runs this has already lost a file once,
            // and a bare "not found" leaves them guessing whether the net caught anything at all.
            var names = IntrayPreviousSurface.Rows(listing)
                .Select(r => r.TryGetProperty("name", out var n) ? n.GetString() : null)
                .Where(n => n is { Length: > 0 })
                .ToList();

            throw new CliException(
                $"Nothing was set aside under the name '{settings.Name}'. "
                + (names.Count == 0
                    ? "Nothing has been set aside for this user at all."
                    : $"Recoverable right now: {string.Join(", ", names)}."));
        }

        // The row's OWN address, which is how a conforming client acts (ADR 0559 applied to a CLI: the thing
        // you listed carries the way to act on it).
        var restore = Hypermedia.Href(Hypermedia.LinksOf(row), "restore", $"The preserved item '{settings.Name}'");
        await api.PostAsync(restore, new { }, cancellationToken);

        console.MarkupLine($"[green]Restored[/] {Markup.Escape(settings.Name)} [grey]into the intray.[/]");
        console.MarkupLine("[grey]Whatever was there has itself been set aside, so this is reversible.[/]");
        return 0;
    }
}
