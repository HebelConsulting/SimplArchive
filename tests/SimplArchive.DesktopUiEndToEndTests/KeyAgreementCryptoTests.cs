using System.Security.Cryptography;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.DesktopUiEndToEndTests;

// The two primitives .NET does not expose, pinned to PUBLISHED vectors — which is the condition under which
// hand-writing them was reasonable rather than reckless.
//
// All six RFC 3394 vectors, not a representative two: they cover every KEK size (128/192/256) against every
// key-data size (128/192/256), and the index arithmetic in the unwrap is exactly the kind of code that is
// right for one shape and wrong for another.
public class KeyAgreementCryptoTests
{
    [Theory]
    // RFC 3394 §4.1 – 4.6, verbatim.
    [InlineData("000102030405060708090A0B0C0D0E0F", "00112233445566778899AABBCCDDEEFF",
        "1FA68B0A8112B447AEF34BD8FB5A7B829D3E862371D2CFE5")]
    [InlineData("000102030405060708090A0B0C0D0E0F1011121314151617", "00112233445566778899AABBCCDDEEFF",
        "96778B25AE6CA435F92B5B97C050AED2468AB8A17AD84E5D")]
    [InlineData("000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F", "00112233445566778899AABBCCDDEEFF",
        "64E8C3F9CE0F5BA263E9777905818A2A93C8191E7D6E8AE7")]
    [InlineData("000102030405060708090A0B0C0D0E0F1011121314151617", "00112233445566778899AABBCCDDEEFF0001020304050607",
        "031D33264E15D33268F24EC260743EDCE1C6C7DDEE725A936BA814915C6762D2")]
    [InlineData("000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F", "00112233445566778899AABBCCDDEEFF0001020304050607",
        "A8F9BC1612C68B3FF6E6F4FBE30E71E4769C8B80A32CB8958CD5D17D6B254DA1")]
    [InlineData("000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F",
        "00112233445566778899AABBCCDDEEFF000102030405060708090A0B0C0D0E0F",
        "28C9F404C4B810F4CBCCB35CFB87F8263F5786E2D80ED326CBC7F0E71A99F43BFB988B9B7A02DD21")]
    public void Unwraps_every_RFC_3394_vector(string kek, string expectedKey, string wrapped)
    {
        Assert.Equal(
            Convert.FromHexString(expectedKey),
            KeyAgreementCrypto.UnwrapKey(Convert.FromHexString(kek), Convert.FromHexString(wrapped)));
    }

    [Fact]
    public void A_wrong_key_encryption_key_FAILS_rather_than_returning_wrong_bytes()
    {
        // The property the whole hand-written approach rests on. RFC 3394 prepends a known integrity value, so
        // a KEK derived from the wrong shared secret — or a misparsed UKM, which is the likelier mistake —
        // cannot yield plausible content-key bytes that then decrypt a document into garbage.
        var wrong = Convert.FromHexString("0F0E0D0C0B0A09080706050403020100");
        var wrapped = Convert.FromHexString("1FA68B0A8112B447AEF34BD8FB5A7B829D3E862371D2CFE5");

        var refusal = Assert.Throws<CryptographicException>(() => KeyAgreementCrypto.UnwrapKey(wrong, wrapped));

        Assert.Contains("integrity check", refusal.Message);
    }

    [Theory]
    [InlineData(8)]     // shorter than the minimum: A plus one block is 24
    [InlineData(16)]
    [InlineData(28)]    // not a multiple of 8
    public void A_structurally_impossible_wrapped_key_is_refused_before_any_AES(int length)
    {
        var kek = Convert.FromHexString("000102030405060708090A0B0C0D0E0F");

        Assert.Throws<CryptographicException>(() => KeyAgreementCrypto.UnwrapKey(kek, new byte[length]));
    }

