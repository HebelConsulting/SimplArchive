using System.Security.Cryptography;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.DesktopUiEndToEndTests;

// Opening an ELLIPTIC-CURVE CMS recipient — the path that did not exist, because the opener reached for
// RecipientInfos[0].EncryptedKey and decrypted it. Meaningful for RSA, where the content key is wrapped TO the
// public key; meaningless for an EC recipient, where a key-encryption key is AGREED and EncryptedKey holds the
// content key wrapped under THAT.
//
// The envelope below is a FIXTURE PRODUCED BY OPENSSL, and that is the point rather than a convenience.
//
// It cannot be produced by .NET here at all: EnvelopedCms's managed PAL refuses to CREATE an EC recipient,
// answering "Unknown algorithm 1.2.840.10045.2.1" on Unix. But even where it works, a round trip against our
// own writer would have proved nothing — it agrees with whatever this code assumed. Two real bugs were found
// exactly this way, and both were invisible to a self-consistent test:
//
//   * the key-wrap OID is NESTED inside the key-encryption algorithm's parameters, so reading the outer OID
//     yields the KDF scheme and every envelope looked like it used an unimplemented wrap algorithm;
//   * the KDF hash is named by that outer scheme, and OpenSSL emits sha1kdf — so assuming SHA-256 derived the
//     wrong key against the most likely interoperating producer there is.
//
// Neither surfaced as a malformed structure. RFC 3394's integrity check turned both into loud failures rather
// than a document full of noise, which is the property that makes hand-written unwrapping defensible.
public class KeyAgreementEnvelopeTests
{
    // openssl cms -encrypt -aes256 -recip <P-256 certificate, keyUsage=keyAgreement> -in plain.txt -outform DER
    private const string EnvelopeBase64 =
        "MIIBoQYJKoZIhvcNAQcDoIIBkjCCAY4CAQIxggEpoYIBJQIBA6BRoU8wCQYHKoZIzj0CAQNCAAQ9C7uKei4cwOt2UzKzh0af"
        + "gTYPnbbvJWkUYQuCmeT2ke15ZoTn5J8+ozWh75l5+FhcZmWDN7iw0t37FVB7qxjKMBgGCSuBBRCGSD8AAjALBglghkgBZQME"
        + "AS0wgbIwga8wgYIwajELMAkGA1UEBhMCQ0gxHjAcBgNVBAoMFUhlYmVsIENvbnN1bHRpbmcgR21iSDEVMBMGA1UECwwMU2lt"
        + "cGxBcmNoaXZlMSQwIgYDVQQDDBtjcnlwdEBkZW1vLnNpbXBsYXJjaGl2ZS5kZXYCFB3pxrso0aJBp2x2bTR3t1X3rQxUBChQ"
        + "kMZFGOcDeQuq/U3egcMmL3A2CK6ER4YkKsH7KpOavDLn+3dUdeoPMFwGCSqGSIb3DQEHATAdBglghkgBZQMEASoEEEiPxGqp"
        + "p3YymLr8F+SJ28WAMIm4+6NlRg1PLfgkYkqk7LQ2cvc/RECzF2PC43G2Uefm7p0a3dlfi6leUA9PtEiUBQ==";

    // The matching PKCS#8 private key — a fixture, never a real key. Its certificate was self-signed, which
    // openssl permits because one EC key can do ECDSA as well as ECDH; a CARD cannot, which is why the hardware
    // check's certificate had to be CA-issued.
    private const string PrivateKeyBase64 =
        "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgpFzGA7kNncZ+kpo0n+d2E8LHLgS+xYSC57SzWLPsQTOhRANC"
        + "AAR7sBNo9cElA0NDugxlbE08CF9enDtN6SqwUUGlZjDxIOrG/+mRR5NJCe2ZdpfYzpG4UkbJh8I+Alq0uFU/xCxc";

    private const string Expected = "The quick brown fox jumps over the lazy dog.";

    [Fact]
    public void Opens_an_EC_recipients_envelope_produced_by_openssl()
    {
        var opened = CardEnvelopeOpener.OpenWithAgreement(Envelope(), Agree);

        Assert.Equal(Expected, System.Text.Encoding.UTF8.GetString(opened).TrimEnd());
    }

