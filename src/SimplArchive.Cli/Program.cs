using SimplArchive.Cli.Commands;
using SimplArchive.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

// saconsole — administrative CLI for a SimplArchive installation (ADR 0822). An API client and nothing else:
// it does not write configuration, seed secrets or start containers, and everything it does is an act some
// principal could also perform through a client.
var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("saconsole");

    // Three kinds of failure, told apart because calling them all the same thing is how a tool starts lying
    // about what went wrong.
    //
    //   CliException            — something the installation said; the message is written for the reader.
    //   CommandRuntimeException — Spectre's own parse/validation failure, i.e. the command line was wrong.
    //                             Found by running the PACKAGED tool: a forgotten --url printed
    //                             "Unexpected CommandRuntimeException", which tells someone they hit a bug
    //                             when they merely mistyped.
    //   anything else           — a real bug, and it keeps its type rather than hiding behind a friendly
    //                             sentence.
    config.SetExceptionHandler((exception, _) =>
    {
        if (exception.GetBaseException() is CliException expected)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(expected.Message)}[/]");
            return 1;
        }

        if (exception.GetBaseException() is CommandRuntimeException usage)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(usage.Message)}[/]");
            AnsiConsole.MarkupLine("Run with [blue]--help[/] to see the options.");
            return 1;
        }

        AnsiConsole.MarkupLine($"[red]Unexpected {exception.GetBaseException().GetType().Name}:[/] " +
            $"{Markup.Escape(exception.GetBaseException().Message)}");
        return 2;
    });

    config.AddBranch("tenant", tenant =>
    {
        tenant.SetDescription("Installation-level tenant administration (platform administrator).");
        tenant.AddCommand<TenantCreateCommand>("create")
            .WithDescription("Provision a tenant with its first administrator and repository.");
    });
});

return app.Run(args);
