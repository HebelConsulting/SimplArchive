using System.Security.Cryptography;
using SimplArchive.Infrastructure.Storage;

namespace SimplArchive.UnitTests;

// The at-rest blob format (ADR 0818): nonce(12) ‖ ciphertext ‖ tag(16), AES-256-GCM — shared with the
// encryption service and the future client-side upload leg, so the format IS the contract.
public class AtRestBlobCipherTests
{
    [Fact]
    public void Round_trips_and_carries_exactly_the_documented_overhead()
    {
        var dek = RandomNumberGenerator.GetBytes(32);
        var plaintext = RandomNumberGenerator.GetBytes(64 * 1024);

        var blob = AtRestBlobCipher.Encrypt(dek, plaintext);
        Assert.Equal(plaintext.Length + AtRestBlobCipher.Overhead, blob.Length);
        Assert.Equal(plaintext, AtRestBlobCipher.Decrypt(dek, blob));
    }

    [Fact]
    public void A_wrong_key_and_a_tampered_byte_both_refuse()
    {
        var dek = RandomNumberGenerator.GetBytes(32);
        var blob = AtRestBlobCipher.Encrypt(dek, "sensitive"u8);

        Assert.ThrowsAny<CryptographicException>(
            () => AtRestBlobCipher.Decrypt(RandomNumberGenerator.GetBytes(32), blob));

        blob[AtRestBlobCipher.NonceSize] ^= 0xFF; // flip one ciphertext bit — GCM authenticates
        Assert.ThrowsAny<CryptographicException>(() => AtRestBlobCipher.Decrypt(dek, blob));
    }

    [Fact]
    public void Shorter_than_the_overhead_is_refused_loudly()
    {
        Assert.Throws<ArgumentException>(
            () => AtRestBlobCipher.Decrypt(RandomNumberGenerator.GetBytes(32), new byte[AtRestBlobCipher.Overhead - 1]));
    }
}
