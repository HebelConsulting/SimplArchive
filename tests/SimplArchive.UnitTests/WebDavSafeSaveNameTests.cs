using SimplArchive.Api.WebDav;

namespace SimplArchive.UnitTests;

// The name rule for a word processor's macOS safe-save collection (#764). Worth pinning in the fast tier,
// because BOTH directions are costly and neither is visible at the call site:
//
//   too NARROW — the MKCOL is refused, the editor rolls back and DELETES the original file (unrecoverably in
//                the Intray, whose items are storage keys with no soft-delete);
//   too WIDE   — a real folder whose name happens to end this way is silently accepted and never created,
//                so the user's folder simply does not appear and nothing reports why.
public class WebDavSafeSaveNameTests
{
    [Theory]
    // The exact shapes observed on the kiosk, from two different saves.
    [InlineData("Fdsajfijsadfsadfkldsflköalkdfjlöadslköfas.docx.sb-dea8d513-ucgFm5")]
    [InlineData("Testdatei.docx.sb-dea8d513-HXEmVr")]
    [InlineData("report.pdf.sb-0a1b2c3d-Zz09")]
    public void A_safe_save_collection_is_recognised(string name)
        => Assert.True(WebDavClutter.IsSafeSaveTemp(name));

    [Theory]
    [InlineData("Invoice 2026.docx")]
    [InlineData("Contracts")]
    [InlineData("Silvan Zingg")]
    [InlineData("notes.sb")]                 // no hex-and-suffix pair
    [InlineData("quarterly.sb-report")]      // one segment only — a plausible real folder name
    [InlineData("data.sb-xyz-1")]            // "xyz" is not hex: the marker is a HEX id, not any word
    public void An_ordinary_name_is_left_alone(string name)
        => Assert.False(WebDavClutter.IsSafeSaveTemp(name));
}
