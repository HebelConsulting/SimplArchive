using Microsoft.Extensions.DependencyInjection;
using SimplArchive.Domain.CalDav;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.ModuleAbi;

namespace SimplArchive.UnitTests;

// The registry (ADR 0791) is what lets a MODULE's calendar-shaped folder be a CalDAV collection the whole DAV
// layer serves. These pin the two things a consumer relies on: the core kinds are always present, and a module's
// declaration is translated into a DavCollectionKind keyed by its extension — with its read-only flag carried
// through, because the read-only refusal in DavWrites reads it from here.
public class DavCollectionKindRegistryTests
{
    private static readonly Guid ModuleFolderMask = Guid.Parse("D0000000-0000-0000-0000-0000000000F0");
    private static readonly Guid ModuleItemMask = Guid.Parse("D0000000-0000-0000-0000-0000000000E0");
    private static readonly Guid PlainFolderMask = Guid.Parse("D0000000-0000-0000-0000-0000000000AA");
    private static readonly Guid WritableFolderMask = Guid.Parse("D0000000-0000-0000-0000-0000000000C0");
    private static readonly Guid WritableItemMask = Guid.Parse("D0000000-0000-0000-0000-0000000000B0");

    [Fact]
    public void Core_kinds_are_present_with_no_modules()
    {
        var registry = new DavCollectionKindRegistry([]);

        Assert.Equal(DavCollectionKinds.All.Count, registry.All.Count);
        Assert.Contains(registry.All, k => k.FolderMaskId == WellKnownMaskIds.Calendar);
        Assert.Contains(registry.All, k => k.FolderMaskId == WellKnownMaskIds.Addressbook);
    }

    [Fact]
    public void A_module_dav_collection_joins_the_kinds_translated_and_read_only()
    {
        var registry = new DavCollectionKindRegistry([Loaded(WithLogbook())]);

        var kind = registry.ForFolderMask(ModuleFolderMask);
        Assert.NotNull(kind);
        Assert.Equal(ModuleItemMask, kind!.ItemMaskId);
        Assert.Equal(".ics", kind.Extension);
        Assert.Equal("Event UID", kind.UidFieldName);
        Assert.Equal("Test Logbook", kind.Name); // the mask's own name, which the collection listing reports
        Assert.True(kind.ReadOnly);
    }

    [Fact]
    public void Module_masks_join_the_extension_keyed_sets_the_dav_layer_queries_on()
    {
        var registry = new DavCollectionKindRegistry([Loaded(WithLogbook())]);

        // The .ics sets carry both the core calendars AND the module's; the .vcf sets do not (it is an .ics kind).
        Assert.Contains(WellKnownMaskIds.Calendar, registry.FolderMaskIds(".ics"));
        Assert.Contains(ModuleFolderMask, registry.FolderMaskIds(".ics"));
        Assert.Contains(ModuleItemMask, registry.ItemMaskIds(".ics"));
        Assert.DoesNotContain(ModuleFolderMask, registry.FolderMaskIds(".vcf"));
    }

    [Fact]
    public void A_module_mask_without_a_declaration_contributes_nothing()
    {
        var registry = new DavCollectionKindRegistry([Loaded(new FakeModule([PlainMask()]))]);

        Assert.Equal(DavCollectionKinds.All.Count, registry.All.Count);
        Assert.Null(registry.ForFolderMask(PlainFolderMask));
    }

    // ---- the READ / CREATE split (#1242) -------------------------------------------------------------------
    //
    // These two questions used to be one, answered by ChildCreationPolicy.AdmitsCalendarEntries — a CONTAINMENT
    // check against two core-only static tables, so no module kind could satisfy it. A module's read-only
    // Logbook was therefore advertised WITHOUT the `appointments` rel, which is the only address the Calendar
    // tab reads entries from: listed, tickable, permanently empty, while CalDAV served it perfectly.
    //
    // They are asked separately now because one rel serves two methods on one address (ADR 0719): GET lists,
    // POST creates. And they must be asked of the REGISTRY, which is what the accepting endpoint derives from
    // too — advertising and accepting drifting apart is what produced the defect.

    [Fact]
    public void A_read_only_module_collection_SERVES_entries_but_admits_no_creation()
    {
        var registry = new DavCollectionKindRegistry([Loaded(WithLogbook())]);

        Assert.True(registry.ServesCalendarEntries(ModuleFolderMask),
            "a read-only module collection must still SERVE its entries — the Calendar tab has no other address "
            + "to read them from, so withholding this leaves it listed and permanently empty (#1242)");
        Assert.False(registry.AdmitsCalendarEntryCreation(ModuleFolderMask),
            "nothing may be created in a read-only collection: it is history the module writes. Advertising a "
            + "create the server then refuses is the affordance ADR 0543 exists to prevent");
    }

