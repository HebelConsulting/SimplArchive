namespace SimplArchive.Infrastructure.Storage;

/// <summary>
/// An object is encrypted at rest and this installation has no encryption service to unwrap it (#1499).
/// </summary>
/// <remarks>
/// <para>
/// Its own type because the alternative is what this replaced: the ciphertext handed on as though it were the
/// document, at <b>200</b>, with the symptom appearing at the far end as an unrenderable file. A named
/// exception makes the cause reach a log and an administrator instead of a user reporting that a preview
/// stopped working.
/// </para>
/// <para>
/// Deliberately NOT an <c>ApiException</c> subclass: this is Infrastructure, which the architecture tests
/// keep free of Api, and the condition is a misconfiguration of the INSTALLATION rather than anything the
/// caller did — so it surfaces as a 500 with the cause in the log, which is the honest status for "this
/// server is not set up to serve what you asked for".
/// </para>
/// </remarks>
public sealed class EncryptedObjectWithoutEncryptionServiceException(string objectKey)
    : InvalidOperationException(
        $"Object '{objectKey}' is encrypted at rest, but this installation has no Encryption:ServiceUrl and "
        + "therefore cannot unwrap its data key. Serving it would hand out ciphertext as though it were the "
        + "document. Restore the encryption service configuration this store was written under.")
{
    /// <summary>The object that could not be served.</summary>
    public string ObjectKey { get; } = objectKey;

    /// <summary>Error code for the API boundary, beside its at-rest siblings (#1582).</summary>
    public string ErrorCode => "ENCRYPTION_SERVICE_NOT_CONFIGURED";

    /// <summary>503: the installation can be fixed, and the object is intact meanwhile.</summary>
    /// <remarks>
    /// Not 500. Nothing is broken and nothing is lost — the store was written under an encryption service this
    /// process is no longer pointed at, so the read becomes possible again the moment the configuration is
    /// restored. 503 is the status that says "this server cannot serve it RIGHT NOW", which is the true one.
    /// </remarks>
    public int ApiStatusCode => 503;

    /// <summary>
    /// What the caller is told. The object key stays out of it — it is in the Error log line, which is where
    /// an operator is already looking — but the CAUSE is named, because "no text layout" sent a reader hunting
    /// for a missing text layer when the installation was misconfigured (#1582).
    /// </summary>
    public string ApiDetail =>
        "This installation stores content encrypted at rest but is not configured with an encryption service, "
        + "so it cannot read it back. Nothing is lost: restore the Encryption:ServiceUrl this store was "
        + "written under. Quote this to an administrator rather than retrying.";
}
