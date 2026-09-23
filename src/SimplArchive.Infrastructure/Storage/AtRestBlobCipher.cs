using System.Security.Cryptography;

namespace SimplArchive.Infrastructure.Storage;

/// <summary>
/// The at-rest blob cipher (ADR 0818): AES-256-GCM over the one wire format every encrypting party
/// shares — <c>nonce(12) ‖ ciphertext ‖ tag(16)</c> — the same convention the encryption service and the
/// future client-side upload leg use (SimplArchiveEncryption ADR 0011).
/// </summary>
public static class AtRestBlobCipher
{
    public const int NonceSize = 12;
    public const int TagSize = 16;

    /// <summary>Ciphertext is exactly this much longer than its plaintext.</summary>
    public const int Overhead = NonceSize + TagSize;

    public static byte[] Encrypt(byte[] dek, ReadOnlySpan<byte> plaintext)
    {
        var blob = new byte[NonceSize + plaintext.Length + TagSize];
        RandomNumberGenerator.Fill(blob.AsSpan(0, NonceSize));
        using var aes = new AesGcm(dek, TagSize);
        aes.Encrypt(
            nonce: blob.AsSpan(0, NonceSize),
            plaintext: plaintext,
            ciphertext: blob.AsSpan(NonceSize, plaintext.Length),
            tag: blob.AsSpan(blob.Length - TagSize));
        return blob;
    }

    public static byte[] Decrypt(byte[] dek, ReadOnlySpan<byte> blob)
    {
        if (blob.Length < Overhead)
        {
            throw new ArgumentException($"The blob is {blob.Length} bytes — shorter than nonce + tag.", nameof(blob));
        }

        var plaintext = new byte[blob.Length - Overhead];
        using var aes = new AesGcm(dek, TagSize);
        aes.Decrypt(
            nonce: blob[..NonceSize],
            ciphertext: blob[NonceSize..^TagSize],
            tag: blob[^TagSize..],
            plaintext: plaintext);
        return plaintext;
    }
}
