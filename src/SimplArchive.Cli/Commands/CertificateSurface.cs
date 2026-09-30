using SimplArchive.Cli.Infrastructure;

namespace SimplArchive.Cli.Commands;

/// <summary>
/// Where the Encryption Module's certificate surface lives — resolved by following a rel, once.
/// </summary>
/// <remarks>
/// <para>
/// ONE place, because four commands need the same address and four copies of "follow this rel" is four
/// places for the rel name to drift from the module's declaration.
/// </para>
/// <para>
/// <b>A missing rel is a real answer here</b> (ADR 0543): the API root emits a module's rels only for a
/// tenant whose activation is live, so absence means the Encryption Module is not active — which is a
/// different problem from anything in the certificate the caller is holding, and the refusal says so.
/// </para>
/// </remarks>
internal static class CertificateSurface
{
    internal const string Rel = "encryption:certificates";

    internal static async Task<string> AddressAsync(Hypermedia hypermedia, CancellationToken cancellationToken)
    {
        try
        {
            return await hypermedia.RootHrefAsync(Rel, cancellationToken);
        }
        catch (CliException)
        {
            throw new CliException(
                "This installation does not advertise the Encryption Module's certificate surface, so the "
                + "module is not active for this tenant. File its licence first: saconsole module activate.");
        }
    }
}
