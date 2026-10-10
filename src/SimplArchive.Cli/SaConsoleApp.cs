using SimplArchive.Cli.Commands;
using SimplArchive.Cli.Infrastructure;
using SimplArchive.ConsoleLogging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    /// <summary>
    /// Runs saconsole: the logger lives exactly as long as the command, and is disposed before the process exits so its
    /// background writer flushes (ADR 0906, #1661). <c>--verbose</c> is taken out of the arguments before parsing: it is
    /// a property of the output, not of any command.
    /// </summary>
    public static int Run(string[] args)
    {
        var verbose = args.Contains(PlainConsoleLogging.VerboseFlag, StringComparer.Ordinal);
        using var logging = PlainConsoleLogging.CreateFactory(verbose);
        return Build(logging).Run([.. args.Where(a => a != PlainConsoleLogging.VerboseFlag)]);
    }

    public static CommandApp Build(ILoggerFactory logging)
    {
        var services = new ServiceCollection();
        services.AddSingleton(logging);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        var app = new CommandApp(new TypeRegistrar(services));
        app.Configure(config =>
        {
            config.SetApplicationName("saconsole");

            // EVERY error goes to STDERR, and that is load-bearing rather than tidy. `saconsole login` prints shell
            // exports to stdout so it can be used as `eval "$(saconsole login --url …)"` — so an error written to
            // stdout is an error the shell EXECUTES. Measured before this was fixed: an unreachable installation put
            // `Unexpected SocketException: Connection refused` on stdout, where eval would have run it.
            var errors = logging.CreateLogger("saconsole");

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
                        errors.LogError("{Message}", expected.Message);
                        return 1;

                    case CommandRuntimeException usage:
                        errors.LogError("{Message} Run with --help to see the options.", usage.Message);
                        return 1;

                    // The base exception of a refused connection is SocketException, so catching HttpRequestException
                    // alone would miss it — which is exactly how it reached the "real bug" branch.
                    case HttpRequestException or System.Net.Sockets.SocketException:
                        errors.LogError("Could not reach the installation. Check the URL, that it is running, and that this host can reach it.");
                        return 1;

                    case var bug:
                        errors.LogError("Unexpected {ExceptionType}: {Message}", bug.GetType().Name, bug.Message);
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

            // CERTIFICATES, the ADMINISTRATOR's surface (owner-decided): enrolling for OTHER people, in bulk,
            // from a certification authority's manifest. Deliberately not under `me` — that branch is the
            // signed-in user's own settings and writes the self-service MAIL certificate, which is a
            // different store from the reader certificates documents are addressed to (#1501).
            config.AddBranch("certificates", certificates =>
            {
                certificates.SetDescription("Reader certificates, for a tenant administrator.");
                certificates.AddCommand<CertificateListCommand>("list")
                    .WithDescription("Report what is enrolled, including what is no longer usable and why.");
                certificates.AddCommand<CertificateEnrolCommand>("enrol")
                    .WithDescription("Enrol one certificate, for a named holder or the caller.");
                certificates.AddCommand<CertificateRevokeCommand>("revoke")
                    .WithDescription("Revoke one, by serial or thumbprint.");
                certificates.AddCommand<CertificateImportCommand>("import")
                    .WithDescription("Enrol and revoke in bulk from a CA manifest. Safe to re-run.");
            });

            // The intray's recovery path (#799). The bytes an overwrite set aside have been preserved since
            // #794 and were reachable only by going into object storage by hand; this is the way back, and it
            // is here rather than in the two user interfaces because saconsole is a client too — an endpoint
            // it reaches by following a rel is complete, not a gap waiting for a button.
            config.AddBranch("intray", intray =>
            {
                intray.SetDescription("The signed-in user's intray.");
                intray.AddBranch("previous", previous =>
                {
                    previous.SetDescription("Bytes an intray overwrite set aside, and the way back to them.");
                    previous.AddCommand<IntrayPreviousListCommand>("list")
                        .WithDescription("What is recoverable, and how long is left to recover it.");
                    previous.AddCommand<IntrayPreviousRestoreCommand>("restore")
                        .WithDescription("Put one back into the intray. Reversible: what is there now is kept.");
                });
            });

            // Modules, and for now the one act a script needs that no client covers: filing a signed licence
            // and pointing the modules it names at it. Both clients already let a HUMAN do this; nothing let
            // a script, which is why the flight-school demo seeder grew its own inline copy (#1474).
            config.AddBranch("module", module =>
            {
                module.SetDescription("Modules: activation, settings and actions (tenant administrator), and uploads with a module credential.");
                module.AddCommand<ModuleListCommand>("list")
                    .WithDescription("Name the installed modules, and whether each is active for this tenant.");
                module.AddBranch("settings", moduleSettings =>
                {
                    moduleSettings.SetDescription("What a module declared it needs configuring (ADR 0772).");
                    moduleSettings.AddCommand<ModuleSettingsShowCommand>("show")
                        .WithDescription("Report the declared settings and what is configured. A secret's value is never shown.");
                    moduleSettings.AddCommand<ModuleSettingsSetCommand>("set")
                        .WithDescription("Write one setting. The value comes from --from-env, --from-file or --stdin, never an option.");
                });
                module.AddCommand<ModuleRebuildCommand>("rebuild")
                    .WithDescription("Re-derive a module's read model from documents. One projection, or all.");
                module.AddCommand<ModuleActivateCommand>("activate")
                    .WithDescription("File a vendor-signed licence and activate every module it names.");
                module.AddCommand<ModuleActionCommand>("action")
                    .WithDescription("Invoke a module's action on a document. A value it reveals once is written to a file.");
                module.AddCommand<ModuleUploadCommand>("upload")
                    .WithDescription("Upload a file to a module's upload rel with a module credential (e.g. an app to the app repository).");
            });

            config.AddBranch("repository", repository =>
            {
                repository.SetDescription("Repositories: the root documents everything is filed under (ADR 0200).");
                repository.AddCommand<RepositoryCreateCommand>("create")
                    .WithDescription("Create a repository, or find the one of that name. Prints its id on stdout.");
            });

            // ACL: `set` states the complete set of rights (a replace, because the endpoint is a PUT), while
            // `grant` and `revoke` adjust what is there. Three verbs for one concept, owner-decided
            // 2026-10-01, because one verb makes one of the two intentions awkward or dishonest.
            config.AddBranch("acl", acl =>
            {
                acl.SetDescription("Who may do what to a document.");
                acl.AddCommand<AclSetCommand>("set")
                    .WithDescription("State the COMPLETE set of rights a principal holds. Unnamed rights are withheld.");
                acl.AddCommand<AclGrantCommand>("grant")
                    .WithDescription("Add rights to what a principal already holds, leaving the rest alone.");
                acl.AddCommand<AclRevokeCommand>("revoke")
                    .WithDescription("Take rights away, leaving the rest alone.");
            });

            config.AddBranch("tenant", tenant =>
            {
                tenant.SetDescription("Tenant administration: provisioning (platform administrator) and the tenant's standard repository (tenant administrator).");
                tenant.AddCommand<TenantCreateCommand>("create")
                    .WithDescription("Provision a tenant with its first administrator and repository.");
                tenant.AddBranch("standard-repository", standard =>
                {
                    standard.SetDescription("Where SimplArchive files what it brings, its manuals first (ADR 0892).");
                    standard.AddCommand<StandardRepositoryShowCommand>("show")
                        .WithDescription("Show the tenant's standard repository.");
                    standard.AddCommand<StandardRepositorySetCommand>("set")
                        .WithDescription("Make another repository the standard one; the manuals folder moves with it.");
                });
            });
        });

        return app;
    }
}
