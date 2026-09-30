using SimplArchive.Api.Documents;
using SimplArchive.Infrastructure.Encryption;
using Microsoft.Extensions.Configuration;

namespace SimplArchive.UnitTests;

/// <summary>
/// Which content doors a version resource advertises, per tier and per reader (ADR 0865, #1512).
/// </summary>
/// <remarks>
/// <para>
/// <b>A unit test because the tier cannot be hosted.</b> The E2E factory declares its tenants' modes in
/// process-wide configuration read once at startup, and a <c>SealedDeliveryPermissive</c> entry would make
/// <c>EncryptionModes.ThrowIfSealedDeliveryHasNoModule</c> refuse to start unless a loaded module answers
/// <c>ReaderCertificates</c> — and making the test module answer it would make the module the ONLY
/// certificate source (ADR 0842), closing the core's own column and registry and breaking every existing
/// strict test that registers a certificate in one of them. So the decision was extracted to a pure function
/// and the table is asserted here, exhaustively.
/// </para>
/// <para>
/// The defect this pins was invisible for the opposite reason: every layer was individually correct, and the
/// tier's whole difference from <c>Strict</c> lived in one boolean that nothing compared.
/// </para>
/// </remarks>
public class ContentDoorsTests
{
    // Every combination of the three inputs, so a fourth tier cannot inherit an answer it was never given.
    [Theory]
    // delivers, certificate, refuses  →  enveloped, plaintext
    [InlineData(false, false, false, false, true)]  // None / Storage: the ordinary pair, nothing enveloped
    [InlineData(false, true, false, false, true)]   // …a certificate changes nothing where nothing delivers
    [InlineData(true, false, false, false, true)]   // permissive, reader with NO certificate: plaintext only
    [InlineData(true, true, false, true, true)]     // permissive, reader WITH one: BOTH — the bug
    [InlineData(true, true, true, true, false)]     // Strict: the envelope, and no plaintext door
    [InlineData(true, false, true, false, false)]   // Strict, no certificate: neither. Metadata still renders
    [InlineData(false, false, true, false, false)]  // a refusing tenant that delivers nothing: neither
    [InlineData(false, true, true, false, false)]   // …same, with a certificate nothing can use
    public void Each_combination_of_the_three_questions_has_an_explicit_answer(
        bool delivers, bool certificate, bool refuses, bool enveloped, bool plaintext)
    {
        var doors = ContentDoors.For(delivers, certificate, refuses);

        Assert.Equal(enveloped, doors.Enveloped);
        Assert.Equal(plaintext, doors.Plaintext);
    }

    [Fact]
    public void The_plaintext_pair_is_named_plain_only_when_it_stands_BESIDE_the_envelope()
    {
        // The naming is the compatibility half of ADR 0865: `download` keeps pointing at the enveloped route
        // on every delivering tenant, so the desktop needs no change, and the plaintext pair takes new names
        // ONLY where it is an addition. Where it is the only pair it must still be `download`/`preview`, or
        // every non-encrypting tenant in the installation would rename its content rels.
        var both = ContentDoors.For(deliversEnvelopes: true, hasReaderCertificate: true, refusesPlaintextDoors: false);
        Assert.Equal("plain-download", both.DownloadRel);
        Assert.Equal("plain-preview", both.PreviewRel);

        var ordinary = ContentDoors.For(deliversEnvelopes: false, hasReaderCertificate: false, refusesPlaintextDoors: false);
        Assert.Equal("download", ordinary.DownloadRel);
        Assert.Equal("preview", ordinary.PreviewRel);

        // A permissive tenant whose reader has no certificate has nothing to stand beside, so its single
        // pair keeps the plain names too — otherwise the rel a browser follows would depend on whether some
        // OTHER capability happened to be available to that reader.
        var noCertificate = ContentDoors.For(deliversEnvelopes: true, hasReaderCertificate: false, refusesPlaintextDoors: false);
        Assert.Equal("download", noCertificate.DownloadRel);
        Assert.Equal("preview", noCertificate.PreviewRel);
    }

    /// <summary>
    /// The named modes, asked through the real <see cref="EncryptionModes"/> rather than through booleans.
    /// </summary>
    /// <remarks>
    /// The theory above pins the decision; this pins that the two delivery tiers actually feed it differently,
    /// which is the fact the defect disproved. Asked of the real predicate pair, so a change to either
    /// membership question surfaces here rather than only in a live stack.
    /// </remarks>
    [Theory]
    [InlineData("Storage", false, true)]
    [InlineData("Strict", true, false)]
    [InlineData("SealedDeliveryPermissive", true, true)]
    [InlineData("SealedDeliveryStrict", true, false)]
    public void A_reader_with_a_certificate_gets_these_doors_per_mode(string mode, bool enveloped, bool plaintext)
    {
        var tenant = "Acme";
        var modes = new EncryptionModes(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{EncryptionModes.Section}:{tenant}"] = mode,
                [EncryptionModes.ServiceUrlKey] = "http://encryption.invalid",
            })
            .Build());

        var doors = ContentDoors.For(
            deliversEnvelopes: modes.DeliversEnvelopes(tenant),
            hasReaderCertificate: true,
            refusesPlaintextDoors: modes.RefusesPlaintextDoors(tenant));

        Assert.Equal(enveloped, doors.Enveloped);
        Assert.Equal(plaintext, doors.Plaintext);
    }

    [Fact]
    public void Permissive_is_the_ONLY_mode_that_serves_both_doors_at_once()
    {
        // Stated as its own assertion because it is the tier's definition, and because the regression it
        // guards was precisely that permissive served the same single door Strict does. If a later mode
        // joins this set, that is a decision someone made rather than a position it inherited.
        var both = Enum.GetNames<EncryptionMode>()
            .Where(name =>
            {
                var modes = new EncryptionModes(new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Encryption:DefaultMode"] = name,
                        [EncryptionModes.ServiceUrlKey] = "http://encryption.invalid",
                    })
                    .Build());
                var doors = ContentDoors.For(
                    modes.DeliversEnvelopes("Acme"), hasReaderCertificate: true,
                    refusesPlaintextDoors: modes.RefusesPlaintextDoors("Acme"));
                return doors is { Enveloped: true, Plaintext: true };
            })
            .ToList();

        Assert.Equal([nameof(EncryptionMode.SealedDeliveryPermissive)], both);
    }
}
