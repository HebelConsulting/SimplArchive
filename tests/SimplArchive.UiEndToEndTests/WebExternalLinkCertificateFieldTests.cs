using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The web client offers the recipient-certificate field too (#1390, ADRs 0827/0511).
//
// The desktop is the reference and the web matches it, so the thing worth asserting is PARITY: both dialogs
// let a sharer address a link to a key. This fixture's tenant is not in the strict tier — nothing here
// configures encryption — so the field is OPTIONAL here, which is exactly the case that would be missed if the
// field were only rendered when the server demands it.
//
// That is the decision this test pins rather than merely exercises: the field is shown on every tenant,
// because the server accepts it on every tenant, and a control that appears and disappears with configuration
// the sharer cannot see is worse than one that is simply optional.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebExternalLinkCertificateFieldTests
{
    private readonly SelfHostedAppFixture _app;

    public WebExternalLinkCertificateFieldTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task The_create_dialog_offers_a_recipient_certificate_field()
    {
        var page = await Ui.LoginAsync(_app);

        await page.GetByText("Demo Repository").First.ClickAsync();
        foreach (var folder in new[] { "Contracts", "MyCountry Telekom" })
        {
            var row = page.Locator(".wb-list-row").Filter(new() { HasText = folder });
            await row.First.WaitForAsync(new() { Timeout = 15000 });
            await row.First.DblClickAsync();
        }

        var doc = page.Locator(".wb-list-row").Filter(new() { HasText = "service agreement" });
        await doc.First.WaitForAsync(new() { Timeout = 15000 });
        await doc.First.ClickAsync();

        await page.GetByRole(AriaRole.Button, new() { Name = "External links…" }).First.ClickAsync();
        var dialog = page.Locator(".mud-dialog").First;
        await dialog.WaitForAsync(new() { Timeout = 10000 });

        var field = dialog.GetByLabel("Recipient certificate (PEM)");
        await Expect(field).ToBeVisibleAsync(new() { Timeout = 10000 });

        // Pasting one describes it back — the sharer's only check on who they are addressing the document to,
        // and the same two fields the server's audit record names. Asserting the SUBJECT appears is what makes
        // this more than "a text box exists".
        // No blur needed, and that is the assertion: the field is Immediate, so the description appears while
        // the sharer is still looking at what they pasted. Without it the value commits on blur and a
        // description most people never see is no check at all.
        await field.FillAsync(Certificate);

        await Expect(dialog.GetByText("CN=parity@outside.example")).ToBeVisibleAsync(new() { Timeout = 10000 });
    }

    // A fixed self-signed certificate rather than one minted per run: this test asserts on the subject text,
    // and generating a certificate in a Playwright test would add a crypto dependency to a suite whose job is
    // the browser. Expiry is irrelevant — the client only reads the subject and the fingerprint, and ADR 0827
    // deliberately does not refuse an expired certificate (the private key outlives it).
    private const string Certificate = """
        -----BEGIN CERTIFICATE-----
        MIICwzCCAaugAwIBAgIJANrbNe15k7v2MA0GCSqGSIb3DQEBCwUAMCExHzAdBgNV
        BAMMFnBhcml0eUBvdXRzaWRlLmV4YW1wbGUwHhcNMjYwOTI0MTgxNzQ4WhcNNDYw
        OTI1MTgxNzQ4WjAhMR8wHQYDVQQDDBZwYXJpdHlAb3V0c2lkZS5leGFtcGxlMIIB
        IjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEA3Lcr8h3k71IUMS9ZpVcvsXRL
        J+gWdTVvji6MpgQ7zqCkMUrFWsfd5XmuggvcPisP0ETbu/l7GjRHDgB+88e1ZrWF
        Ksgi3mOF+mSwj1DZ/X7muWzCdhBAx2JZkatiLNP+qNOjitTif2QRT4PZEoZfuaCK
        Ce1izXSn/L/k1WAAwtaK/Sm3oMg+tBVV2Xqp+7S2TyVROBqiRdWaqzOLPsfZM/06
        mygGSp2PZeYL2hRC7/ytgJ7sr4GFFwx4VwtqqizfhlN/K7l5fKJeSVRqQoUzaU1G
        hC7tmBE6vxq2caQwUeKdwcjuJQ0iCKxr3IaD3GSHlHI5S3sMo7rqGeeMWBuobwID
        AQABMA0GCSqGSIb3DQEBCwUAA4IBAQCJrerIdEQPbr6WXRHMktqlFkbuctA2dnBj
        IfPqIiFoVBPZkHgf4VHx4rynZ9jXKNwlACMay5oQkHN2vDZHZNQn0BCYFUM0WpVe
        /kXXvySDP79KPu0TrHgmesehoH9cnyx1fJPzuYl2hcITyyivQWwUkrbWpFPYW6vb
        Kc9f12rUTCs+NbmJGMX9VPPqXldq7qU65SmT/KBee0UnMAMBkWs/ETeAzr0oL0l3
        2IiHJLpsnSQwzUHOTgNqP72hbA/nglZeZqJ0PV+JQy9Ata1ifF1nKNdp0LIExLyG
        w9IaRhbomoi1xoblZ8/NjYChJ/G12y6E/fZDUiL8OCbsrfoTQFhQ
        -----END CERTIFICATE-----
        """;
}