    [Fact]
    public void A_WRITABLE_module_collection_admits_both()
    {
        // The other side, and why the fix is not "exclude modules": a module may declare a writable calendar,
        // and the old predicate denied it a create rel too.
        var registry = new DavCollectionKindRegistry([Loaded(WithWritableCalendar())]);

        Assert.True(registry.ServesCalendarEntries(WritableFolderMask));
        Assert.True(registry.AdmitsCalendarEntryCreation(WritableFolderMask));
    }

    [Theory]
    [MemberData(nameof(CalendarShapedCoreMasks))]
    public void Every_core_calendar_kind_both_serves_and_admits(Guid folderMaskId)
    {
        // Driven FROM the kind list, so ADDING a kind extends this test rather than leaving it passing against
        // the old set — the property a hand-listed set lacked through two earlier regressions of this shape.
        var registry = new DavCollectionKindRegistry([Loaded(WithLogbook())]);

        Assert.True(registry.ServesCalendarEntries(folderMaskId));
        Assert.True(registry.AdmitsCalendarEntryCreation(folderMaskId), "no core .ics kind is read-only today");
    }

    public static TheoryData<Guid> CalendarShapedCoreMasks()
    {
        var data = new TheoryData<Guid>();
        foreach (var kind in DavCollectionKinds.All.Where(k => k.Extension == ".ics"))
        {
            data.Add(kind.FolderMaskId);
        }

        return data;
    }

    [Fact]
    public void The_kind_list_still_carries_every_calendar_shaped_core_collection() =>
        // Anti-vacuous: the theory proves nothing if the kind list has shrunk back to the two masks that were
        // once hardcoded. Four core .ics kinds exist — Calendar, Schedule, Maintenance, Availability.
        Assert.Equal(4, DavCollectionKinds.All.Count(k => k.Extension == ".ics"));

    [Fact]
    public void An_addressbook_does_not_serve_the_appointments_surface() =>
        // The sibling surface, so a future "simplify" cannot collapse the .ics filter away: an Addressbook
        // admits typed items too, and its rel is `contacts`.
        Assert.False(new DavCollectionKindRegistry([]).ServesCalendarEntries(WellKnownMaskIds.Addressbook));

    [Fact]
    public void A_folder_that_is_no_collection_at_all_serves_nothing() =>
        Assert.False(new DavCollectionKindRegistry([]).ServesCalendarEntries(null));

    private static ModuleLoader.LoadedModule Loaded(IIndustryModule module) => new(module, "in-memory");

    private static FakeModule WithLogbook() =>
        new(
        [
            new ModuleMaskSeed(ModuleFolderMask, "Test Logbook", IsFolderMask: true, IsBookable: false, Fields: [])
            {
                DavCollection = new ModuleDavCollection(".ics", ModuleItemMask, "Event UID", ReadOnly: true),
            },
            new ModuleMaskSeed(ModuleItemMask, "Test Log Entry", IsFolderMask: false, IsBookable: false, Fields: []),
        ]);

    private static FakeModule WithWritableCalendar() =>
        new(
        [
            new ModuleMaskSeed(WritableFolderMask, "Test Diary", IsFolderMask: true, IsBookable: false, Fields: [])
            {
                DavCollection = new ModuleDavCollection(".ics", WritableItemMask, "Event UID", ReadOnly: false),
            },
            new ModuleMaskSeed(WritableItemMask, "Test Diary Entry", IsFolderMask: false, IsBookable: false, Fields: []),
        ]);

    private static ModuleMaskSeed PlainMask() =>
        new(PlainFolderMask, "Test Plain Folder", IsFolderMask: true, IsBookable: false, Fields: []);

    // The minimum an IIndustryModule needs to be — the registry reads only Masks.
    private sealed class FakeModule(IReadOnlyList<ModuleMaskSeed> masks) : IIndustryModule
    {
        public string ModuleId => "test-dav-kinds";

        public string DisplayName => "DAV kinds test module";

        public int AbiMajorVersion => ModuleAbiVersion.Major;

        public string LicenseVerifyKeyPem => string.Empty;

        public IReadOnlyList<ModuleMaskSeed> Masks { get; } = masks;

        public void ConfigureServices(IServiceCollection services)
        {
        }
    }
}
