using SimplArchive.Cli.Commands;
using SimplArchive.Cli.Infrastructure;

namespace SimplArchive.UnitTests;

/// <summary>
/// The CA manifest saconsole imports in bulk (#1501) — its literal wire shape, and the refusals.
/// </summary>
/// <remarks>
/// <para>
/// <b>The fixture below is a LITERAL of the file caconsole writes, on purpose.</b> The two repositories share
/// no package, so the shape cannot be taken from a common type — and a test that reads both sides from one
/// source agrees at every version while the wire disagrees. This is the SimplArchive side's statement of what
/// it accepts; CAManagement#21 carries the producer's.
/// </para>
/// <para>
/// The refusals are the substance. This is a bulk operation against real readers' access, so a file whose
/// tenth row is unusable must be rejected as a whole and fixed, not discovered after nine enrolments have
/// already happened.
/// </para>
/// </remarks>
public sealed class CertificateManifestTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"manifest-{Guid.NewGuid():N}.json");

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void The_manifest_caconsole_writes_is_read()
    {
        Write("""
            {
              "issued": [
                {
                  "holder": "anna@acme.test",
                  "label": "YubiKey 5C",
                  "serial": "2CEF6410C6745437",
                  "thumbprint": "CA5FF4EFFC791CDC",
                  "certificatePem": "-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----"
                }
              ],
              "revoked": [
                { "serial": "2334CB43CDEC7428", "at": "2026-09-30T19:36:21Z", "reason": "KeyCompromise" }
              ]
            }
            """);

        var manifest = CertificateManifest.Read(_path);

        var issued = Assert.Single(manifest.Issued);
        Assert.Equal("anna@acme.test", issued.Holder);
        Assert.Equal("YubiKey 5C", issued.Label);
        Assert.Contains("BEGIN CERTIFICATE", issued.CertificatePem, StringComparison.Ordinal);

        var revoked = Assert.Single(manifest.Revoked);
        Assert.Equal("2334CB43CDEC7428", revoked.Serial);
        Assert.Equal("KeyCompromise", revoked.Reason);
    }

    [Fact]
    public void A_field_this_tool_does_not_know_is_not_a_reason_to_refuse_the_file()
    {
        // The producer is a separate tool on its own release cycle. A strict reader would make every
        // caconsole addition a breaking change here, which is how two tools stop being upgradable apart.
        Write("""
            {
              "issued": [ { "holder": "a@b.test", "label": "x", "certificatePem": "-----BEGIN CERTIFICATE-----x",
                            "issuedBy": "a field from a later caconsole" } ],
              "revoked": []
            }
            """);

        Assert.Single(CertificateManifest.Read(_path).Issued);
    }

    [Fact]
    public void A_row_with_no_holder_refuses_the_whole_file_and_names_the_row()
    {
        Write("""
            { "issued": [ { "label": "x", "serial": "AB01", "certificatePem": "-----BEGIN CERTIFICATE-----x" } ] }
            """);

        var refusal = Assert.Throws<CliException>(() => CertificateManifest.Read(_path));

        Assert.Contains("issued[0]", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("AB01", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_PRIVATE_KEY_in_the_manifest_is_refused_before_anything_is_sent()
    {
        // The useful moment to say "that is not what you want to hand over" is before it leaves the machine.
        // The server refuses it too; this is the refusal an operator can act on, because the file is still
        // in front of them.
        Write("""
            { "issued": [ { "holder": "a@b.test", "label": "x",
                            "certificatePem": "-----BEGIN PRIVATE KEY-----\nAAA\n-----END PRIVATE KEY-----" } ] }
            """);

        var refusal = Assert.Throws<CliException>(() => CertificateManifest.Read(_path));

        Assert.Contains("PRIVATE KEY", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("destroyed", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_manifest_naming_nothing_is_refused_rather_than_reported_as_done()
    {
        // An empty file almost always means the export went wrong. Reporting "enrolled 0, revoked 0" and
        // exiting 0 would read as success.
        Write("""{ "issued": [], "revoked": [] }""");

        Assert.Throws<CliException>(() => CertificateManifest.Read(_path));
    }

    [Fact]
    public void An_unreadable_file_names_the_path_rather_than_the_parser()
    {
        Write("not json at all");

        var refusal = Assert.Throws<CliException>(() => CertificateManifest.Read(_path));

        Assert.Contains(_path, refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("23:34:CB:43", "2334CB43")]           // a CA tool's colon-separated rendering
    [InlineData("2334cb43", "2334CB43")]              // lower case
    [InlineData("0x2334CB43", "2334CB43")]            // a 0x prefix
    [InlineData("  2334CB43  ", "2334CB43")]          // surrounding whitespace
    public void A_serial_is_normalised_to_the_one_spelling_both_sides_use(string written, string expected)
    {
        Assert.Equal(expected, CertificateManifest.NormaliseSerial(written));
    }

    [Fact]
    public void LEADING_ZEROS_SURVIVE_normalisation()
    {
        // THE BUG THIS EXISTS FOR, caught before it shipped. A serial is an opaque octet string rendered as
        // hex, not a number: X509Certificate2.SerialNumber keeps every byte, and the test nano's really is
        // four zero bytes followed by the rest. Trimming them — the reflex, and what the first draft did —
        // would make that certificate's serial fail to match its own enrolment, so a revocation from the CA
        // would report "not found" against a card sitting in the reader.
        Assert.Equal(
            "00000000221971BD5836ACF3CABB0213B7F13E67",
            CertificateManifest.NormaliseSerial("00:00:00:00:22:19:71:bd:58:36:ac:f3:ca:bb:02:13:b7:f1:3e:67"));
    }

    private void Write(string json) => File.WriteAllText(_path, json);
}
