using System.Net.Http.Headers;
using System.Text.Json;
using SimplArchive.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SimplArchive.Cli.Commands;

/// <summary>
/// Reports the certificate the archive addresses this user's content to.
/// </summary>
/// <remarks>
/// The first verb somebody needs and the one they will run after every other, because the whole feature is
/// invisible otherwise: a registered certificate produces no banner, no badge and no change anywhere in the
/// clients — it simply decides whether content arrives readable. So "what is set?" has to be cheap and
/// unambiguous, in the same spirit as <c>whoami</c>.
/// </remarks>
public sealed class CertificateShowCommand(IAnsiConsole console) : AsyncCommand<UserSessionSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, UserSessionSettings settings, CancellationToken cancellationToken)
    {
        var status = await CertificateEndpoint.ReadAsync(settings, cancellationToken);

        var table = new Table().Border(TableBorder.None).HideHeaders();
        table.AddColumn(new TableColumn(string.Empty).PadRight(2));
        table.AddColumn(string.Empty);
        table.AddRow("Installation", Markup.Escape(settings.ResolvedUrl));

        if (!Flag(status, "selfService"))
        {
            // Stated FIRST and on its own, because on such an installation every other line would invite an
            // action that is going to be refused (ADR 0813): these certificates are provisioned from outside.
            table.AddRow("Certificate", "managed centrally");
            console.Write(table);
            console.MarkupLine(
                "[yellow]This installation provisions certificates for its users.[/] "
                + "Registering or deleting one here is refused; ask whoever administers it.");
            return 0;
        }

        if (!Flag(status, "enabled"))
        {
            table.AddRow("Certificate", "none registered");
            console.Write(table);
            console.MarkupLine(
                "Register one with: [blue]saconsole me certificate register <file>[/]  "
                + "(the public certificate — never a private key).");
            return 0;
        }

        table.AddRow("Subject", Markup.Escape(Text(status, "subject") ?? "(unnamed)"));
        table.AddRow("Expires", Markup.Escape(Text(status, "notAfter")?[..10] ?? "(unknown)"));
        console.Write(table);
        return 0;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.GetString() : null;

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}

/// <summary>Reaches the caller's certificate resource by FOLLOWING rels, never by composing a path.</summary>
/// <remarks>
/// <para>
/// Root → <c>me</c> → <c>smimeCertificate</c>. ADR 0543 binds this tool exactly as it binds the two clients:
/// rel names are the compatibility surface and paths are not, so a tool that hardcoded
/// <c>api/me/smime-certificate</c> would keep working right up to the day that route is renamed and then
/// answer 404 while the application was fine.
/// </para>
/// <para>
/// All three verbs act on ONE address, so it is resolved once per command and the method decides the action
/// (ADR 0719) — a <c>GET</c> reads it, a <c>PUT</c> sets it, a <c>DELETE</c> clears it.
/// </para>
/// </remarks>
internal static class CertificateEndpoint
{
    /// <summary>The rel the caller's own resource advertises for it.</summary>
    internal const string Rel = "smimeCertificate";

    internal static HttpClient Client(UserSessionSettings settings)
    {
        var http = new HttpClient { BaseAddress = new Uri(settings.ResolvedUrl.TrimEnd('/') + "/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.ResolvedToken);

        return http;
    }

    /// <summary>Resolves the address by following the rels from the API root.</summary>
    internal static async Task<string> AddressAsync(SimplArchiveApi api, CancellationToken cancellationToken)
    {
        var hypermedia = new Hypermedia(api);
        var me = await hypermedia.RootHrefAsync("me", cancellationToken);

        return Hypermedia.Href(
            await hypermedia.LinksOfAsync(me, cancellationToken), Rel, "This installation");
    }

    internal static async Task<JsonElement> ReadAsync(
        UserSessionSettings settings, CancellationToken cancellationToken)
    {
        using var http = Client(settings);
        var api = new SimplArchiveApi(http);

        return await api.GetAsync(await AddressAsync(api, cancellationToken), cancellationToken);
    }
}
