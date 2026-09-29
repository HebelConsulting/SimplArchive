using CAManagement.Pkcs11;
using CAManagement.Pkcs11.Configuration;
using CAManagement.Pkcs11.DataStructures;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// Opens a CMS envelope with the card in the reader, and prints what happened — the verification that needs
/// hardware, invoked by <c>--card-envelope-test</c>.
/// </summary>
/// <remarks>
/// Deliberately prints each step rather than just a verdict. When a key agreement fails, the integrity check
/// is all that says so, and "the unwrap did not verify" is true of a wrong KDF hash, a wrong key-wrap OID, a
/// misread shared info and a wrong key alike — so the steps are worth seeing separately.
/// </remarks>
internal static class CardEnvelopeCheck
{
    internal static void Run(string envelopePath)
    {
        var raw = File.ReadAllBytes(envelopePath);
        Console.WriteLine($"envelope: {raw.Length} bytes from {envelopePath}");

        var (point, ukm, kdfScheme, wrapOid) = CardEnvelopeOpener.ReadKeyAgreement(raw);
        Console.WriteLine($"  originator point : {point.Length} bytes, first byte 0x{point[0]:X2}");
        Console.WriteLine($"  user keying mat. : {(ukm is null ? "(absent)" : $"{ukm.Length} bytes")}");
        Console.WriteLine($"  kdf scheme       : {kdfScheme}");
        Console.WriteLine($"  key wrap         : {wrapOid}");

        if (CardCertificates.FindModule() is not { } modulePath)
        {
            Console.WriteLine("REFUSED: no PKCS#11 module found.");
            return;
        }

        Console.WriteLine($"  module           : {modulePath}");

        using var library = new Pkcs11Library(new Pkcs11Options { ModulePath = modulePath });
        using var session = library.OpenSession();
        using var login = session.Login(Environment.GetEnvironmentVariable("CARD_PIN") ?? string.Empty);

        var keys = session.FindObjects(CK_OBJECT_CLASS.CKO_PRIVATE_KEY);
        Console.WriteLine($"  private keys     : {keys.Count}");

        var opened = CardEnvelopeOpener.OpenWithAgreement(
            raw, point => session.DeriveEcdhSecret(point, keys[0]));

        Console.WriteLine($"OPENED: {opened.Length} bytes");
        Console.WriteLine($"content: {System.Text.Encoding.UTF8.GetString(opened).TrimEnd()}");
    }
}
