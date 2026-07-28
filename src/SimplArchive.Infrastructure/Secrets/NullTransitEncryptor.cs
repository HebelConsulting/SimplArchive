using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Secrets;

// The pass-through encryptor used when OpenBao isn't configured (ADR "MFA require-policy + TOTP secret
// encryption") — the secret is stored as-is (dev-grade plaintext, as before). Registered when OpenBao:Address
// is unset, so tests and non-OpenBao deployments are unaffected.
public sealed class NullTransitEncryptor : ITransitEncryptor
{
    public Task<string> EncryptAsync(string plaintext, CancellationToken cancellationToken = default) => Task.FromResult(plaintext);

    public Task<string> DecryptAsync(string ciphertext, CancellationToken cancellationToken = default) => Task.FromResult(ciphertext);
}
