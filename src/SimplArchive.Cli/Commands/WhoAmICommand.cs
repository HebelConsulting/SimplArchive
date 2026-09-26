using System.Net.Http.Headers;
using SimplArchive.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SimplArchive.Cli.Commands;

/// <summary>
/// Names the installation and the identity the current session acts as.
/// </summary>
/// <remarks>
/// This is not a convenience. The moment a tool carries a "current installation" rather than naming one per
/// command, it acquires the failure where somebody runs the right command against the wrong system — and the
/// only defence is that the answer to "which one am I pointed at?" is cheap, obvious and always available.
/// So it ships WITH the environment fallback rather than after it.
/// </remarks>
public sealed class WhoAmICommand(IAnsiConsole console) : AsyncCommand<UserSessionSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, UserSessionSettings settings, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = new Uri(settings.ResolvedUrl.TrimEnd('/') + "/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.ResolvedToken);

        // FOLLOWED, not composed (ADR 0543): the root advertises `whoami`, so the tool does not need to know
        // that it lives under /api/diagnostics — and will not break when it stops doing so.
        var api = new SimplArchiveApi(http);
        var me = await api.GetAsync(
            await new Hypermedia(api).RootHrefAsync("whoami", cancellationToken), cancellationToken);

        string? Text(string name) => me.TryGetProperty(name, out var v) ? v.GetString() : null;
        bool Flag(string name) => me.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.True;

        var table = new Table().Border(TableBorder.None).HideHeaders();
        table.AddColumn(new TableColumn(string.Empty).PadRight(2));
        table.AddColumn(string.Empty);
        table.AddRow("Installation", Markup.Escape(settings.ResolvedUrl));
        table.AddRow("Signed in as", Markup.Escape(Text("userName") ?? "(unknown)"));
        table.AddRow("Tenant", Markup.Escape(Text("tenantName") ?? "(none)"));

        // Named rather than dumped: these are the two that decide whether an administrative verb will be
        // refused, and reading them here is cheaper than meeting a 403 halfway through one.
        table.AddRow("Tenant admin", Flag("isTenantAdmin") ? "yes" : "no");
        table.AddRow("Can manage users", Flag("canManageUsers") ? "yes" : "no");

        console.Write(table);
        return 0;
    }
}
