namespace SimplArchive.Infrastructure.Storage;

/// <summary>
/// The object was wrapped by a different key than the one its KEK generation holds now (#1510, ADR 0867).
/// </summary>
/// <remarks>
/// <para>
/// <b>A generation's label is not its identity.</b> <c>kek-v1</c> addresses whichever key currently wears that
/// label on whichever token the encryption service is pointed at, and every encrypted object records only that
/// string. So a token that is re-provisioned, restored or re-pointed mints a fresh <c>kek-v1</c> — locally
/// correct, and every object the previous one wrapped is permanently unreadable.
/// </para>
/// <para>
/// <b>Measured, which is why this type exists:</b> 5 of 48 encrypted objects dead on a development stack, two
/// of them WORM audit segments that can never be re-wrapped. The only symptom was
/// <c>CKR_ENCRYPTED_DATA_INVALID</c> on a read months later — a message that says <i>the ciphertext is
/// wrong</i>, which sends a reader looking for a corrupt blob. This says what actually happened.
/// </para>
/// <para>
/// <b>Thrown INSTEAD of calling the oracle, and that costs nothing.</b> The oracle unwraps with whatever key
/// is behind the label, so if the key differs the unwrap cannot succeed — the refusal forgoes a round trip and
/// a misleading error, not a chance of working. The honest risk is the inverse: if our own stamp were wrong,
/// this blocks a read that would have worked. An escape-hatch setting was rejected (ADR 0343 is no-bypass-flags),
/// and the mitigation is that an ABSENT stamp is always permitted, so only objects this installation itself
/// stamped are ever refused.
/// </para>
/// <para>
/// <b>Separate from <see cref="AtRestKeyRefusedException"/></b>, which is the service declining an operation.
/// The remedies differ completely — that one may be an outage that recovers, this one never recovers and means
/// an administrator must find out what happened to the token — and ADR 0859's standing argument is that
/// refusals whose fixes differ must stay distinguishable.
/// </para>
/// </remarks>
public sealed class AtRestKeyChangedException(string objectKey, string generation, string stamped, string held)
    : Exception(
        $"'{objectKey}' was wrapped under {generation} when its key was {stamped}, but the key now behind "
        + $"{generation} is {held}. The token has been re-provisioned, restored or recreated, so this object "
        + "cannot be unwrapped by the current key.")
{
    /// <summary>The object that cannot be decrypted.</summary>
    public string ObjectKey { get; } = objectKey;

    /// <summary>The generation both thumbprints are about.</summary>
    public string Generation { get; } = generation;

    /// <summary>The thumbprint stamped on the object when it was written.</summary>
    public string StampedThumbprint { get; } = stamped;

    /// <summary>The thumbprint of the key the generation holds now.</summary>
    public string HeldThumbprint { get; } = held;

    /// <summary>The error code the Api answers with.</summary>
    public string ErrorCode => "AT_REST_KEY_CHANGED";

    /// <summary>
    /// <b>500</b>, not a 4xx: nothing the caller did is wrong and there is nothing for them to correct.
    /// </summary>
    public int ApiStatusCode => 500;

    /// <summary>What a reader is told — enough to quote to an administrator, and no thumbprints.</summary>
    /// <remarks>
    /// The thumbprints are an administrator's diagnostic and are in the log line and the exception message. A
    /// reader cannot act on them, and telling them to retry would be false: this one never recovers on its own.
    /// </remarks>
    public string ApiDetail =>
        "This content was encrypted with an earlier key that the encryption service no longer holds, so it "
        + "cannot be decrypted. An administrator needs to investigate; retrying will not help.";
}
