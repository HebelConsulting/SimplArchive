namespace SimplArchive.Application.Abstractions;

/// <summary>
/// Reads the <c>TXT</c> records published at a DNS name — how a mail-domain claim is proven (#667).
/// </summary>
/// <remarks>
/// <para>
/// An abstraction rather than a direct call for the usual reason and one specific one: the tests must be able
/// to answer without a network, and a check whose only implementation talks to the real DNS is a check that
/// cannot be exercised in CI at all.
/// </para>
/// <para>
/// <b>An empty answer and a failed lookup are the same answer here.</b> Both mean "the record we asked for is
/// not visible to us", which is the only thing verification may conclude from. Distinguishing them would tempt
/// a caller into treating a resolver outage as a reason to verify anyway.
/// </para>
/// </remarks>
public interface IDnsTxtLookup
{
    /// <summary>The TXT strings published at <paramref name="name"/>, or empty when there are none.</summary>
    Task<IReadOnlyList<string>> GetTxtRecordsAsync(string name, CancellationToken cancellationToken);
}
