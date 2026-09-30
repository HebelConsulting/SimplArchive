namespace SimplArchive.Infrastructure.Storage;

/// <summary>
/// The encryption service answered, and what it answered was a REFUSAL (ADR 0862).
/// </summary>
/// <remarks>
/// <para>
/// Its own type because a refusal and an outage are different facts with different remedies, and the code
/// could not tell them apart: <c>EnsureSuccessStatusCode</c> threw <c>HttpRequestException</c> for both, and
/// the Api mapped every one of those to "the encryption service is unavailable, try again shortly". So a
/// <c>400</c> naming something about OUR request — a recipient certificate the service cannot address, a DEK
/// that does not belong to the blob — reached the reader as a transient outage, told them to retry something
/// that can never succeed, and left no trace of the service's own words anywhere in this process.
/// </para>
/// <para>
/// Measured on the demo stack: a reader whose only enrolled certificate held an EC (P-256) key got
/// <c>503 ENVELOPE_SERVICE_UNAVAILABLE</c> on every document while the service was healthy and had already
/// fetched and decrypted the blob. Diagnosing it needed the service's own stdout, which an administrator of a
/// real installation may not have.
/// </para>
/// <para>
/// <see cref="Detail"/> is the service's problem-document text, carried so the Api can log what was refused.
/// It is a diagnostic for an administrator, never shown to the reader — a service's prose is not localized and
/// may name internals.
/// </para>
/// </remarks>
public sealed class EnvelopeServiceRefusedException(int statusCode, string? detail)
    : Exception($"The encryption service refused the envelope request with {statusCode}: {detail}")
{
    /// <summary>The status the service answered — 4xx is our request, 5xx is the service's own trouble.</summary>
    public int StatusCode { get; } = statusCode;

    /// <summary>What the service said, or null when it said nothing readable.</summary>
    public string? Detail { get; } = detail;

    /// <summary>Whether the fault is in what we asked, rather than in the service.</summary>
    /// <remarks>
    /// The split that matters to a caller: a 4xx will answer the same way however often it is retried, so a
    /// client must not be told to try again shortly. A 5xx may recover on its own and is the outage case.
    /// </remarks>
    public bool OurRequest => StatusCode is >= 400 and < 500;
}
