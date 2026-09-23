using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Encryption;

// Thrown when a finalize carries client-side encryption fields the server cannot honour (ADR 0818/B2):
// a wrapped DEK for a tenant that is not encryption-gated (the object would be stored ciphertext with no
// decorator to ever decrypt it — refusing loudly beats archiving an unreadable blob), or a wrapped DEK
// without its KEK generation (unusable by construction).
public sealed class EncryptedUploadRejectedException : EncryptionException
{
    public EncryptedUploadRejectedException(string reason)
        : base("ENCRYPTED_UPLOAD_REJECTED", StatusCodes.Status400BadRequest,
            $"The encrypted upload cannot be accepted: {reason}")
    {
    }
}
