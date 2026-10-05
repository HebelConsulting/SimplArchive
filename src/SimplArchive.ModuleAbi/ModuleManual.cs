namespace SimplArchive.ModuleAbi;

/// <summary>
/// The manual a module ships IN its package, which the core files into the archive (ABI 1.5, core ADR 0891).
/// </summary>
/// <remarks>
/// <para>
/// A module's manual used to reach a tenant by a side door: the flight-school demo seeder uploaded it, and the
/// Encryption Module's manual reached nobody at all unless an administrator found the PDF in its repository and
/// filed it by hand. Either way the copy in the archive drifted from the module actually running — an upgraded
/// module kept serving last release's manual, with nothing to say so.
/// </para>
/// <para>
/// So the module DECLARES the bytes and the core owns the rest: where it is filed (a per-module setting the core
/// adds to the module's form, unset meaning "not filed"), when (activation, saving that setting, every startup),
/// and how a newer manual arrives (as a NEW VERSION of the same document when its content hash differs — never a
/// second document). The module needs no archive rights for any of it.
/// </para>
/// <para>
/// <b>INIT-ONLY PROPERTIES, and that is not style.</b> Anything added later must be an init-only property —
/// never a new primary-constructor parameter, which changes the signature and makes a module compiled against
/// the older record die with <c>MissingMethodException</c> after loading successfully (ABI 0.21, see
/// <see cref="ModuleAbiVersion"/>). The two parameters below are the ones this cannot work without.
/// </para>
/// </remarks>
/// <param name="FileName">
/// The manual's file name, extension included (<c>SimplArchive-Encryption-Module-Manual.pdf</c>). The extension
/// becomes the filed version's; the stem is not the document's name — the core names it after the module.
/// </param>
/// <param name="Open">
/// Opens a fresh, readable stream over the manual's bytes — typically an embedded resource
/// (<c>() =&gt; typeof(MyModule).Assembly.GetManifestResourceStream("…")!</c>). Called once per filing check; the
/// core disposes the stream.
/// </param>
public sealed record ModuleManual(string FileName, Func<Stream> Open)
{
    /// <summary>The content type the filed version is stored with. Defaults to <c>application/pdf</c>, which is
    /// what a manual is; a module shipping another format says so.</summary>
    public string ContentType { get; init; } = "application/pdf";
}
