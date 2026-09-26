using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimplArchive.Api.Lmtp;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.EndToEndTests;

// A refused recipient's Warning names the mailbox somebody MEANT to work (#1369).
//
// The wrong turn this exists for cost a debugging session: a Mailbox document was created and NAMED after the
// address, in the right folder, with the right mask — and mail to it bounced, because a mailbox claims addresses
// through its "eMail Addresses" field and never through its name. Nothing said so. The tree looked right, the
// detail pane looked right, and the refusal listed the three conditions that would have permitted delivery
// rather than which one had failed. It was found by comparing field values against a mailbox known to work.
//
// Asserted on the NOTE rather than on log output: the note is the behaviour, and the one interpolation that
// puts it in the Warning is checked by the compiler. Making the field required was rejected — a personal
// mailbox with no addresses is correct, and the mask heal refuses required fields (#1369, ADR 0626's shape).
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class LmtpRefusalDiagnosticTests
{
    private readonly E2EApiFactory _factory;

    public LmtpRefusalDiagnosticTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_mailbox_named_after_the_address_that_claims_nothing_is_named_in_the_refusal()
    {
        var rig = await TenantWithVerifiedDomainAsync();
        var address = $"sales@{rig.Domain}";
        await MailboxNamedAsync(rig, folderName: "sales", documentName: address);

        var note = await DescribeAsync(address);

        Assert.NotNull(note);
        Assert.Contains(address, note, StringComparison.Ordinal);            // WHICH document
        Assert.Contains("'sales'", note, StringComparison.Ordinal);          // and where it is
        Assert.Contains("EMPTY", note, StringComparison.Ordinal);            // and what is wrong with it
        Assert.Contains(WellKnownMaskSeeder.MailboxAddressesFieldName, note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_mailbox_that_claims_a_DIFFERENT_address_is_described_as_that_instead()
    {
        // A different and rarer mistake — a typo in the field rather than an empty field — and saying "EMPTY"
        // about it would be false. The count is what distinguishes them, which is why the note counts rather
        // than assuming.
        var rig = await TenantWithVerifiedDomainAsync();
        var address = $"support@{rig.Domain}";
        var mailboxId = await MailboxNamedAsync(rig, folderName: "support", documentName: address);
        await ClaimAsync(rig.TenantId, mailboxId, $"suport@{rig.Domain}");   // the typo

        var note = await DescribeAsync(address);

        Assert.NotNull(note);
        Assert.DoesNotContain("EMPTY", note, StringComparison.Ordinal);
        Assert.Contains("not this one", note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_ordinary_unknown_recipient_says_nothing_extra()
    {
        // The half that keeps this usable. A public-facing MTA refuses a great deal of mail, and a note on every
        // refusal would be noise that buries the one that matters — so with no document named after the address,
        // there is nothing to add.
        var rig = await TenantWithVerifiedDomainAsync();

        Assert.Null(await DescribeAsync($"nobody-{Guid.NewGuid():N}@{rig.Domain}"));
    }

    [Fact]
    public async Task An_address_on_a_domain_this_installation_does_not_serve_says_nothing()
    {
        // Naming one of our documents for mail addressed to somebody else's domain would be both noise and a
        // small disclosure; on a public MTA this is most refusals.
        Assert.Null(await DescribeAsync($"anyone@not-ours-{Guid.NewGuid():N}.test"));
    }

    private sealed record Rig(Guid TenantId, string Domain, HttpClient Api);

    private async Task<string?> DescribeAsync(string address)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<LmtpDelivery>()
            .DescribeRefusalAsync(address, CancellationToken.None);
    }

    /// <summary>A tenant whose domain is registered and verified, plus an authenticated client.</summary>
    private async Task<Rig> TenantWithVerifiedDomainAsync()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var domain = $"inert-{Guid.NewGuid():N}"[..16].ToLowerInvariant() + ".test";

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
            db.TenantMailDomains.Add(new TenantMailDomain
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Domain = domain,
                CreatedAt = DateTimeOffset.UtcNow,
                VerifiedAt = DateTimeOffset.UtcNow,   // delivery accepts only a verified domain (#667)
            });
            await db.SaveChangesAsync();
        }

        return new Rig(tenantId, domain,
            _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret)));
    }

    /// <summary>The mistake, made the way a person makes it: a folder, and a Mailbox inside it NAMED after the
    /// address — created through the API, with the Mailbox mask assigned and no field value touched.</summary>
    private async Task<Guid> MailboxNamedAsync(Rig rig, string folderName, string documentName)
    {
        var repoId = (await TestJson.Post(rig.Api, "/api/repositories",
            new { name = $"Inert {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var folderId = (await TestJson.Post(rig.Api, $"/api/documents/{repoId}/children",
            new { name = folderName })).GetProperty("id").GetGuid();
        var mailboxId = (await TestJson.Post(rig.Api, $"/api/documents/{folderId}/children",
            new { name = documentName })).GetProperty("id").GetGuid();

        await TestJson.Put(rig.Api, $"/api/documents/{mailboxId}/mask", new { maskId = WellKnownMaskIds.Mailbox });
        return mailboxId;
    }

    /// <summary>Puts one address on a mailbox's claim list, straight at the store.</summary>
    private async Task ClaimAsync(Guid tenantId, Guid mailboxId, string claimedAddress)
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentTenantAccessor>().TenantId = tenantId;
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();

        var fieldId = await db.MaskVersions.IgnoreQueryFilters()
            .Where(v => v.TenantId == tenantId && v.MaskId == WellKnownMaskIds.Mailbox && v.IsCurrent)
            .Join(db.FieldDefinitions.IgnoreQueryFilters(), v => v.Id, f => f.MaskVersionId, (_, f) => f)
            .Where(f => f.Name == WellKnownMaskSeeder.MailboxAddressesFieldName)
            .Select(f => f.Id)
            .SingleAsync();

        db.FieldValues.Add(new FieldValue
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DocumentId = mailboxId,
            FieldDefinitionId = fieldId,
            Value = claimedAddress,
        });
        await db.SaveChangesAsync();
    }
}
