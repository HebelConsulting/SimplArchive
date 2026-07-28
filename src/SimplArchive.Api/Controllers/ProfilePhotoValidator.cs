using System.Buffers.Binary;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// Validates an uploaded profile photo (ADR "User profile photo") with pure byte-parsing — no image
/// library. The clients normalize to a 256×256 PNG before upload; this is the server-side guard: the bytes
/// must be a PNG (8-byte signature + an IHDR chunk) with dimensions and size within sane caps.
/// </summary>
public static class ProfilePhotoValidator
{
    public const int MaxBytes = 1024 * 1024;   // 1 MB — a 256×256 PNG is far smaller; a generous guard.
    public const int MaxDimension = 1024;      // clients send 256×256; allow up to 1024 defensively.

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] Ihdr = [0x49, 0x48, 0x44, 0x52]; // "IHDR"

    public static bool IsValid(byte[] bytes, out string? error)
    {
        if (bytes.Length == 0)
        {
            error = "The photo is empty.";
            return false;
        }

        if (bytes.Length > MaxBytes)
        {
            error = "The photo is too large.";
            return false;
        }

        // Signature (0..8), then the first chunk must be IHDR: length (8..12), type "IHDR" (12..16),
        // width (16..20), height (20..24) — all we need to validate it's a real PNG with sane dimensions.
        if (bytes.Length < 24
            || !bytes.AsSpan(0, 8).SequenceEqual(PngSignature)
            || !bytes.AsSpan(12, 4).SequenceEqual(Ihdr))
        {
            error = "The photo must be a PNG image.";
            return false;
        }

        var width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));

        if (width is 0 or > MaxDimension || height is 0 or > MaxDimension)
        {
            error = "The photo dimensions are out of range.";
            return false;
        }

        error = null;
        return true;
    }
}
