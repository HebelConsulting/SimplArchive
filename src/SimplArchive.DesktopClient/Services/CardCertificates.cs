using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using CAManagement.Pkcs11;
using CAManagement.Pkcs11.Configuration;
using CAManagement.Pkcs11.DataStructures;
using CAManagement.Pkcs11.Extensions;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// Reads the certificates a PKCS#11 device carries, so one can be registered as the caller's own (#1398).
/// </summary>
/// <remarks>
/// <para>
/// The third way to register a certificate, beside uploading a <c>.pem</c> and generating an identity: point at
/// the token and register what is already on it. Only the PUBLIC half travels — the private key never leaves the
/// device, which is the entire reason to want this.
/// </para>
/// <para>
/// <b>The unit to walk is a SLOT, not "the card".</b> A PIV token has four key slots and may carry a certificate
/// in each; a non-PIV token may carry any number under any labels; and a user may have two readers plugged in.
/// So this enumerates every slot with a token present and every certificate on each, and lets the caller choose.
/// Searching by a known label — which is what the hardware proof did — would hard-code one vendor's slot naming
/// and show NOTHING on a token that names things differently.
/// </para>
/// <para>
/// <b>No PIN is collected, because none is needed.</b> Certificates are public objects: measured on a YubiKey,
/// enumerating them succeeds with no login. The same measurement is why this cannot report whether a matching
/// private key is present — <c>CKO_PRIVATE_KEY</c> objects stay invisible until <c>C_Login</c>, so asking would
/// mean a PIN prompt to answer a question the user did not ask. Registering a card certificate therefore carries
/// the same trust as uploading a <c>.pem</c>: the holder asserts it is theirs.
/// </para>
/// </remarks>
public static class CardCertificates
{
    /// <summary>A certificate found on a token, with what the user needs to recognise it.</summary>
    public sealed record Found(
        string TokenLabel,
        string TokenSerial,
        string ObjectLabel,
        X509Certificate2 Certificate)
    {
        /// <summary>The certificate in the PEM form the server registers.</summary>
        public string Pem => new(System.Security.Cryptography.PemEncoding.Write("CERTIFICATE", Certificate.RawData));
    }

    /// <summary>Set by the Browse fallback when the module is not where it is usually installed.</summary>
    public static string? ModulePathOverride { get; set; }

    /// <summary>Replaced in tests; the real one walks the token.</summary>
    public static Func<string, IReadOnlyList<Found>> Reader { get; set; } = ReadFromToken;

