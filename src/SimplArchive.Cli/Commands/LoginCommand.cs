using System.ComponentModel;
using System.Net.Http.Headers;
using SimplArchive.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SimplArchive.Cli.Commands;

/// <summary>
/// Signs a USER in through the device authorization grant (ADR 0823) and prints the session as shell exports.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing is written to disk.</b> The session lives in the environment and nowhere else, so there is no
/// credential file to find on a host, no stale token to revoke, and no second place for "which installation
/// am I pointed at?" to be answered differently from the shell the administrator is typing into. The cost is
/// that a new shell means a new approval, which is the honest consequence of not persisting anything.
/// </para>
/// <para>
/// <b>The stdout/stderr split is what makes this usable</b> and is not a stylistic choice: the exports go to
/// STDOUT and everything a person reads — the code, the URL, the progress, the confirmation — goes to STDERR.
/// That is what lets <c>eval "$(saconsole login --url …)"</c> work while the administrator still sees the
/// code they have to approve. Printing the prompt to stdout would have `eval` try to execute it.
/// </para>
/// <para>
/// The token is deliberately NOT echoed in the human output. It is on stdout because that is where the shell
/// must read it from; repeating it in the readable half would put an access token in a terminal scrollback
/// and, over SSH, in somebody's session log.
/// </para>
/// </remarks>
public sealed class LoginCommand(IAnsiConsole console) : AsyncCommand<LoginCommand.Settings>
{
    public sealed class Settings : ApiSettings
    {
        [CommandOption("--no-export")]
        [Description("Print only the confirmation, not the shell exports (for a human, not for eval).")]
        public bool NoExport { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        // Everything readable goes to stderr; see the remarks. `console` is stdout and carries the exports.
        var human = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });

        using var http = new HttpClient { BaseAddress = new Uri(settings.ResolvedUrl.TrimEnd('/') + "/") };
        var flow = new DeviceFlow(http);

        var authorization = await flow.RequestAsync(cancellationToken);

        human.WriteLine();
        human.MarkupLine($"  Code            [bold]{Markup.Escape(authorization.UserCode)}[/]");
        human.MarkupLine($"  Approve it at   [blue]{Markup.Escape(authorization.VerificationUriComplete ?? authorization.VerificationUri)}[/]");
        if (authorization.VerificationUriComplete is not null)
        {
            // Both are shown when they differ: the complete URI is the convenient one, and the bare one is
            // what to type on a phone that cannot follow a link from a terminal.
            human.MarkupLine($"  or enter it at  [blue]{Markup.Escape(authorization.VerificationUri)}[/]");
        }

        human.WriteLine();
        human.MarkupLine("  [dim]Check the code on that page matches the one above before approving.[/]");
        human.WriteLine();

        var token = await human.Status()
            .StartAsync("Waiting for approval…", async _ =>
                await flow.PollAsync(authorization, _ => { }, cancellationToken));

        // Prove the token works AND name who it belongs to, before announcing success. A login that reports
        // success and then fails on the first real command has told the administrator the wrong thing about
        // where the problem is.
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var me = await new SimplArchiveApi(http).GetAsync("api/diagnostics/whoami", cancellationToken);
        var who = me.TryGetProperty("userName", out var name) ? name.GetString() : null;
        var tenant = me.TryGetProperty("tenantName", out var t) ? t.GetString() : null;

        human.MarkupLine($"  [green]Signed in[/] as {Markup.Escape(who ?? "(unknown)")}"
            + (tenant is null ? string.Empty : $" in {Markup.Escape(tenant)}"));

        if (settings.NoExport)
        {
            human.MarkupLine("  [dim]No exports printed (--no-export). This session is not usable by other commands.[/]");
            return 0;
        }

        human.MarkupLine("  [dim]Run this, or wrap the command in: eval \"$(saconsole login --url …)\"[/]");
        human.WriteLine();

        // STDOUT, and only this. Single-quoted so a URL with shell metacharacters cannot be re-interpreted;
        // neither value can contain a single quote (one is a bearer token, the other a URL we just used).
        console.WriteLine($"export {ApiSettings.TokenVariable}='{token}'");
        console.WriteLine($"export {ApiSettings.UrlVariable}='{settings.ResolvedUrl.TrimEnd('/')}'");
        return 0;
    }
}
