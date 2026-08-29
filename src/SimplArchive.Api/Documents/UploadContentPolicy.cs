namespace SimplArchive.Api.Documents;

/// <summary>Why an upload was refused. The reason is shown to the person who uploaded it.</summary>
public readonly record struct UploadRefusal(string Reason);

/// <summary>
/// What may be stored in the archive (ADR 0718, superseding ADR 0123's allowlist). A BLOCKLIST of things a
/// mainstream operating system executes, not an allowlist of document types: an archive that refuses the CAD
/// file, the disk image or the ZIP a customer wants to keep has failed at its job, and the security difference
/// is small — a ZIP carrying an executable passes either rule, since nothing here unpacks it.
/// </summary>
/// <remarks>
/// <para>
/// Two directions, because each catches what the other cannot. The <b>bytes</b> catch an executable wearing a
/// document's name — a `.pdf` on a Windows binary — which is the disguise that matters. The <b>extension</b>
/// catches a script, which is plain text and has no signature to find: a `.bat` is dangerous because of what
/// Windows does with the name, not because of anything in the file.
/// </para>
/// <para>
/// This is a content-type policy, not a scanning capability (ADR 0123's own words). It does not look inside an
/// allowed format, and it never claims to.
/// </para>
/// </remarks>
public static class UploadContentPolicy
{
    /// <summary>How much of the file the sniff needs. Every signature below lives in the first few bytes.</summary>
    public const int HeadBytes = 64;

    // Executable IMAGE formats. An extension is a claim; these are what the file actually is.
    private static readonly (byte[] Magic, string What)[] ExecutableSignatures =
    [
        ([0x4D, 0x5A], "a Windows executable"),                          // MZ — .exe/.dll/.scr/.sys
        ([0x7F, 0x45, 0x4C, 0x46], "a Linux executable"),                // \x7FELF
        ([0xFE, 0xED, 0xFA, 0xCE], "a macOS executable"),                // Mach-O, 32-bit
        ([0xFE, 0xED, 0xFA, 0xCF], "a macOS executable"),                // Mach-O, 64-bit
        ([0xCE, 0xFA, 0xED, 0xFE], "a macOS executable"),                // Mach-O, byte-swapped
        ([0xCF, 0xFA, 0xED, 0xFE], "a macOS executable"),                // Mach-O, byte-swapped 64-bit
        ([0xCA, 0xFE, 0xBA, 0xBE], "compiled program code"),             // Mach-O fat binary AND a Java .class
        ([0x23, 0x21], "a script"),                                      // #! — a shebang is an instruction to run it
    ];

    // Formats whose danger is in the NAME rather than the bytes: a batch file, a shell script and a registry
    // file are all plain text, so no signature exists to find. The line drawn here is "what a mainstream
    // operating system will run from a double-click", plus the shell and scripting-host languages.
    //
    // Deliberately NOT here: .dmg, .deb, .rpm, .apk and other package or disk-image formats. They need a
    // package manager or a mount rather than a click, and they are plausibly the very artefacts a company
    // archives. Deliberately IS here: .js, which costs somebody archiving web sources — the Windows scripting
    // host runs a .js from a double-click, and it has been an email-malware vector for two decades.
    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".scr", ".pif", ".cpl", ".msi", ".msp", ".msc",
        ".bat", ".cmd", ".ps1", ".psm1", ".psd1",
        ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta",
        ".lnk", ".scf", ".reg", ".jar",
        ".sh", ".bash", ".zsh", ".ksh", ".csh",
        ".dll", ".sys", ".drv", ".ocx",
    };

    /// <summary>
    /// Whether this content may be stored. <paramref name="head"/> is the first <see cref="HeadBytes"/> bytes
    /// (fewer for a short file); <paramref name="fileName"/> is any name carrying the extension — the object
    /// key will do, since it ends in the uploaded extension.
    /// </summary>
    public static UploadRefusal? Inspect(ReadOnlySpan<byte> head, string? fileName)
    {
        foreach (var (magic, what) in ExecutableSignatures)
        {
            if (head.StartsWith(magic))
            {
                return new UploadRefusal($"the content is {what}");
            }
        }

        var extension = fileName is null ? string.Empty : Path.GetExtension(fileName);

        return ExecutableExtensions.Contains(extension)
            ? new UploadRefusal($"{extension} files are executable and are not archived")
            : null;
    }
}
