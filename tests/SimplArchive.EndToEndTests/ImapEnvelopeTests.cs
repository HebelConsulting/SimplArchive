using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using SimplArchive.Api.Imap;

namespace SimplArchive.EndToEndTests;

// IMAP and a reader with NO certificate: served plaintext, which is the stated contract rather than an error.
//
// This class used to drive the encryption service's `/enveloped` hook and its per-tenant gate. That hook, and
// the certificate registry behind it, are retired (ADR 0890): IMAP envelopes IN-PROCESS to the reader's
// certificates — the Module's, else the reader's own column — which SmimeSelfServiceTests proves with real CMS.
// What remains here is the no-certificate half, run on a tenant whose mode DOES envelope mail, so plaintext is
// served for the right reason.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class ImapEnvelopeTests
{
    private readonly E2EApiFactory _factory;

    public ImapEnvelopeTests(E2EApiFactory factory) => _factory = factory;

    // The CryptoDemo seed derives florian/thomas from the admin's domain (ADR 0813).
    private static string CryptoUserEmail(string localPart) =>
        $"{localPart}@{E2EApiFactory.CryptoAdminEmail.Split('@')[1]}";

    private async Task<string> FetchRawMessageAsync(string email, string imapPassword, string folderName)
    {
        var port = ((ImapServer)_factory.Services.GetService(typeof(ImapServer))!).BoundPort!.Value;
        using var client = new ImapClient();
        await client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None);
        await client.AuthenticateAsync(email, imapPassword);

        var folder = await client.GetFolderAsync(folderName);
        await folder.OpenAsync(FolderAccess.ReadOnly);
        Assert.True(folder.Count >= 1, $"expected at least one message in {folderName}, found {folder.Count}");
        using var message = await folder.GetMessageAsync(0);
        using var stream = new MemoryStream();
        await message.WriteToAsync(stream);
        await client.DisconnectAsync(quit: true);
        return Encoding.ASCII.GetString(stream.ToArray());
    }

    [Fact]
    public async Task A_recipient_without_a_certificate_gets_plaintext_in_the_listed_tenant()
    {
        // thomas is in the LISTED tenant but deliberately NOT registered — the stub answers 404, which is
        // the contract, not a failure. Running him through the listed tenant is what keeps this test
        // meaningful: an unlisted tenant would be served plaintext for the wrong reason.
        var email = CryptoUserEmail("thomas");

        // The seeded documents live in the repository's SampleData folder (ADR 0891), not at its root.
        var raw = await FetchRawMessageAsync(email, E2EApiFactory.CryptoPassword, $"{E2EApiFactory.CryptoTenantName}/SampleData");

        Assert.DoesNotContain("application/pkcs7-mime", raw, StringComparison.Ordinal);
        // Positively plaintext: the synthetic message serves the document as a PDF attachment part.
        Assert.Contains("application/pdf", raw, StringComparison.Ordinal);
    }
}
