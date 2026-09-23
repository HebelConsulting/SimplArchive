using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The desktop uploader's client-side encryption (ADR 0818/B2): the instruction-parsing and the blob
// format, driven through the same internal seam DocumentsClient uses. The server contract itself is
// proven by the E2E client-flow test; this pins the desktop's producer to the shared format.
public class DesktopUploadEncryptionTests
{
    [Fact]
    public void No_instruction_means_plaintext()
    {
        var response = JsonSerializer.Deserialize<JsonElement>("""{"id":"x","uploadUrl":"u"}""");
        Assert.Null(UploadEncryption.EncryptIfInstructed(response, [1, 2, 3]));
    }

    [Fact]
    public void The_instruction_yields_the_shared_format_and_a_wrap_the_kek_opens()
    {
        using var kek = RSA.Create(2048);
        var response = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
        {
            id = "x",
            uploadUrl = "u",
            encryption = new { kekGeneration = "kek-v7", publicKeyPem = kek.ExportSubjectPublicKeyInfoPem(), oaepHash = "SHA256" },
        }));

        var plaintext = Encoding.ASCII.GetBytes("DESKTOP-CROSS-IMPL-MARKER");
        var encrypted = UploadEncryption.EncryptIfInstructed(response, plaintext)!;
        Assert.Equal("kek-v7", encrypted.KekGeneration);
        Assert.Equal(plaintext.Length + 28, encrypted.Blob.Length); // nonce(12) ‖ ct ‖ tag(16)

        var dek = kek.Decrypt(Convert.FromBase64String(encrypted.WrappedDek), RSAEncryptionPadding.OaepSHA256);
        var opened = new byte[plaintext.Length];
        using var aes = new AesGcm(dek, 16);
        aes.Decrypt(encrypted.Blob.AsSpan(0, 12), encrypted.Blob.AsSpan(12, plaintext.Length),
            encrypted.Blob.AsSpan(^16..), opened);
        Assert.Equal(plaintext, opened);
    }
}
