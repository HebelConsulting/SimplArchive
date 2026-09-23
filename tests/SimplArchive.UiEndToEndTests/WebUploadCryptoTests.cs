using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SimplArchive.UiEndToEndTests;

// The web uploader's client-side encryption (ADR 0818/B2), proven CROSS-IMPLEMENTATION: the real
// dropUpload.js saUploadCrypto runs in the real browser against a keypair the page generates, and the
// .NET side (this test standing in for the server's decorator) must unwrap and decrypt what it produced.
// A replay built by analogy would replay assumptions; this executes the shipped JS byte for byte.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebUploadCryptoTests
{
    private readonly SelfHostedAppFixture _app;

    public WebUploadCryptoTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task What_the_browser_encrypts_the_dotnet_side_opens()
    {
        var page = await Ui.LoginAsync(_app);

        var result = await page.EvaluateAsync<JsonElement>("""
            async () => {
                await import('/dropUpload.js'); // the shipped module registers window.saUploadCrypto
                const pair = await crypto.subtle.generateKey(
                    { name: 'RSA-OAEP', modulusLength: 2048, publicExponent: new Uint8Array([1, 0, 1]), hash: 'SHA-256' },
                    true, ['encrypt', 'decrypt']);
                const spki = new Uint8Array(await crypto.subtle.exportKey('spki', pair.publicKey));
                const pkcs8 = new Uint8Array(await crypto.subtle.exportKey('pkcs8', pair.privateKey));
                const b64 = a => btoa(String.fromCharCode(...a));
                const pem = '-----BEGIN PUBLIC KEY-----\n' + b64(spki) + '\n-----END PUBLIC KEY-----';

                const plaintext = new TextEncoder().encode('WEBCRYPTO-CROSS-IMPL-MARKER');
                const enc = await window.saUploadCrypto.encrypt(plaintext.buffer,
                    { publicKeyPem: pem, oaepHash: 'SHA256', kekGeneration: 'kek-test' });
                return { blob: b64(enc.blob), wrappedDek: enc.wrappedDek, privateKey: b64(pkcs8) };
            }
            """);

        using var kek = RSA.Create();
        kek.ImportPkcs8PrivateKey(Convert.FromBase64String(result.GetProperty("privateKey").GetString()!), out _);
        var dek = kek.Decrypt(Convert.FromBase64String(result.GetProperty("wrappedDek").GetString()!),
            RSAEncryptionPadding.OaepSHA256);
        Assert.Equal(32, dek.Length);

        var blob = Convert.FromBase64String(result.GetProperty("blob").GetString()!);
        var plaintext = new byte[blob.Length - 28];
        using var aes = new AesGcm(dek, 16);
        aes.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(12, plaintext.Length), blob.AsSpan(^16..), plaintext);

        Assert.Equal("WEBCRYPTO-CROSS-IMPL-MARKER", Encoding.UTF8.GetString(plaintext));
    }
}
