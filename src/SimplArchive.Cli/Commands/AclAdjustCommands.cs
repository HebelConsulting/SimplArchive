using SimplArchive.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SimplArchive.Cli.Commands;

/// <summary>
/// The shared half of <c>grant</c> and <c>revoke</c>: read what is there, adjust it, write the whole value
/// back under the tag it was read with (#1504).
/// </summary>
/// <remarks>
/// <para>
/// <b>These are a read-modify-write over an API that has no partial update</b>, and that is deliberate rather
/// than hidden. The API serves no <c>PATCH</c> on purpose — a delta applied to whatever is stored at that
/// moment is the lost-update shape <c>If-Match</c> exists to prevent — so "add one right" has to become
/// *read the set, add, state the whole set*. The precondition is the tag from the read that produced the
/// value, which is what makes a concurrent change a <b>412</b> rather than a silent overwrite.
/// </para>
/// <para>
/// <b>A 412 is a real case here</b>, not a generic failure: somebody else changed this principal's rights
/// between the read and the write, and the answer is to run the command again. The API client says exactly
/// that, so nothing is added on top of it.
/// </para>
/// </remarks>
public abstract class AclAdjustCommand(IAnsiConsole console) : AsyncCommand<AclSettings>
{
    /// <summary>Whether the named rights are added or taken away.</summary>
    protected abstract bool Granting { get; }

    protected override async Task<int> ExecuteAsync(
        CommandContext context, AclSettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.Rights))
        {
            throw new CliException(
                $"Name what to {(Granting ? "grant" : "revoke")} with --rights. "
                + $"Rights: {string.Join(", ", AclRights.All)}. "
                + $"Presets: {string.Join(", ", AclRights.Presets.Keys)}.");
        }

        using var http = CertificateEndpoint.Client(settings);
        var api = new SimplArchiveApi(http);

        var target = await AclSurface.ResolveAsync(api, settings, cancellationToken);
        var named = AclRights.Expand(settings.Rights);

        // THE TAG FROM THE READ THAT PRODUCED THE VALUE (ADR 0794), not a re-read immediately before writing
        // — that one asserts "I edited what was there a millisecond ago", which is always true and can
        // essentially never fail.
        var (current, etag) = await AclSurface.CurrentAsync(api, target.Address, cancellationToken);
        var intended = Granting ? current.Plus(named) : current.Minus(named);

        if (intended == current)
        {
            AclSurface.Report(console, target, current, intended);
            return 0;
        }

        await api.PutWithETagAsync(target.Address, AclSurface.Body(intended), etag, cancellationToken);
        AclSurface.Report(console, target, current, intended);
        return 0;
    }
}

/// <summary>Adds rights to what a principal already holds, leaving the rest alone.</summary>
public sealed class AclGrantCommand(IAnsiConsole console) : AclAdjustCommand(console)
{
    protected override bool Granting => true;
}

/// <summary>
/// Takes rights away, leaving the rest alone.
/// </summary>
/// <remarks>
/// Revoking every right leaves an entry granting <b>nothing</b>, which is not the same as having no entry:
/// an explicit empty grant still breaks nothing and inherits nothing extra, while a deleted entry returns the
/// principal to whatever inheritance gives them. Removing the entry entirely is
/// <c>DELETE</c> on the same address and is deliberately not this command — "take away the right to delete"
/// and "take away their entry" are different acts, and one verb for both is how the wrong one gets run.
/// </remarks>
public sealed class AclRevokeCommand(IAnsiConsole console) : AclAdjustCommand(console)
{
    protected override bool Granting => false;
}