    [Fact]
    public void The_KDF_is_the_hash_of_Z_then_the_counter_then_the_shared_info()
    {
        // For a single block the X9.63 KDF IS this concatenation, so the expected value is built here by an
        // independent path rather than by calling the code under test. What is being checked is the ASSEMBLY —
        // the order of the three parts and the counter's width and endianness — which is all the KDF contains.
        var z = Convert.FromHexString("00112233445566778899AABBCCDDEEFF");
        var sharedInfo = Convert.FromHexString("A0B0C0D0");

        var expected = SHA256.HashData([.. z, 0x00, 0x00, 0x00, 0x01, .. sharedInfo]);

        Assert.Equal(expected, KeyAgreementCrypto.DeriveKeyEncryptionKey(z, sharedInfo, 32, HashAlgorithmName.SHA256));
    }

    [Fact]
    public void The_KDF_counter_INCREMENTS_past_one_block()
    {
        // The case a 256-bit KEK never exercises, which is why it is worth its own test: SHA-256 gives exactly
        // 32 bytes, so a counter that never increments — or starts at 0 — is correct for every key this code
        // will meet in practice and wrong the first time a longer output is asked for.
        var z = Convert.FromHexString("00112233445566778899AABBCCDDEEFF");
        var sharedInfo = Convert.FromHexString("A0B0C0D0");

        var expected = new byte[48];
        SHA256.HashData([.. z, 0x00, 0x00, 0x00, 0x01, .. sharedInfo]).CopyTo(expected, 0);
        SHA256.HashData([.. z, 0x00, 0x00, 0x00, 0x02, .. sharedInfo]).AsSpan(0, 16).CopyTo(expected.AsSpan(32));

        Assert.Equal(expected, KeyAgreementCrypto.DeriveKeyEncryptionKey(z, sharedInfo, 48, HashAlgorithmName.SHA256));
    }

    [Fact]
    public void The_HASH_is_the_senders_choice_and_changes_the_derived_key()
    {
        // OpenSSL emits dhSinglePass-stdDH-sha1kdf-scheme unless told otherwise, so a hard-coded SHA-256
        // derives the wrong key against the most likely interoperating producer there is — and the only
        // symptom is a failed integrity check with nothing naming the cause. This was a real bug, found by
        // opening an actual OpenSSL envelope rather than one of our own.
        var z = Convert.FromHexString("00112233445566778899AABBCCDDEEFF");
        var sharedInfo = Convert.FromHexString("A0B0C0D0");

        Assert.NotEqual(
            KeyAgreementCrypto.DeriveKeyEncryptionKey(z, sharedInfo, 32, HashAlgorithmName.SHA256),
            KeyAgreementCrypto.DeriveKeyEncryptionKey(z, sharedInfo, 32, HashAlgorithmName.SHA1));

        // SHA-1 gives 20 bytes, so a 32-byte KEK spans two blocks — the counter path, exercised by the shape
        // the real producer actually uses rather than only by a synthetic long output.
        var sha1Derived = KeyAgreementCrypto.DeriveKeyEncryptionKey(z, sharedInfo, 32, HashAlgorithmName.SHA1);
        var expected = new byte[32];
        SHA1.HashData([.. z, 0, 0, 0, 1, .. sharedInfo]).CopyTo(expected, 0);
        SHA1.HashData([.. z, 0, 0, 0, 2, .. sharedInfo]).AsSpan(0, 12).CopyTo(expected.AsSpan(20));
        Assert.Equal(expected, sha1Derived);
    }

    [Fact]
    public void Two_different_shared_infos_derive_different_keys()
    {
        // The shared info carries the key-wrap OID and the user keying material, so a KDF that ignored it would
        // derive the same KEK for two different messages — and every unwrap would still verify, because the
        // integrity check only proves the KEK matches what the sender used.
        var z = Convert.FromHexString("00112233445566778899AABBCCDDEEFF");

        Assert.NotEqual(
            KeyAgreementCrypto.DeriveKeyEncryptionKey(z, Convert.FromHexString("A0"), 32, HashAlgorithmName.SHA256),
            KeyAgreementCrypto.DeriveKeyEncryptionKey(z, Convert.FromHexString("B0"), 32, HashAlgorithmName.SHA256));
    }
}
