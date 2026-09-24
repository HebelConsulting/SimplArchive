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

    // A CliException carries a message written for the person running the command; anything else is a bug and
    // keeps its type, because hiding an unexpected exception behind a friendly sentence is how a tool starts
    // lying about what went wrong.
    config.SetExceptionHandler((exception, _) =>
    {
        if (exception.GetBaseException() is CliException expected)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(expected.Message)}[/]");
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
