using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// A folder wearing a structural mask keeps it (ADR 0685).
//
// The rule is about what re-typing COSTS. Turn a Calendar into a plain folder and only CalDAV subscribability
// is lost — the appointments inside stay perfectly good documents. Turn a Mailbox or a Notebook into one and
// you break what the content depends on: mail has nowhere to arrive, a notebook's projection stops existing.
//
// The refusals are only half of what is asserted here. The other half is the ALLOWED cases, because the
// dangerous version of this rule is the one that reads "wears a structural mask ⇒ refuse" and thereby breaks
// provisioning and the personal-space heal — the paths that put these masks on in the first place.
public class StructuralMaskImmutabilityTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, Guid? tenantId = null) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options,
            new CurrentTenantAccessor { TenantId = tenantId });

    private sealed record World(Guid TenantId, Guid UserId, Guid FolderId);

    /// <summary>A tenant with the well-known mask versions seeded by hand, and one folder to re-type.</summary>
    private static async Task<World> SeedAsync(SqliteConnection connection, Guid? initialMaskId)
    {
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();

        var w = new World(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;

        using var db = CreateContext(connection);
        db.Tenants.Add(new Tenant { Id = w.TenantId, Name = "T", CreatedAt = now });
        db.Users.Add(new User { Id = w.UserId, TenantId = w.TenantId, Email = "u@example.com", DisplayName = "U", CreatedAt = now });

        foreach (var maskId in WellKnownMaskIds.All)
        {
            db.Masks.Add(new Mask
            {
                Id = maskId,
                TenantId = w.TenantId,
                IsFolderMask = WellKnownMaskIds.FolderMasks.Contains(maskId),
                CreatedAt = now,
            });
            db.MaskVersions.Add(new MaskVersion
            {
                Id = VersionIdFor(maskId),
                TenantId = w.TenantId,
                MaskId = maskId,
                Name = NameFor(maskId),
                CreatedAt = now,
            });
        }

        db.Documents.Add(new Document
        {
            Id = w.FolderId,
            TenantId = w.TenantId,
            Name = "A folder",
            MaskVersionId = initialMaskId is { } m ? VersionIdFor(m) : null,
            CreatedByUserId = w.UserId,
            CreatedAt = now,
        });

        await db.SaveChangesAsync();
        return w;
    }

    // Deterministic version ids, so a test can name the version it wants without a lookup.
    private static Guid VersionIdFor(Guid maskId) =>
        new([.. maskId.ToByteArray().Select(b => (byte)(b ^ 0x5A))]);

    private static string NameFor(Guid maskId) =>
        maskId == WellKnownMaskIds.Mailbox ? "Mailbox"
        : maskId == WellKnownMaskIds.Notebook ? "Notebook"
        : maskId == WellKnownMaskIds.Calendar ? "Calendar"
        : maskId == WellKnownMaskIds.Folder ? "Folder"
        : maskId.ToString();

    private static async Task RetypeAsync(SqliteConnection connection, World w, Guid? toMaskId)
    {
        using var db = CreateContext(connection, w.TenantId);
        var folder = await db.Documents.SingleAsync(d => d.Id == w.FolderId);
        folder.MaskVersionId = toMaskId is { } m ? VersionIdFor(m) : null;
        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData(nameof(WellKnownMaskIds.Mailbox))]
    [InlineData(nameof(WellKnownMaskIds.ImapSpecial))]
    [InlineData(nameof(WellKnownMaskIds.Notebook))]
    [InlineData(nameof(WellKnownMaskIds.NotebookSection))]
    public async Task A_structural_folder_cannot_be_re_typed(string maskName)
    {
        var maskId = (Guid)typeof(WellKnownMaskIds).GetField(maskName)!.GetValue(null)!;

        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var w = await SeedAsync(connection, maskId);

        await Assert.ThrowsAsync<StructuralMaskImmutableException>(
            () => RetypeAsync(connection, w, WellKnownMaskIds.Folder));
    }

    [Fact]
    public async Task A_structural_folder_cannot_be_un_typed_either()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var w = await SeedAsync(connection, WellKnownMaskIds.Mailbox);

        // Clearing is a change: an untyped mailbox is exactly as unreachable as a re-typed one.
        await Assert.ThrowsAsync<StructuralMaskImmutableException>(() => RetypeAsync(connection, w, null));
    }

    [Fact]
    public async Task A_calendar_may_still_be_re_typed()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var w = await SeedAsync(connection, WellKnownMaskIds.Calendar);

        // The decided boundary: only subscribability is lost, and what is inside stays viable — so it is a
        // preference the user may change their mind about.
        await RetypeAsync(connection, w, WellKnownMaskIds.Folder);

        using var db = CreateContext(connection, w.TenantId);
        Assert.Equal(VersionIdFor(WellKnownMaskIds.Folder), (await db.Documents.SingleAsync(d => d.Id == w.FolderId)).MaskVersionId);
    }

    [Fact]
    public async Task An_untyped_folder_may_still_BECOME_a_mailbox()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var w = await SeedAsync(connection, initialMaskId: null);

        // This is the heal, and it is the case a careless version of this rule breaks: a pre-upgrade personal
        // space holds MASKLESS folders that provisioning stamps afterwards.
        await RetypeAsync(connection, w, WellKnownMaskIds.Mailbox);

        using var db = CreateContext(connection, w.TenantId);
        Assert.Equal(VersionIdFor(WellKnownMaskIds.Mailbox), (await db.Documents.SingleAsync(d => d.Id == w.FolderId)).MaskVersionId);
    }

    [Fact]
    public async Task Publishing_a_new_version_of_the_same_mask_is_not_a_re_type()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var w = await SeedAsync(connection, WellKnownMaskIds.Mailbox);

        // Editing the Mailbox mask publishes a new version and re-points every mailbox at it. That is a mask
        // edit, not a re-type — which is why the rule compares by MASK and not by mask VERSION. Without that
        // distinction, changing a field on the Mailbox mask would be refused for every mailbox in the tenant.
        var secondVersionId = Guid.NewGuid();
        using (var db = CreateContext(connection, w.TenantId))
        {
            db.MaskVersions.Add(new MaskVersion
            {
                Id = secondVersionId,
                TenantId = w.TenantId,
                MaskId = WellKnownMaskIds.Mailbox,
                Name = "Mailbox",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using (var db = CreateContext(connection, w.TenantId))
        {
            (await db.Documents.SingleAsync(d => d.Id == w.FolderId)).MaskVersionId = secondVersionId;
            await db.SaveChangesAsync();
        }

        using (var db = CreateContext(connection, w.TenantId))
        {
            Assert.Equal(secondVersionId, (await db.Documents.SingleAsync(d => d.Id == w.FolderId)).MaskVersionId);
        }
    }

    [Fact]
    public async Task A_plain_folder_may_still_be_restamped()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var w = await SeedAsync(connection, WellKnownMaskIds.Folder);

        // The other provisioning path: EnsureTypedFolderAsync moves an already-created folder OFF plain Folder
        // onto its proper type. Plain Folder is not structural, so this stays legal.
        await RetypeAsync(connection, w, WellKnownMaskIds.MyDocuments);

        using var db = CreateContext(connection, w.TenantId);
        Assert.Equal(VersionIdFor(WellKnownMaskIds.MyDocuments), (await db.Documents.SingleAsync(d => d.Id == w.FolderId)).MaskVersionId);
    }
}
