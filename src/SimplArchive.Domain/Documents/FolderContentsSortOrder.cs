namespace SimplArchive.Domain.Documents;

// How a folder's children are ordered by default when the folder is opened (ADR "Per-folder contents sort
// order"). Persisted per folder on Document.ContentsSortOrder. Folders are always listed on top regardless;
// this criterion orders within each group. Clicking a column header in the client is an ephemeral override
// (not persisted). Only meaningful for a folder (a Document with no versions); ignored for a leaf.
public enum FolderContentsSortOrder
{
    // Alphabetical by document name (A–Z).
    Name = 0,

    // By the latest confirmed version's DocumentDate (the issuing/document date). The default.
    DocumentDate = 1,

    // By the latest confirmed version's CreatedAt (the filing timestamp).
    Created = 2,
}
