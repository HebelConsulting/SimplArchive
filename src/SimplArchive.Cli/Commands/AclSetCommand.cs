using System.ComponentModel;
using SimplArchive.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SimplArchive.Cli.Commands;

public sealed class AclSetSettings : AclSettings
{
    [CommandOption("--yes")]
    [Description("Proceed even when this withdraws rights the principal currently holds.")]
    public bool Yes { get; init; }
}

/// <summary>
/// States the complete set of rights a principal holds on a document — a replace, because the API's is a
/// <c>PUT</c> (#1504).
/// </summary>
/// <remarks>
/// <para>
/// <b>Three commands for one concept, by owner decision (2026-10-01)</b>, because the alternatives each make
/// one intent awkward. This one says *"these rights and no others"*, which is what the endpoint actually
/// does; <c>grant</c> and <c>revoke</c> adjust what is there. Collapsing them into one verb would have meant
/// either a command named <c>grant</c> that silently withdraws everything unnamed, or hiding a
/// read-modify-write behind every write.
/// </para>
/// <para>
/// <b>Withdrawing needs <c>--yes</c>.</b> The failure this guards is specific and easy: an operator who means
/// *"also let them annotate"* writes <c>set --rights annotate</c> and takes away everything else, with a
/// success message. Adding is unguarded, because adding is what the command obviously does; only the silent
/// subtraction is surprising.
/// </para>
/// </remarks>
public sealed class AclSetCommand(IAnsiConsole console) : AsyncCommand<AclSetSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, AclSetSettings settings, CancellationToken cancellationToken)
    {
        using var http = CertificateEndpoint.Client(settings);
        var api = new SimplArchiveApi(http);

        var target = await AclSurface.ResolveAsync(api, settings, cancellationToken);
        var intended = AclRights.Parse(settings.Rights);
        var (current, etag) = await AclSurface.CurrentAsync(api, target.Address, cancellationToken);

        var withdrawn = AclRights.All.Where(r => current.Has(r) && !intended.Has(r)).ToList();
        if (withdrawn.Count > 0 && !settings.Yes)
        {
            throw new CliException(
                $"This would withdraw {string.Join(", ", withdrawn)} from {target.PrincipalName}, because "
                + "'set' states the COMPLETE set of rights and anything unnamed is withheld. "
                + $"To add rights without touching the rest: saconsole acl grant {settings.Document} "
                + $"--{Option(target.PrincipalKind)} \"{target.PrincipalName}\" --rights {settings.Rights}. "
                + "To proceed as written, pass --yes.");
        }

        await api.PutWithETagAsync(target.Address, AclSurface.Body(intended), etag, cancellationToken);
        AclSurface.Report(console, target, current, intended);
        return 0;
    }

    /// <summary>The option that names this principal kind, so the hint above is copy-pasteable.</summary>
    private static string Option(string kind) => kind switch
    {
        "users" => "user",
        "groups" => "group",
        _ => "service-account",
    };
}
