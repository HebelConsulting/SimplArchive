using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.CalDav;

namespace SimplArchive.Infrastructure.Modules;

/// <summary>
/// The core DAV collection kinds plus each loaded module's declared kinds (ABI 0.24, ADR 0791). Assembled once
/// from the loaded modules — their kind declarations are process-global, not per tenant.
/// </summary>
public sealed class DavCollectionKindRegistry : IDavCollectionKindRegistry
{
    private readonly IReadOnlyList<DavCollectionKind> _all;

    public DavCollectionKindRegistry(IReadOnlyList<ModuleLoader.LoadedModule> modules)
    {
        var kinds = new List<DavCollectionKind>(DavCollectionKinds.All);
        foreach (var loaded in modules)
        {
            foreach (var mask in loaded.Module.Masks)
            {
                if (mask.DavCollection is { } dav)
                {
                    // The module names its folder mask (the collection) and item mask; the rest mirrors a core
                    // DavCollectionKind. Name = the mask's own name, which the collection listing reports.
                    kinds.Add(new DavCollectionKind(
                        FolderMaskId: mask.MaskId,
                        ItemMaskId: dav.ItemMaskId,
                        Extension: dav.Extension,
                        UidFieldName: dav.UidFieldName,
                        Name: mask.Name,
                        ReadOnly: dav.ReadOnly)
                    {
                        // Which of the module's own fields span an entry (ABI 0.13). Null unless declared, and
                        // then the projection falls back to the core names — so a module that does call them
                        // Start and End needs to say nothing.
                        StartFieldName = dav.StartFieldName,
                        EndFieldName = dav.EndFieldName,
                    });
                }
            }
        }

        _all = kinds;
    }

    public IReadOnlyList<DavCollectionKind> All => _all;

    public DavCollectionKind? ForFolderMask(Guid? folderMaskId) =>
        folderMaskId is { } id ? _all.FirstOrDefault(k => k.FolderMaskId == id) : null;

    /// <inheritdoc />
    public bool ServesCalendarEntries(Guid? folderMaskId) =>
        ForFolderMask(folderMaskId) is { Extension: ".ics" };

    /// <inheritdoc />
    public bool AdmitsCalendarEntryCreation(Guid? folderMaskId) =>
        ForFolderMask(folderMaskId) is { Extension: ".ics", ReadOnly: false };

    public IReadOnlyList<Guid> FolderMaskIds(string extension) =>
        [.. _all.Where(k => k.Extension == extension).Select(k => k.FolderMaskId)];

    public IReadOnlyList<Guid> ItemMaskIds(string extension) =>
        [.. _all.Where(k => k.Extension == extension).Select(k => k.ItemMaskId)];
}
