using SimplArchive.Cli.Commands;
using SimplArchive.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

// saconsole — administrative CLI for a SimplArchive installation (ADR 0822). An API client and nothing else:
// it does not write configuration, seed secrets or start containers, and everything it does is an act some
// principal could also perform through a client.
namespace SimplArchive.Cli;

/// <summary>
/// Builds the configured <c>saconsole</c> application.
/// </summary>
/// <remarks>
/// Its own type rather than top-level statements so the wiring can be EXERCISED. The property that needed a
/// test is not which verbs exist but where errors are written: `login` prints shell exports to stdout for
/// `eval "$(saconsole login …)"`, so anything else on stdout is something the shell will execute. An
/// unreachable installation once put `Unexpected SocketException: Connection refused` there.
/// </remarks>
public static class SaConsoleApp
{
    public static CommandApp Build()
    {
        var app = new CommandApp();
        app.Configure(config =>
        {
            config.SetApplicationName("saconsole");

            // EVERY error goes to STDERR, and that is load-bearing rather than tidy. `saconsole login` prints shell
            // exports to stdout so it can be used as `eval "$(saconsole login --url …)"` — so an error written to
            // stdout is an error the shell EXECUTES. Measured before this was fixed: an unreachable installation put
            // `Unexpected SocketException: Connection refused` on stdout, where eval would have run it.
            var errors = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });

            // Four kinds of failure, told apart because calling them all the same thing is how a tool starts lying
            // about what went wrong.
            //
            //   CliException            — something the installation said; the message is written for the reader.
            //   CommandRuntimeException — Spectre's own parse/validation failure, i.e. the command line was wrong.
            //                             Found by running the PACKAGED tool: a forgotten --url printed
            //                             "Unexpected CommandRuntimeException", which tells someone they hit a bug
            //                             when they merely mistyped.
            //   HttpRequestException    — the installation could not be REACHED. A wrong URL, a stopped server or a
            //                             firewall is the most ordinary thing that can happen to a tool whose whole
            //                             job is to call one, and reporting it as an unexpected SocketException sends
            //                             the reader looking for a bug in the binary.
            //   anything else           — a real bug, and it keeps its type rather than hiding behind a friendly
            //                             sentence.
            config.SetExceptionHandler((exception, _) =>
            {
                switch (exception.GetBaseException())
                {
                    case CliException expected:
                        errors.MarkupLine($"[red]{Markup.Escape(expected.Message)}[/]");
                        return 1;

                    case CommandRuntimeException usage:
                        errors.MarkupLine($"[red]{Markup.Escape(usage.Message)}[/]");
                        errors.MarkupLine("Run with [blue]--help[/] to see the options.");
                        return 1;

                    // The base exception of a refused connection is SocketException, so catching HttpRequestException
                    // alone would miss it — which is exactly how it reached the "real bug" branch.
                    case HttpRequestException or System.Net.Sockets.SocketException:
                        errors.MarkupLine("[red]Could not reach the installation.[/]");
                        errors.MarkupLine("Check the URL, that the installation is running, and that this host can reach it.");
                        return 1;

                    case var bug:
                        errors.MarkupLine($"[red]Unexpected {bug.GetType().Name}:[/] {Markup.Escape(bug.Message)}");
                        return 2;
                }
            });

            // Sign-in and "where am I?" sit at the top level rather than in a branch: they are what somebody reaches
            // for first, and burying them under a noun would make the tool's entry point the least discoverable part
            // of it.
            config.AddCommand<LoginCommand>("login")
                .WithDescription("Sign in through the device grant and print the session as shell exports.");
            config.AddCommand<WhoAmICommand>("whoami")
                .WithDescription("Name the installation and identity the current session acts as.");

            // The caller's OWN certificate (#1353, ADR 0833) — the self-service resource, under `me` rather
            // than a bare `certificate`, because the noun has to say WHOSE. An administrator registering on
            // somebody else's behalf is a different resource and a different right, and naming this one `me`
            // leaves room for that instead of having to rename this when it arrives.
            config.AddBranch("me", me =>
            {
                me.SetDescription("The signed-in user's own settings.");
                me.AddBranch("certificate", certificate =>
                {
                    certificate.SetDescription("The certificate this user's content is enveloped to.");
                    certificate.AddCommand<CertificateShowCommand>("show")
                        .WithDescription("Report the registered certificate, or that there is none.");
                    certificate.AddCommand<CertificateRegisterCommand>("register")
                        .WithDescription("Register a certificate from a file (the public certificate only).");
                    certificate.AddCommand<CertificateDeleteCommand>("delete")
                        .WithDescription("Remove the registered certificate.");
                });
            });

            config.AddBranch("tenant", tenant =>
            {
                tenant.SetDescription("Installation-level tenant administration (platform administrator).");
                tenant.AddCommand<TenantCreateCommand>("create")
                    .WithDescription("Provision a tenant with its first administrator and repository.");
            });
        });

        return app;
    }
}
