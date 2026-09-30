namespace SimplArchive.Infrastructure.Storage;

/// <summary>
/// The encryption service refused a KEY operation — unwrapping a data key, or a rotation step (#1511).
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own type, and it exists because the diagnosis was being thrown away.</b> Every key call in
/// <see cref="AtRestKeyService"/> ended in a bare <c>EnsureSuccessStatusCode()</c>, so a perfectly specific
/// answer from the service became an <c>HttpRequestException</c> the Api could only render as
/// <c>500 INTERNAL_ERROR</c> / "An unexpected error occurred." Measured on 2026-10-01: the token door
/// answered exactly that while the service had said
/// <c>400 "The wrapped DEK is not usable." — PKCS#11 call 'C_Decrypt' failed with
/// CKR_ENCRYPTED_DATA_INVALID</c>, which is a complete diagnosis of a re-minted KEK (#1510). Recovering it
/// took a full scan of the object store and a hand-built oracle round trip.
/// </para>
/// <para>
/// <b>Separate from <see cref="EnvelopeServiceRefusedException"/> on purpose</b>, although the two are the
/// same shape. They have different remedies and name different endpoints, and ADR 0859's standing argument
/// is that refusals whose fixes differ must stay distinguishable — collapsing them is how a reader comes to
/// be told to re-register a certificate that is perfectly fine.
/// </para>
/// <para>
/// <b>It carries its own wire code</b>, the way
/// <see cref="Application.Abstractions.PlaintextContentRefusedException"/> does and for the same reason: it is
/// thrown from the storage seam, which cannot reference the Api project's exception types, and it is reachable
/// from a dozen controllers that read through that seam rather than from one place a translation could sit.
/// The split follows <see cref="OurRequest"/> — a 4xx says this installation cannot decrypt that object, which
/// no retry will change and which a reader can only quote to an administrator; a 5xx is an outage that may
/// recover.
/// </para>
/// </remarks>
public sealed class AtRestKeyRefusedException(string operation, int statusCode, string? detail)
    : Exception($"The encryption service refused to {operation} with {statusCode}: {detail}")
{
    /// <summary>The status the service answered — 4xx is our request or our data, 5xx is the service.</summary>
    public int StatusCode { get; } = statusCode;

    /// <summary>What the service said, or null when it said nothing readable.</summary>
    /// <remarks>
    /// An administrator's diagnostic, never shown to a reader: a service's prose is not localized and names
    /// internals (a PKCS#11 return code, in the case this type was written for).
    /// </remarks>
    public string? Detail { get; } = detail;

    /// <summary>Whether the fault is in what we asked or in what we stored, rather than in the service.</summary>
    public bool OurRequest => StatusCode is >= 400 and < 500;

    /// <summary>The error code the Api answers with.</summary>
    public string ErrorCode => OurRequest ? "AT_REST_KEY_UNUSABLE" : "AT_REST_KEY_SERVICE_UNAVAILABLE";

    /// <summary>The status the Api answers with.</summary>
    /// <remarks>
    /// A refusal about our own stored data is <b>500</b>, not the 4xx the service used: nothing the caller
    /// did is wrong and there is nothing for them to correct, so passing the service's status through would
    /// blame the reader for a key that does not match its object.
    /// </remarks>
    public int ApiStatusCode => OurRequest ? 500 : 503;

    /// <summary>What a reader is told — enough to quote, never the service's internals.</summary>
    public string ApiDetail => OurRequest
        ? "This content cannot be decrypted: its data key does not match the key its KEK generation now holds. "
            + "An administrator needs to investigate; retrying will not help."
        : "The encryption service is unavailable, so this content cannot be decrypted. Try again shortly.";
}
