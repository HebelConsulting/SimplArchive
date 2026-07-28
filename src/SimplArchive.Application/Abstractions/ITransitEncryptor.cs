namespace SimplArchive.Application.Abstractions;

// Encrypts/decrypts small secrets at rest via a key the app never sees (ADR "MFA require-policy + TOTP secret
// encryption") — backed by OpenBao's transit engine when configured, else a pass-through (dev/tests). Used to
// protect the stored TOTP secret. Decrypt is backward-compatible: a value that isn't OpenBao ciphertext (no
// "vault:" prefix) is returned unchanged, so pre-encryption plaintext secrets still verify.
public interface ITransitEncryptor
{
    Task<string> EncryptAsync(string plaintext, CancellationToken cancellationToken = default);

    Task<string> DecryptAsync(string ciphertext, CancellationToken cancellationToken = default);
}
