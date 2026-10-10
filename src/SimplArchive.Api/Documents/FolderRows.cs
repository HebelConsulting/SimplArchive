using SimplArchive.Infrastructure.Masks;

namespace SimplArchive.Api.Documents;

/// <summary>
/// Whether a listing row is a folder, from its MASK (#1708). Clients used to infer it from "has no file version",
/// so a file-less record (a push key, a pull credential, a customer record: a document whose mask is not a folder
/// mask, carrying only index data) opened as an empty folder and its index data never showed; the desktop then
/// kept reloading it. Every row-emitting endpoint answers it here, from the mask rules it loads once per request.
/// </summary>
public static class FolderRows
{
    /// <summary>A folder mask is a folder; a row whose mask is unknown keeps the old meaning (no file = folder),
    /// so nothing stored reads differently where the mask could not be resolved.</summary>
    public static bool IsFolderRow(this MaskContainmentRules rules, Guid? maskId, bool hasVersions) =>
        maskId is { } mask ? rules.IsFolderMask(mask) : !hasVersions;
}
