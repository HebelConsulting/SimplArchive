using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Cli.Commands;
using SimplArchive.Cli.Infrastructure;
using SimplArchive.ModuleAbi;
using SimplArchive.TestModule;

namespace SimplArchive.EndToEndTests;

// `saconsole module upload` (#1703, ABI 1.14, ADR 0918): a client holding only a module credential finds the upload
// rel on the ANONYMOUS root (an installation-wide root link), PUTs the file straight to storage and files it by the
// offer's commit link, never composing an address.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class ModuleUploadCommandTests(E2EApiFactory factory)
{
    private static IEnumerable<string> Rels(JsonElement root) =>
        root.GetProperty("links").EnumerateArray().Select(l => l.GetProperty("rel").GetString()!);

    [Fact]
    public async Task An_installation_wide_rel_is_on_every_root_once_and_a_tenant_rel_stays_with_its_tenant()
    {
        using var anonymous = factory.CreateClient();
        var anonymousRels = Rels(await TestJson.Get(anonymous, "/api")).ToList();
        Assert.Single(anonymousRels, r => r == "test-module:upload");
        Assert.DoesNotContain("test-module:status", anonymousRels);   // ADR 0737's rels still need an active tenant

        var rig = await TestModuleRig.CreateAsync(factory);
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", vendorKey.ExportSubjectPublicKeyInfoPem());
        try
        {
            await TestModuleRig.ActivateAsync(rig, vendorKey, grantPrincipal: true);
            var memberRels = Rels(await TestJson.Get(rig.Owner, "/api")).ToList();
            Assert.Single(memberRels, r => r == "test-module:upload");   // not repeated by the tenant's own pass
            Assert.Contains("test-module:status", memberRels);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", null);
            rig.Admin.Dispose();
            rig.Owner.Dispose();
        }
    }

    [Fact]
    public async Task The_command_uploads_with_a_module_credential_alone_and_answers_the_modules_filing()
    {
        var rig = await TestModuleRig.CreateAsync(factory);
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", vendorKey.ExportSubjectPublicKeyInfoPem());
        Environment.SetEnvironmentVariable(TestUploadController.UploadFolderVariable, rig.RepoId.ToString());
        using var anonymous = factory.CreateClient();   // no login: the root is read without one
        try
        {
            await TestModuleRig.ActivateAsync(rig, vendorKey, grantPrincipal: true);
            var key = ModuleCredentialFormat.Compose(rig.TenantId, TestCredentialAuthenticator.Secret);
            var bytes = Encoding.UTF8.GetBytes($"a release {Guid.NewGuid():N}");

            var filed = JsonDocument.Parse(await ModuleUploadCommand.UploadAsync(
                anonymous, "test-module:upload", key, bytes, NullLogger.Instance, CancellationToken.None)).RootElement;

            Assert.Equal(Encoding.UTF8.GetString(bytes), filed.GetProperty("text").GetString());
            Assert.NotEqual(Guid.Empty, filed.GetProperty("id").GetGuid());

            // A wrong key is the module's refusal, worded for the operator; the rel itself is still found.
            var refused = await Assert.ThrowsAsync<CliException>(() => ModuleUploadCommand.UploadAsync(
                anonymous, "test-module:upload", ModuleCredentialFormat.Compose(rig.TenantId, "wrong"), bytes, NullLogger.Instance, CancellationToken.None));
            Assert.Contains("refused the credential", refused.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestUploadController.UploadFolderVariable, null);
            Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", null);
            rig.Admin.Dispose();
            rig.Owner.Dispose();
        }
    }

    [Fact]
    public void The_tool_spells_the_upload_offer_as_the_ABI_does()
    {
        // saconsole references no server assembly, so it spells the protocol itself; this holds the two equal.
        Assert.Equal(ModuleUploadProtocol.UrlField, ModuleUploadCommand.UrlField);
        Assert.Equal(ModuleUploadProtocol.CommitRel, ModuleUploadCommand.CommitRel);
    }
}
