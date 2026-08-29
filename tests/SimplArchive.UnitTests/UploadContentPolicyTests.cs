using System.Text;
using SimplArchive.Api.Documents;

namespace SimplArchive.UnitTests;

// What the archive will not store (ADR 0718, issue #846). ADR 0123 named this interim mitigation and it was
// never built: an .exe was accepted and stored, and nothing anywhere looked at what arrived.
//
// A BLOCKLIST rather than that ADR's allowlist, because an archive that refuses the CAD file or the disk image
// a customer wants to keep has failed at its job — while the security difference is small, since a ZIP carrying
// an executable passes either rule.
public class UploadContentPolicyTests
{
    private static byte[] Head(params byte[] magic) => [.. magic, .. Encoding.ASCII.GetBytes("the rest of the file")];

    [Theory]
    [InlineData("document.pdf")]     // the disguise that matters: a document's name on a program
    [InlineData("photo.jpg")]
    [InlineData("noextension")]
    public void A_windows_executable_is_refused_whatever_it_is_called(string name)
    {
        var refusal = UploadContentPolicy.Inspect(Head(0x4D, 0x5A, 0x90, 0x00), name);

        Assert.NotNull(refusal);
        Assert.Contains("Windows executable", refusal.Value.Reason);
    }

    [Theory]
    [InlineData(new byte[] { 0x7F, 0x45, 0x4C, 0x46 })]         // ELF
    [InlineData(new byte[] { 0xFE, 0xED, 0xFA, 0xCF })]         // Mach-O 64-bit
    [InlineData(new byte[] { 0xCA, 0xFE, 0xBA, 0xBE })]         // a fat binary, and a Java class
    [InlineData(new byte[] { 0x23, 0x21, 0x2F, 0x62 })]         // #!/b… — a shebang IS an instruction to run it
    public void Every_executable_image_format_is_refused(byte[] magic) =>
        Assert.NotNull(UploadContentPolicy.Inspect(Head(magic), "report.pdf"));

    [Theory]
    [InlineData("setup.exe")]
    [InlineData("SETUP.EXE")]        // the name is matched case-insensitively, like every file system that matters
    [InlineData("run.bat")]
    [InlineData("deploy.ps1")]
    [InlineData("payload.js")]
    [InlineData("install.sh")]
    [InlineData("tweak.reg")]
    [InlineData("shortcut.lnk")]
    public void A_script_is_refused_by_its_name_because_it_has_no_signature(string name)
    {
        // A .bat is plain text. There is nothing in the bytes to find — it is dangerous because of what the
        // operating system does with the NAME, which is why the extension half of the rule exists at all.
        var refusal = UploadContentPolicy.Inspect(Encoding.ASCII.GetBytes("echo hello"), name);

        Assert.NotNull(refusal);
    }

    [Theory]
    [InlineData("contract.pdf")]
    [InlineData("drawing.dwg")]      // CAD: exactly the file an allowlist of "document-ish" types would refuse
    [InlineData("backup.zip")]
    [InlineData("installer.dmg")]    // a disk image needs mounting, not a click — plausibly an archived artefact
    [InlineData("package.rpm")]
    [InlineData("notes.txt")]
    [InlineData("source.py")]        // source code is not run by a double-click on any mainstream desktop
    [InlineData("scan.tiff")]
    public void Everything_else_is_stored(string name) =>
        Assert.Null(UploadContentPolicy.Inspect(Encoding.ASCII.GetBytes("%PDF-1.7 or whatever"), name));

    [Fact]
    public void A_file_shorter_than_the_sniff_window_is_handled()
    {
        // ReadAtLeast gives back what there was; a two-byte file must not index past its own end.
        Assert.Null(UploadContentPolicy.Inspect([0x68, 0x69], "tiny.txt"));
        Assert.NotNull(UploadContentPolicy.Inspect([0x4D, 0x5A], "tiny.txt"));
    }

    [Fact]
    public void An_empty_file_is_stored()
    {
        // An empty upload is a different problem (and a legitimate one — a zero-byte file is a real file).
        // Refusing it HERE would be this rule answering a question it was not asked.
        Assert.Null(UploadContentPolicy.Inspect([], "empty.txt"));
    }
}
