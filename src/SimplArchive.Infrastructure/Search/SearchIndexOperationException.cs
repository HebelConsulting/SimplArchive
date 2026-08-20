namespace SimplArchive.Infrastructure.Search;

/// <summary>
/// An OpenSearch admin call the index build depends on was refused — creating the index, swapping the alias.
/// </summary>
/// <remarks>
/// It exists to carry the RESPONSE BODY. <c>EnsureSuccessStatusCode</c> throws
/// "Response status code does not indicate success: 403 (Forbidden)" and discards the only part that says
/// why — and OpenSearch puts the actual reason in the body, as a typed error (a cluster block, a mapping
/// conflict, a security denial) that reads very differently from one another. A 403 with the body dropped
/// cost a full CI round trip and two wrong diagnoses on #660, because from the outside "refused" and
/// "still working" are indistinguishable once the failure is swallowed.
/// </remarks>
public sealed class SearchIndexOperationException : Exception
{
    public SearchIndexOperationException(string message) : base(message)
    {
    }

    /// <summary>Throws with the status AND the body when <paramref name="response"/> failed.</summary>
    /// <param name="what">What was attempted, in the imperative — "create index documents-abc".</param>
    public static async Task ThrowIfFailedAsync(
        HttpResponseMessage response, string what, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        // Capped: an OpenSearch error body can carry a root_cause array per shard, and the reason is at the
        // front. Bounded so a pathological body cannot flood the log this exists to make readable.
        if (body.Length > 2000)
        {
            body = $"{body[..2000]}… (truncated)";
        }

        throw new SearchIndexOperationException(
            $"Failed to {what}: {(int)response.StatusCode} {response.ReasonPhrase}. OpenSearch said: {body}");
    }
}