    [Fact]
    public void The_nested_key_wrap_OID_is_read_and_not_the_KDF_scheme()
    {
        var (point, ukm, kdfScheme, wrapOid) = CardEnvelopeOpener.ReadKeyAgreement(Envelope());

        // The outer OID names the KDF hash, the inner names the wrap. Swapping them is the bug this pins.
        Assert.Equal("1.3.133.16.840.63.0.2", kdfScheme);       // dhSinglePass-stdDH-sha1kdf-scheme
        Assert.Equal("2.16.840.1.101.3.4.1.45", wrapOid);       // id-aes256-wrap
        Assert.Null(ukm);

        // Uncompressed 04||X||Y straight out of the BIT STRING — NOT a DER SubjectPublicKeyInfo, which is the
        // form CK_ECDH1_DERIVE_PARAMS refuses.
        Assert.Equal(0x04, point[0]);
        Assert.Equal(65, point.Length);
    }

    [Fact]
    public void A_wrong_private_key_fails_the_integrity_check_rather_than_returning_noise()
    {
        using var stranger = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        // EXHAUSTION, not one failed unwrap. The opener tries both encodings of `keyInfo` since #1498 —
        // producers disagree about whether the key-wrap AlgorithmIdentifier carries an explicit NULL — so a
        // wrong key must fail BOTH and refuse, never return one attempt's bytes. The message says which,
        // so a reader is not sent hunting an encoding problem when the answer is that it is not their key.
        var refusal = Assert.Throws<EnvelopeNotOpenedException>(
            () => CardEnvelopeOpener.OpenWithAgreement(Envelope(), point => Agree(stranger, point)));

        Assert.Contains("either encoding", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_shared_info_tags_supp_pub_info_as_2_and_omits_parameters()
    {
        // Two mistakes that encode perfectly and derive the wrong key, so neither surfaces as a malformed
        // structure: tagging suppPubInfo [1] — there is no [1], the tags are 0 and 2 — and writing an explicit
        // NULL for the wrap algorithm's parameters, which RFC 3565 says are absent.
        var encoded = CardEnvelopeOpener.EccCmsSharedInfo("2.16.840.1.101.3.4.1.45", ukm: null, kekLengthBits: 256);

        var outer = new System.Formats.Asn1.AsnReader(encoded, System.Formats.Asn1.AsnEncodingRules.DER).ReadSequence();
        var keyInfo = outer.ReadSequence();
        Assert.Equal("2.16.840.1.101.3.4.1.45", keyInfo.ReadObjectIdentifier());
        Assert.False(keyInfo.HasData);

        var supp = outer.ReadSequence(new System.Formats.Asn1.Asn1Tag(
            System.Formats.Asn1.TagClass.ContextSpecific, 2, isConstructed: true));
        Assert.Equal(new byte[] { 0, 0, 1, 0 }, supp.ReadOctetString());
    }

    [Fact]
    public void User_keying_material_changes_the_derived_key()
    {
        // It is a KDF input, and .NET does not expose it on KeyAgreeRecipientInfo at all — which is why the
        // reader goes to the raw DER. Ignoring one would still encode fine; only the derived key would differ.
        Assert.NotEqual(
            CardEnvelopeOpener.EccCmsSharedInfo("2.16.840.1.101.3.4.1.45", [1, 2, 3, 4], 256),
            CardEnvelopeOpener.EccCmsSharedInfo("2.16.840.1.101.3.4.1.45", ukm: null, 256));
    }

    // --- harness ----------------------------------------------------------------------------------------

    private static byte[] Envelope() => Convert.FromBase64String(EnvelopeBase64);

    /// <summary>The recipient's half of the agreement, standing in for the card.</summary>
    private static byte[] Agree(byte[] uncompressedPoint)
    {
        using var key = ECDiffieHellman.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(PrivateKeyBase64), out _);
        return Agree(key, uncompressedPoint);
    }

    private static byte[] Agree(ECDiffieHellman key, byte[] uncompressedPoint)
    {
        var half = (uncompressedPoint.Length - 1) / 2;
        using var originator = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = uncompressedPoint[1..(1 + half)], Y = uncompressedPoint[(1 + half)..] },
        });

        // RAW agreement — the unhashed Z, which is what a token returns under CKD_NULL and what the X9.63 KDF
        // consumes. DeriveKeyMaterial would hash it first and derive a different key.
        return key.DeriveRawSecretAgreement(originator.PublicKey);
    }
}
