using System.Security.Cryptography;
using SimplArchive.Domain.Modules;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.ModuleAbi;
using SimplArchive.TestModule;

namespace SimplArchive.UnitTests;

// The license artefact's verification (ADRs 0740/0743): a vendor-signed claim set, verified against the
// module's embedded key, each refusal precise. Keys are GENERATED here — no key material lives in the
// repo (gitleaks would rightly object, and a committed test key is one grep away from being trusted).
public class ModuleLicenseTests
{
    private static readonly Guid TenantId = Guid.NewGuid();

    private static (TestModule.TestModule Module, ModuleLicense License) SignedLicense(
        Action<ECDsa>? plantKey = null, Func<ModuleLicense, ModuleLicense>? mutate = null)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        TestModule.TestModule.VerifyKeyPem = key.ExportSubjectPublicKeyInfoPem();
        plantKey?.Invoke(key);
        var license = new ModuleLicense("test-module", TenantId, new DateOnly(2027, 9, 3), ModuleAbiVersion.Major, string.Empty)
            .Sign(key);
        if (mutate is not null)
        {
            license = mutate(license);
        }

        return (new TestModule.TestModule(), license);
    }

    [Fact]
    public void A_vendor_signed_license_verifies()
    {
        var (module, license) = SignedLicense();

        ModuleLicenseVerifier.Verify(license, module, TenantId); // no throw IS the assertion
    }

    [Fact]
    public void The_json_artefact_round_trips_through_parse()
    {
        var (module, license) = SignedLicense();
        var json = System.Text.Json.JsonSerializer.Serialize(license, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        var parsed = ModuleLicenseVerifier.Parse(json);

        Assert.Equal(license, parsed);
        ModuleLicenseVerifier.Verify(parsed, module, TenantId);
    }

    [Fact]
    public void A_tampered_claim_fails_the_signature()
    {
        // The claim moves AFTER signing — the exact fraud the signature exists for (a support contract
        // extended by editing the JSON).
        var (module, license) = SignedLicense(mutate: l => l with { SupportContractEnd = new DateOnly(2099, 1, 1) });

        var refusal = Assert.Throws<ModuleLicenseException>(() => ModuleLicenseVerifier.Verify(license, module, TenantId));
        Assert.Contains("signature", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_license_signed_by_the_wrong_key_is_refused()
    {
        var (module, license) = SignedLicense(plantKey: _ =>
        {
            using var otherVendor = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            TestModule.TestModule.VerifyKeyPem = otherVendor.ExportSubjectPublicKeyInfoPem();
        });

        Assert.Throws<ModuleLicenseException>(() => ModuleLicenseVerifier.Verify(license, module, TenantId));
    }

    [Fact]
    public void The_claim_checks_are_each_precise()
    {
        var (module, license) = SignedLicense();

        var wrongTenant = Assert.Throws<ModuleLicenseException>(
            () => ModuleLicenseVerifier.Verify(license, module, Guid.NewGuid()));
        Assert.Contains("different tenant", wrongTenant.Message);

        // Re-sign with changed claims so only the CLAIM under test fails, never the signature.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        TestModule.TestModule.VerifyKeyPem = key.ExportSubjectPublicKeyInfoPem();

        var wrongModule = (license with { ModuleId = "other-module" }).Sign(key);
        Assert.Contains("other-module", Assert.Throws<ModuleLicenseException>(
            () => ModuleLicenseVerifier.Verify(wrongModule, module, TenantId)).Message);

        var wrongAbi = (license with { AbiMajorVersion = ModuleAbiVersion.Major + 1 }).Sign(key);
        Assert.Contains("ABI major", Assert.Throws<ModuleLicenseException>(
            () => ModuleLicenseVerifier.Verify(wrongAbi, module, TenantId)).Message);
    }

    [Fact]
    public void An_unreadable_artefact_is_refused_as_malformed()
    {
        Assert.Throws<ModuleLicenseException>(() => ModuleLicenseVerifier.Parse("not json at all"));
        Assert.Throws<ModuleLicenseException>(() => ModuleLicenseVerifier.Parse("null"));
    }

    [Fact]
    public void The_grace_arithmetic_carries_the_end_day_and_thirty_more()
    {
        var activation = new ModuleActivation
        {
            SupportContractEndDate = new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero),
        };

        // Through the end day: fully active, not yet in grace.
        var endOfEndDay = new DateTimeOffset(2026, 9, 30, 23, 59, 0, TimeSpan.Zero);
        Assert.True(ModuleActivationPolicy.IsActive(activation, endOfEndDay));
        Assert.False(ModuleActivationPolicy.IsInGrace(activation, endOfEndDay));

        // The day after: still running, but on grace — the escalation window (ADR 0740).
        var dayAfter = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
        Assert.True(ModuleActivationPolicy.IsActive(activation, dayAfter));
        Assert.True(ModuleActivationPolicy.IsInGrace(activation, dayAfter));

        // Grace runs 30 days from expiry: 1 Oct + 30d = 31 Oct 00:00 is the first deactivated instant.
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero), ModuleActivationPolicy.DeactivatesAt(activation));
        Assert.False(ModuleActivationPolicy.IsActive(activation, new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero)));
    }
}
