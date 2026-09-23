using System;
using System.Security.Cryptography;
using System.Text.Json;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// Client-side at-rest encryption for uploads (ADR 0818/B2): when a create-version response carries the
/// `encryption` object (encryption-gated tenants only — its presence is the instruction), the bytes are
/// AES-256-GCM-encrypted as <c>nonce(12) ‖ ciphertext ‖ tag(16)</c> and the fresh DEK wrapped RSA-OAEP
/// against the published KEK — the same shapes the server's decorator and the web uploader use, so all
/// three producers meet one format.
/// </summary>
internal static class UploadEncryption
{
    internal sealed record Encrypted(byte[] Blob, string WrappedDek, string KekGeneration);

    /// <summary>Returns null when the response carries no encryption instruction (plaintext upload).</summary>
    internal static Encrypted? EncryptIfInstructed(JsonElement createVersionResponse, byte[] plaintext)
    {
        if (!createVersionResponse.TryGetProperty("encryption", out var encryption)
            || encryption.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var dek = RandomNumberGenerator.GetBytes(32);
        var blob = new byte[12 + plaintext.Length + 16];
        RandomNumberGenerator.Fill(blob.AsSpan(0, 12));
        using (var aes = new AesGcm(dek, 16))
        {
            aes.Encrypt(blob.AsSpan(0, 12), plaintext, blob.AsSpan(12, plaintext.Length), blob.AsSpan(blob.Length - 16));
        }

        using var kek = RSA.Create();
        kek.ImportFromPem(encryption.GetProperty("publicKeyPem").GetString()!);
        var padding = encryption.GetProperty("oaepHash").GetString() == "SHA1"
            ? RSAEncryptionPadding.OaepSHA1
            : RSAEncryptionPadding.OaepSHA256;

        return new Encrypted(
            blob,
            Convert.ToBase64String(kek.Encrypt(dek, padding)),
            encryption.GetProperty("kekGeneration").GetString()!);
    }
}
