using SimplArchive.Infrastructure.Secrets;

namespace SimplArchive.IntegrationTests;

// Verifies the transit-encryptor contract (ADR "MFA require-policy + TOTP secret encryption"): the null
// pass-through (dev/tests) round-trips unchanged, and the OpenBao encryptor treats a non-"vault:" value as
// pre-encryption plaintext (backward compatibility) without needing a live OpenBao. The real OpenBao transit
// round-trip is covered by OpenBaoSecretsTests in the E2E project.
public class TransitEncryptorTests
{
    [Fact]
    public async Task Null_encryptor_passes_through_unchanged()
    {
        var encryptor = new NullTransitEncryptor();
        Assert.Equal("JBSWY3DPEHPK3PXP", await encryptor.EncryptAsync("JBSWY3DPEHPK3PXP"));
        Assert.Equal("JBSWY3DPEHPK3PXP", await encryptor.DecryptAsync("JBSWY3DPEHPK3PXP"));
    }

    [Fact]
    public async Task OpenBao_encryptor_returns_plaintext_that_isnt_vault_ciphertext_unchanged()
    {
        // A pre-encryption plaintext secret (no "vault:" prefix) must verify as-is — this path never calls
        // OpenBao, so no live server is needed. (An unreachable address would throw if it tried.)
        var encryptor = new OpenBaoTransitEncryptor("http://openbao.invalid:8200", "role", "secret", "simplarchive-mfa");
        Assert.Equal("JBSWY3DPEHPK3PXP", await encryptor.DecryptAsync("JBSWY3DPEHPK3PXP"));
    }
}
