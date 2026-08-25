namespace SimplArchive.Application.Abstractions;

/// <summary>
/// A storage operation was asked about an object that is not there.
/// </summary>
/// <remarks>
/// <para>
/// It exists because "absent" and "present but unconfigured" are different facts that the S3 protocol reports
/// with the SAME status. Asking whether an object is locked, and being told <c>404</c>, means either "this
/// object has no lock configuration" or "there is no such object" — and answering the second as
/// <c>ObjectLockStatus(null, false)</c> states, confidently, that an object nobody can find carries no legal
/// hold. In a WORM and legal-hold context that is the most consequential sentence the system can get wrong.
/// </para>
/// <para>
/// A dedicated type rather than a bare exception so a caller can make a POLICY decision about the missing case —
/// the document purger treats a vanished blob as nothing to protect, while a caller verifying an audit segment
/// must not — without catching a vendor exception type and reaching straight through
/// <see cref="IObjectStorageClient"/>.
/// </para>
/// </remarks>
public sealed class StorageObjectNotFoundException : Exception
{
    public StorageObjectNotFoundException(string objectKey, Exception? innerException = null)
        : base($"Object '{objectKey}' does not exist in storage.", innerException)
        => ObjectKey = objectKey;

    public string ObjectKey { get; }
}
