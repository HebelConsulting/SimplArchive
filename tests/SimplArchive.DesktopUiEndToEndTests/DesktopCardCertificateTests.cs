using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// Reading a certificate off a card or token, as the third way to register one (#1398, ADR 0831).
//
// WHAT CAN AND CANNOT BE TESTED HERE. No test has a card, so the PKCS#11 walk itself is verified by the
// `--card-test` hook against real hardware and by nothing else — pretending otherwise with a fake would prove
// the client agrees with the fake. What IS testable is everything around it: where the module is looked for,
// what the classifier says about a certificate, and that the seam is a seam.
//
// The classifier is the part worth pinning, because its most important behaviour is a NEGATIVE one: it must
// not refuse a certificate whose stated key usage omits key encipherment. The card this was built against
// carries exactly such a certificate and decrypts a real CMS envelope anyway, so a guard that "helpfully"
// filtered those out would reject hardware measured to work.
// IN NO COLLECTION, and that is checked rather than assumed — #1401 was filed the same day about exactly this
// hazard. These tests do mutate process-global statics, but they are the ONLY class that touches
// CardCertificates.Reader or ModulePathOverride, and xUnit runs a class's own methods sequentially. So there is
// nothing to race with. If another class ever starts setting either, this needs to join it in one collection.
[Collection(UiCollection.Name)]
public class DesktopCardCertificateTests : IDisposable
{
    private readonly Func<string, IReadOnlyList<CardCertificates.Found>> _originalReader = CardCertificates.Reader;
    private readonly string? _originalOverride = CardCertificates.ModulePathOverride;

    public void Dispose()
    {
        CardCertificates.Reader = _originalReader;
        CardCertificates.ModulePathOverride = _originalOverride;
    }

    [Fact]
    public void A_key_usage_that_omits_encipherment_is_a_NOTE_and_never_a_refusal()
    {
        // The measured case: a YubiKey PIV Key Management certificate issued by our own CA states
        // DigitalSignature only, and opens a real envelope regardless — neither EnvelopedCms nor the token
        // enforces keyUsage. So this reports a concern; it must never be turned into a filter.
        var certificate = NewCertificate(X509KeyUsageFlags.DigitalSignature);

        Assert.Equal(CardCertificates.Concerns.KeyUsage,
            CardCertificates.Concern(certificate, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void A_certificate_that_states_key_encipherment_raises_nothing()
    {
        var certificate = NewCertificate(X509KeyUsageFlags.KeyEncipherment);

        Assert.Equal(CardCertificates.Concerns.None,
            CardCertificates.Concern(certificate, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void A_certificate_with_no_stated_key_usage_raises_nothing()
    {
        // No extension means no claim, which is not the same as a claim that excludes us. Refusing here would
        // reject the plainest certificates there are.
        var certificate = NewCertificate(usage: null);

        Assert.Equal(CardCertificates.Concerns.None,
            CardCertificates.Concern(certificate, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData(-400, CardCertificates.Concerns.Expired)]
    [InlineData(400, CardCertificates.Concerns.NotYetValid)]
    public void Validity_is_reported_before_key_usage(int daysFromNow, CardCertificates.Concerns expected)
    {
        // Order matters: an expired certificate that ALSO has an unhelpful key usage should say it is expired,
        // because that is the one the server will actually refuse.
        var now = DateTimeOffset.UtcNow;
        var certificate = NewCertificate(X509KeyUsageFlags.DigitalSignature,
            notBefore: now.AddDays(daysFromNow - 1), notAfter: now.AddDays(daysFromNow));

        Assert.Equal(expected, CardCertificates.Concern(certificate, now));
    }

    [Fact]
    public void The_module_is_looked_for_where_this_platform_puts_it()
    {
        // Not an assertion about THIS machine — a candidate list that quietly became empty on some platform
        // would make the feature silently unavailable there, which is exactly the failure the Browse fallback
        // exists to rescue and nobody would notice.
        var candidates = CardCertificates.ModuleCandidates();

        Assert.NotEmpty(candidates);
        Assert.All(candidates, path => Assert.True(Path.IsPathRooted(path), $"not an absolute path: {path}"));

        var expected = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".dll" : ".so";
        Assert.All(candidates, path => Assert.Equal(expected, Path.GetExtension(path)));
    }

    [Fact]
    public void A_chosen_module_path_wins_over_the_probe_but_only_if_it_exists()
    {
        // The Browse fallback's contract. A remembered path whose file has gone — an uninstall, an external
        // drive — must fall back to probing rather than making the feature permanently broken.
        var real = Path.Combine(Path.GetTempPath(), $"sa-module-{Guid.NewGuid():N}.so");
        File.WriteAllText(real, "not really a module");
        try
        {
            CardCertificates.ModulePathOverride = real;
            Assert.Equal(real, CardCertificates.FindModule());

            CardCertificates.ModulePathOverride = Path.Combine(Path.GetTempPath(), $"gone-{Guid.NewGuid():N}.so");
            Assert.NotEqual(CardCertificates.ModulePathOverride, CardCertificates.FindModule());
        }
        finally
        {
            File.Delete(real);
        }
    }

    [Fact]
    public void The_reader_is_a_seam_so_the_dialog_can_be_exercised_without_hardware()
    {
        // Anti-vacuous for the tests above: they replace nothing, so this is what proves the walk is reachable
        // through a seam at all rather than hard-wired into the dialog.
        var certificate = NewCertificate(X509KeyUsageFlags.KeyEncipherment);
        CardCertificates.Reader = _ =>
            [new CardCertificates.Found("Test token", "0001", "Certificate for Key Management", certificate)];

        var found = Assert.Single(CardCertificates.Read("/nowhere/opensc-pkcs11.so"));

        Assert.Equal("Test token", found.TokenLabel);
        Assert.Contains("-----BEGIN CERTIFICATE-----", found.Pem, StringComparison.Ordinal);

        // The PEM is what gets registered, so it has to round-trip back to the same certificate — a wrong
        // encoding here would register something the server parses into a different key.
        Assert.Equal(certificate.RawData, X509Certificate2.CreateFromPem(found.Pem).RawData);
    }

    private static X509Certificate2 NewCertificate(
        X509KeyUsageFlags? usage, DateTimeOffset? notBefore = null, DateTimeOffset? notAfter = null)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=card-holder", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (usage is { } flags)
        {
            request.CertificateExtensions.Add(new X509KeyUsageExtension(flags, critical: false));
        }

        return request.CreateSelfSigned(
            notBefore ?? DateTimeOffset.UtcNow.AddDays(-1),
            notAfter ?? DateTimeOffset.UtcNow.AddYears(1));
    }
}
