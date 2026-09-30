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
}
