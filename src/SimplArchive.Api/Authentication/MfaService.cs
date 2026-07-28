using System.Security.Cryptography;
using System.Text;
using OtpNet;
using QRCoder;

namespace SimplArchive.Api.Authentication;

// Two-factor (TOTP) helpers (ADR "MFA (interactive login, TOTP)") — secret generation, the otpauth URI + its
// QR, code verification with a small time skew, and one-time recovery codes. Stateless, registered as a
// singleton. Lives in the Api project (near the login page + the users controller that use it), matching where
// ProfilePhotoValidator / TenantProvisioningService already sit.
public sealed class MfaService
{
    private const string Issuer = "SimplArchive";
    private const int RecoveryCodeCount = 10;
    // Base32-ish alphabet without ambiguous characters (no 0/1/O/I/L).
    private const string RecoveryAlphabet = "abcdefghjkmnpqrstuvwxyz23456789";

    // A base32 shared secret for a new enrollment.
    public string GenerateSecret() => Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(20));

    // otpauth://totp/<Issuer>:<account>?secret=...&issuer=<Issuer> — what an authenticator app consumes.
    public string BuildOtpauthUri(string secret, string account)
    {
        var label = Uri.EscapeDataString($"{Issuer}:{account}");
        return $"otpauth://totp/{label}?secret={secret}&issuer={Uri.EscapeDataString(Issuer)}&digits=6&period=30";
    }

    // The enrollment QR as a PNG (data the client renders inline).
    public byte[] GenerateQrPng(string otpauthUri)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(otpauthUri, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(6);
    }

    // Verifies a 6-digit TOTP against the secret, allowing one 30s step of clock skew either way.
    public bool VerifyTotp(string secret, string code)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        var totp = new Totp(Base32Encoding.ToBytes(secret));
        return totp.VerifyTotp(code.Trim(), out _, new VerificationWindow(previous: 1, future: 1));
    }

    // A fresh set of one-time recovery codes as (plaintext shown once, hash stored).
    public IReadOnlyList<(string Plaintext, string Hash)> GenerateRecoveryCodes()
    {
        var codes = new List<(string, string)>(RecoveryCodeCount);
        for (var i = 0; i < RecoveryCodeCount; i++)
        {
            var plaintext = $"{RandomChunk(5)}-{RandomChunk(5)}";
            codes.Add((plaintext, HashRecoveryCode(plaintext)));
        }

        return codes;
    }

    // SHA-256 hex of the normalized code — recovery codes are high-entropy random values, so an unsalted fast
    // hash is the standard choice (unlike passwords) and lets the login path match by direct lookup.
    public string HashRecoveryCode(string code)
    {
        var normalized = NormalizeRecoveryCode(code);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(bytes);
    }

    // Strips spacing/dashes and lowercases so display formatting doesn't affect matching.
    public static string NormalizeRecoveryCode(string code) =>
        new(code.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string RandomChunk(int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = RecoveryAlphabet[RandomNumberGenerator.GetInt32(RecoveryAlphabet.Length)];
        }

        return new string(chars);
    }
}