    /// <summary>
    /// Where OpenSC puts its module on each platform, in probe order.
    /// </summary>
    /// <remarks>
    /// Probing rather than asking, because a file picker as the FIRST step means a user has to know what
    /// <c>opensc-pkcs11</c> is and where their platform put it — which for a showcase feature is most of the
    /// reason nobody would try it. The Browse fallback keeps the case where probing fails answerable.
    /// </remarks>
    public static IReadOnlyList<string> ModuleCandidates()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return
            [
                "/opt/homebrew/lib/opensc-pkcs11.so",   // Homebrew on Apple silicon
                "/usr/local/lib/opensc-pkcs11.so",      // Homebrew on Intel
                "/Library/OpenSC/lib/opensc-pkcs11.so", // the OpenSC .pkg installer
            ];
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return
            [
                @"C:\Program Files\OpenSC Project\OpenSC\pkcs11\opensc-pkcs11.dll",
                @"C:\Program Files (x86)\OpenSC Project\OpenSC\pkcs11\opensc-pkcs11.dll",
            ];
        }

        return
        [
            "/usr/lib/x86_64-linux-gnu/opensc-pkcs11.so", // Debian/Ubuntu
            "/usr/lib64/opensc-pkcs11.so",                // Fedora/RHEL
            "/usr/lib/opensc-pkcs11.so",
            "/usr/lib/pkcs11/opensc-pkcs11.so",
        ];
    }

    /// <summary>The module to load, or null when none of the usual places has one.</summary>
    public static string? FindModule() =>
        ModulePathOverride is { Length: > 0 } chosen && File.Exists(chosen)
            ? chosen
            : ModuleCandidates().FirstOrDefault(File.Exists);

    /// <summary>Every certificate on every token currently present.</summary>
    public static IReadOnlyList<Found> Read(string modulePath) => Reader(modulePath);

    /// <summary>What is worth telling the user about a certificate before they register it.</summary>
    public enum Concerns
    {
        /// <summary>Nothing to say.</summary>
        None,

        /// <summary>Past its validity — the server will refuse it.</summary>
        Expired,

        /// <summary>Not yet valid.</summary>
        NotYetValid,

        /// <summary>Its stated key usage does not mention key encipherment.</summary>
        KeyUsage,
    }

    /// <summary>
    /// Why this certificate may not work as an envelope recipient — advisory, never a refusal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An enum rather than a message, so the wording is translated with everything else rather than being
    /// English sentences compiled into a service.
    /// </para>
    /// <para>
    /// <b>A keyUsage mismatch is a WARNING on purpose.</b> The card this was built against carries a Key
    /// Management certificate stating <c>DigitalSignature</c> only, and it decrypts a real CMS envelope anyway:
    /// neither <c>EnvelopedCms</c> nor the token enforces keyUsage. A filter that refused on those grounds would
    /// refuse a certificate measured to work, so the user is told and left to decide. <c>Expired</c> is
    /// different in kind — the server itself refuses those — but it is still reported rather than hidden, so a
    /// user whose card has lapsed learns why instead of finding an empty list.
    /// </para>
    /// </remarks>
    public static Concerns Concern(X509Certificate2 certificate, DateTimeOffset now) =>
        certificate.NotAfter.ToUniversalTime() < now.UtcDateTime ? Concerns.Expired
        : certificate.NotBefore.ToUniversalTime() > now.UtcDateTime ? Concerns.NotYetValid
        : certificate.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault() is { } usage
            && !usage.KeyUsages.HasFlag(X509KeyUsageFlags.KeyEncipherment)
            && !usage.KeyUsages.HasFlag(X509KeyUsageFlags.DataEncipherment)
                ? Concerns.KeyUsage
                : Concerns.None;

    private static IReadOnlyList<Found> ReadFromToken(string modulePath)
    {
        var found = new List<Found>();
        using var library = new Pkcs11Library(new Pkcs11Options { ModulePath = modulePath });

        foreach (var slot in library.GetSlotList(tokenPresent: true))
        {
            // GUARDED PER SLOT, because a user may have two readers and only one of them a problem. Letting one
            // unreadable token abort the walk would report "the device could not be read" while a perfectly good
            // card sat in the other reader, with no way for the user to tell which was which.
            //
            // Written inline rather than as a helper taking the slot: a slot id is a CK_ULONG, whose CLR type is
            // `uint` on Windows and `ulong` everywhere else, so any signature naming it compiles for one target
            // and not the other. The compiler catches that — this project multi-targets for exactly this reason
            // (ADR 0831) — but the fix is to never name the type, not to spell it twice.
            try
            {
                var token = library.GetTokenInfo(slot);

                // Read-only, and no Login: certificates are public objects, so a PIN would be collected for
                // nothing — and asking for one would also be the only way to learn whether a matching private
                // key is present, which is a question the user did not ask.
                using var session = library.OpenSession(slot, readWrite: false);

                foreach (var handle in session.FindObjects(CK_OBJECT_CLASS.CKO_CERTIFICATE))
                {
                    X509Certificate2 certificate;
                    string label;
                    try
                    {
                        certificate = X509CertificateLoader.LoadCertificate(
                            session.GetAttributeValue(handle, CK_ATTRIBUTE_TYPE.CKA_VALUE));
                        label = session.GetLabel(handle);
                    }
                    catch (Exception)
                    {
                        // An object that says it is a certificate and does not parse is the token's problem, not
                        // a reason to abandon the ones that do — a user with three certificates should not lose
                        // all three to one bad object.
                        continue;
                    }

                    found.Add(new Found(
                        token.Label.AsPkcs11String().Trim(),
                        token.SerialNumber.AsPkcs11String().Trim(),
                        label,
                        certificate));
                }
            }
            catch (Exception e)
            {
                DesktopLog.Warn(e, "Reading a slot of the PKCS#11 module at {Module} failed", modulePath);
            }
        }

        return found;
    }
}
