using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The desktop half of the self-service S/MIME certificate (#1332, ADR 0816): the ProfileClient area the
// SmimeDialog rides — status via the me rel, generate (artifacts shown once), upload of the public half,
// the set-blocks-both state machine, and delete, all through the resource's advertised self rel. The
// mutation is restored (delete) before the test ends, the sibling IMAP test's own pattern.
[Collection(UiCollection.Name)]
public class DesktopSmimeCertificateTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopSmimeCertificateTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Smime_generates_blocks_while_set_uploads_and_deletes_through_advertised_rels()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var status = await api.Profile.GetSmimeAsync();
        Assert.True(status.SelfService); // the fixture's tenant is not encryption-service-gated
        Assert.False(status.Enabled);

        // Generate: the p12 opens with the typed password and the state flips.
        var generated = await api.Profile.GenerateSmimeIdentityAsync(status, "desk-secret");
        Assert.True(generated.Enabled);
        var identity = X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(generated.Pkcs12!), "desk-secret");
        Assert.True(identity.HasPrivateKey);
        Assert.NotNull(generated.MobileConfig);

        // Set blocks BOTH entrances until delete — the dialog's disabled buttons are the server's rule too.
        await Assert.ThrowsAsync<HttpRequestException>(
            () => api.Profile.GenerateSmimeIdentityAsync(generated, "again"));
        await Assert.ThrowsAsync<HttpRequestException>(
            () => api.Profile.UploadSmimeCertificateAsync(generated, [1, 2, 3]));

        await api.Profile.DeleteSmimeCertificateAsync(generated);
        Assert.False((await api.Profile.GetSmimeAsync()).Enabled);

        // Upload the PUBLIC half of a locally generated identity; subject reflects it; then restore.
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=desktop-upload@e2e.local", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var uploaded = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var afterUpload = await api.Profile.UploadSmimeCertificateAsync(
            await api.Profile.GetSmimeAsync(),
            System.Text.Encoding.ASCII.GetBytes(uploaded.ExportCertificatePem()));
        Assert.True(afterUpload.Enabled);
        Assert.Contains("desktop-upload@e2e.local", afterUpload.Subject);

        await api.Profile.DeleteSmimeCertificateAsync(afterUpload);
        Assert.False((await api.Profile.GetSmimeAsync()).Enabled);
    }
}
