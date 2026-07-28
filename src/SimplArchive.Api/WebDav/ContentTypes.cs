namespace SimplArchive.Api.WebDav;

// A small extension → MIME map for WebDAV responses (ADR "WebDAV gateway"). Only a hint for clients; the
// stored object's own content type isn't relied on here.
internal static class ContentTypes
{
    public static string ForExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".txt" => "text/plain",
        ".md" or ".markdown" => "text/markdown",
        ".csv" => "text/csv",
        ".json" => "application/json",
        ".xml" => "application/xml",
        ".html" or ".htm" => "text/html",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".tif" or ".tiff" => "image/tiff",
        ".zip" => "application/zip",
        ".eml" => "message/rfc822",
        ".doc" => "application/msword",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xls" => "application/vnd.ms-excel",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".ppt" => "application/vnd.ms-powerpoint",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        _ => "application/octet-stream",
    };
}
