using System.Security.Cryptography;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// The two primitives a CMS key-agreement recipient needs and .NET does not expose: the X9.63 KDF that turns
/// an ECDH shared secret into a key-encryption key, and the RFC 3394 AES key unwrap that recovers the
/// content-encryption key with it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written here rather than taken from a library, by decision.</b> Both are compositions of primitives .NET
/// already has — SHA-256 and AES-ECB — and both are pinned to published test vectors, which is the condition
/// under which hand-writing them is reasonable. The alternative was BouncyCastle for about sixty lines'
/// worth of use in a client that ships as a self-contained package.
/// </para>
/// <para>
/// <b>The unwrap VERIFIES, and that is what makes this safe to hand-write.</b> RFC 3394 prepends a known
/// integrity value (<c>A6A6A6A6A6A6A6A6</c>) before wrapping, so unwrapping with the wrong key — or a KEK
/// derived from a wrong shared secret, or a misparsed UKM — fails a comparison rather than producing plausible
/// bytes. A bug here is loud. That property is why the assertion order matters: never decrypt content with a
/// key whose unwrap did not check out.
/// </para>
/// </remarks>
internal static class KeyAgreementCrypto
{
    /// <summary>RFC 3394's fixed integrity check value, and the reason a wrong key fails loudly.</summary>
    private static readonly byte[] DefaultIv = [0xA6, 0xA6, 0xA6, 0xA6, 0xA6, 0xA6, 0xA6, 0xA6];

    /// <summary>
    /// The ANSI X9.63 KDF: <c>H(Z || counter || sharedInfo)</c>, concatenated until <paramref name="length"/>
    /// bytes are produced, with a big-endian counter starting at 1.
    /// </summary>
    /// <param name="hash">The hash the SENDER's KDF scheme names, never a default. OpenSSL emits
    /// <c>dhSinglePass-stdDH-sha1kdf-scheme</c> unless told otherwise, so assuming SHA-256 derives the wrong
    /// key for the most likely interoperating producer there is — which is how this parameter came to exist.</param>
    /// <remarks>
    /// The counter is where an implementation goes wrong, and it goes wrong invisibly for the common case: a
    /// 256-bit KEK from SHA-256 needs exactly ONE block, so a counter that never increments — or starts at 0 —
    /// produces the right answer for every key this code will meet in practice and the wrong one the first
    /// time somebody asks for AES-256-wrap with a longer output. Hence the multi-block test.
    /// </remarks>
    internal static byte[] DeriveKeyEncryptionKey(
        byte[] sharedSecret, byte[] sharedInfo, int length, HashAlgorithmName hash)
    {
        var derived = new byte[length];
        var written = 0;
        var counter = 1u;

        while (written < length)
        {
            var block = new byte[sharedSecret.Length + 4 + sharedInfo.Length];
            sharedSecret.CopyTo(block, 0);
            // Big-endian, per the standard — BitConverter is host-endian and would silently differ on a
            // little-endian machine, which is every machine this runs on, so it would be consistently wrong.
            block[sharedSecret.Length] = (byte)(counter >> 24);
            block[sharedSecret.Length + 1] = (byte)(counter >> 16);
            block[sharedSecret.Length + 2] = (byte)(counter >> 8);
            block[sharedSecret.Length + 3] = (byte)counter;
            sharedInfo.CopyTo(block, sharedSecret.Length + 4);

            var digest = CryptographicOperations.HashData(hash, block);
            var take = Math.Min(digest.Length, length - written);
            digest.AsSpan(0, take).CopyTo(derived.AsSpan(written));
            written += take;
            counter++;
        }

        return derived;
    }

    /// <summary>
    /// RFC 3394 AES key unwrap: recovers the wrapped key, or throws when the integrity check fails.
    /// </summary>
    /// <exception cref="CryptographicException">The unwrap did not verify — a wrong KEK, or a corrupted or
    /// misparsed wrapped key. Never returns wrong bytes instead.</exception>
    internal static byte[] UnwrapKey(byte[] kek, byte[] wrapped)
    {
        if (wrapped.Length < 24 || wrapped.Length % 8 != 0)
        {
            throw new CryptographicException(
                $"a wrapped key is a multiple of 8 bytes and at least 24; this is {wrapped.Length}.");
        }

        using var aes = Aes.Create();
        aes.Key = kek;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;

        // n is the number of 8-byte blocks of PLAINTEXT; the wrapped form carries one extra for A.
        var n = (wrapped.Length / 8) - 1;
        var a = wrapped[..8];
        var r = new byte[n][];
        for (var i = 0; i < n; i++)
        {
            r[i] = wrapped[(8 * (i + 1))..(8 * (i + 2))];
        }

        var buffer = new byte[16];
        for (var j = 5; j >= 0; j--)
        {
            for (var i = n; i >= 1; i--)
            {
                // A XOR t, where t counts DOWN through the same sequence the wrap counted up. Getting the
                // index arithmetic wrong is the classic error here and it cannot pass the check below.
                var t = (uint)(n * j + i);
                a.CopyTo(buffer, 0);
                buffer[7] ^= (byte)t;
                buffer[6] ^= (byte)(t >> 8);
                buffer[5] ^= (byte)(t >> 16);
                buffer[4] ^= (byte)(t >> 24);
                r[i - 1].CopyTo(buffer, 8);

                var decrypted = aes.DecryptEcb(buffer, PaddingMode.None);
                a = decrypted[..8];
                r[i - 1] = decrypted[8..];
            }
        }

        // FIXED-TIME comparison, even though a wrapped content key is not a long-lived secret: the timing of a
        // failed unwrap is one of the few things an attacker who can feed us envelopes could measure.
        if (!CryptographicOperations.FixedTimeEquals(a, DefaultIv))
        {
            throw new CryptographicException(
                "the wrapped content key did not pass its integrity check, so the key-encryption key derived "
                + "from this agreement is not the one it was wrapped with.");
        }

        return r.SelectMany(block => block).ToArray();
    }
}
