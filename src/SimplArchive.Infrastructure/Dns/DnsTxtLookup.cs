using DnsClient;
using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Dns;

/// <inheritdoc cref="IDnsTxtLookup"/>
/// <remarks>
/// <para>
/// <b>Why a library at all:</b> the BCL cannot ask for a TXT record. <c>System.Net.Dns</c> resolves names to
/// addresses and back and offers nothing else, so the choice is a DNS client, shelling out to <c>dig</c> — not
/// present in the Alpine image, and parsing its output is worse than parsing the wire format — or querying a
/// resolver over HTTPS, which trades a local lookup for a dependency on somebody else's service.
/// </para>
/// <para>
/// The resolvers are the HOST's own, as configured. That is deliberate: a deployment that runs a split-horizon
/// or internal resolver gets the answer its operator intends, and a check that hard-coded a public resolver
/// would disagree with the network it runs in.
/// </para>
/// </remarks>
public sealed class DnsTxtLookup(ILogger<DnsTxtLookup> logger) : IDnsTxtLookup
{
    // Short, because a verification request is a person waiting on a form. A resolver that has not answered in
    // a couple of seconds is not going to make the difference between verified and not — the administrator
    // retries once the record has propagated anyway.
    private readonly LookupClient _client = new(new LookupClientOptions
    {
        Timeout = TimeSpan.FromSeconds(3),
        Retries = 1,
        UseCache = false, // a just-published record is exactly what this asks about
    });

    public async Task<IReadOnlyList<string>> GetTxtRecordsAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _client.QueryAsync(name, QueryType.TXT, cancellationToken: cancellationToken);

            // Each TXT record can carry several character-strings, which resolvers hand back separately; a
            // value longer than 255 bytes is published split and must be joined before comparing. Ours is
            // shorter than that, but joining costs nothing and the alternative is a token that verifies
            // everywhere except where someone padded the record.
            var values = response.Answers.TxtRecords()
                .Select(r => string.Concat(r.Text))
                .Where(v => v.Length > 0)
                .ToList();

            // Trace carries the exchange for every outbound client (ADR 0626): what was asked, what came back.
            logger.LogTrace("DNS TXT {Name} → {Count} record(s): {Values}", name, values.Count, values);
            return values;
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // Not a Warning: a lookup that cannot reach a resolver is reported to the caller as "the record is
            // not visible", and the caller — the verification endpoint — is what tells the administrator, in
            // words, on the screen they are looking at. Logging it as an administrator-actionable event here
            // would fire on every mistyped domain a user tries.
            logger.LogDebug(failure, "DNS TXT lookup for {Name} failed; treating it as no records.", name);
            return [];
        }
    }
}
