using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using SimplArchive.Domain.Modules;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.ModuleAbi;

namespace SimplArchive.UnitTests;

// Vendor key rotation with an OVERLAP (ABI 0.25, ADR 0793). A single verify key made rotation a hard cutover:
// the release that swapped it invalidated every not-yet-activated license signed with the old one. The module
// now declares a LIST — the overlap window itself — a license verifies under ANY listed key, and the verifier
// reports WHICH one accepted it so a compromised key's activations can be found.
//
// Keys are GENERATED here, never committed: a test key in the repo is one grep away from being trusted (and
// gitleaks would rightly object) — the same rule ModuleLicenseTests states.
public class ModuleLicenseKeyRotationTests
{
    private static readonly Guid TenantId = Guid.NewGuid();

    private static ModuleLicense SignedBy(ECDsa key) =>
        new ModuleLicense(KeyListModule.Id, TenantId, new DateOnly(2027, 9, 3), ModuleAbiVersion.Major, string.Empty)
            .Sign(key);

    [Fact]
    public void A_module_that_declares_one_key_is_unchanged_by_the_widening()
    {
        // The default bridge: a module that never thought about rotation keeps working and its list IS its one
        // key. This is what makes the ABI addition free for every module built before 0.25.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        // Through the INTERFACE deliberately: the bridge is a default interface member, so it exists exactly
        // where the core reads it and nowhere else — which is also why a module need not mention it at all.
        IIndustryModule module = new SingleKeyModule(key.ExportSubjectPublicKeyInfoPem());

        Assert.Equal([module.LicenseVerifyKeyPem], module.LicenseVerifyKeysPem);
        ModuleLicenseVerifier.Verify(SignedBy(key), module, TenantId); // no throw IS the assertion
    }

    [Fact]
    public void During_the_overlap_a_license_signed_by_either_key_activates()
    {
        // The point of the whole change: mid-rotation the vendor issues on the NEW key while licenses signed in
        // the old, uncompromised window still activate.
        using var retiring = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var current = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var module = new KeyListModule(current.ExportSubjectPublicKeyInfoPem(), retiring.ExportSubjectPublicKeyInfoPem());

        ModuleLicenseVerifier.Verify(SignedBy(current), module, TenantId);
        ModuleLicenseVerifier.Verify(SignedBy(retiring), module, TenantId);
    }

    [Fact]
    public void The_verification_reports_which_key_accepted_it()
    {
        // Without this the overlap is unauditable: with several keys accepted, "who is on the compromised one"
        // stops being derivable from the license alone.
        using var retiring = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var current = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var currentPem = current.ExportSubjectPublicKeyInfoPem();
        var retiringPem = retiring.ExportSubjectPublicKeyInfoPem();
        var module = new KeyListModule(currentPem, retiringPem);

        Assert.Equal(ModuleLicenseVerifier.KeyThumbprint(currentPem), ModuleLicenseVerifier.Verify(SignedBy(current), module, TenantId));
        Assert.Equal(ModuleLicenseVerifier.KeyThumbprint(retiringPem), ModuleLicenseVerifier.Verify(SignedBy(retiring), module, TenantId));

        // …and the two keys are told apart, or the record would identify nothing.
        Assert.NotEqual(ModuleLicenseVerifier.KeyThumbprint(currentPem), ModuleLicenseVerifier.KeyThumbprint(retiringPem));
    }

    [Fact]
    public void Retiring_a_key_is_shipping_a_release_without_it()
    {
        // The overlap ENDS by the list shrinking — that is the whole retirement mechanism, so a license signed
        // by the dropped key must then be refused.
        using var retiring = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var current = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var license = SignedBy(retiring);

        var duringOverlap = new KeyListModule(current.ExportSubjectPublicKeyInfoPem(), retiring.ExportSubjectPublicKeyInfoPem());
        ModuleLicenseVerifier.Verify(license, duringOverlap, TenantId); // still fine

        var afterRetirement = new KeyListModule(current.ExportSubjectPublicKeyInfoPem());
        var refusal = Assert.Throws<ModuleLicenseException>(() => ModuleLicenseVerifier.Verify(license, afterRetirement, TenantId));
        Assert.Contains("signature", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_key_nobody_listed_is_still_refused()
    {
        // The list widens WHO may sign, not WHETHER a signature is checked.
        using var current = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var retiring = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var module = new KeyListModule(current.ExportSubjectPublicKeyInfoPem(), retiring.ExportSubjectPublicKeyInfoPem());

        Assert.Throws<ModuleLicenseException>(() => ModuleLicenseVerifier.Verify(SignedBy(stranger), module, TenantId));
    }

    [Fact]
    public void A_thumbprint_names_the_key_rather_than_its_pem_text()
    {
        // Of the KEY, not of the text: a re-export or different line endings must not look like a different
        // key, or a rotation record would be noise. Exercised by re-exporting the same key.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var once = key.ExportSubjectPublicKeyInfoPem();
        var again = key.ExportSubjectPublicKeyInfoPem().ReplaceLineEndings("\r\n") + "\n";

        Assert.Equal(ModuleLicenseVerifier.KeyThumbprint(once), ModuleLicenseVerifier.KeyThumbprint(again));
        Assert.Equal(64, ModuleLicenseVerifier.KeyThumbprint(once).Length); // SHA-256 as lowercase hex

        // An unreadable key names nothing rather than throwing — it can only ever be a key that verifies nothing.
        Assert.Equal(string.Empty, ModuleLicenseVerifier.KeyThumbprint("not a pem"));
    }

    // A module declaring SEVERAL verify keys — newest first, as the ABI advises.
    private sealed class KeyListModule(params string[] keysPem) : IIndustryModule
    {
        internal const string Id = "key-list-module";

        public string ModuleId => Id;

        public string DisplayName => "Key list module";

        public int AbiMajorVersion => ModuleAbiVersion.Major;

        public string LicenseVerifyKeyPem => keysPem[0];

        public IReadOnlyList<string> LicenseVerifyKeysPem { get; } = keysPem;

        public IReadOnlyList<ModuleMaskSeed> Masks => [];

        public void ConfigureServices(IServiceCollection services)
        {
        }
    }

    // A module built the old way: ONE key, and no opinion about the list at all.
    private sealed class SingleKeyModule(string keyPem) : IIndustryModule
    {
        public string ModuleId => KeyListModule.Id;

        public string DisplayName => "Single key module";

        public int AbiMajorVersion => ModuleAbiVersion.Major;

        public string LicenseVerifyKeyPem => keyPem;

        public IReadOnlyList<ModuleMaskSeed> Masks => [];

        public void ConfigureServices(IServiceCollection services)
        {
        }
    }
}
