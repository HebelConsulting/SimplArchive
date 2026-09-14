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
