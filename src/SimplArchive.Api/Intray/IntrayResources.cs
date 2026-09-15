namespace SimplArchive.Api.Intray;

// The Intray's wire types, lifted out of IntrayController.
//
// WHY THEY MOVED: the controller had reached 999 of its 1000-line budget — one line from a ceiling that makes
// the NEXT change to it, whatever that turns out to be, impossible without an exception. Thirteen type
// declarations were never the controller's job, so they are the cheapest 130 lines to give back, and giving
// them back needs no exception from anyone.
//
// NOT for the reason the first draft of this comment claimed. It said the move unblocked giving
// `from-document` and `from-items` resource-shaped routes (#1173). ADR 0797 had already examined and REJECTED
// that conversion — "the intray creates stay as they are — the table was wrong": `POST /api/intray` already
// exists as the plain create, so these are three distinct creation SOURCES on one collection with three
// unrelated bodies and responses, and folding them into one endpoint would mean a polymorphic body
// discriminated by which field is present. That is a worse contract than three named sub-resources, not a
// better one. The size was recorded there as a second, independent obstacle — which is exactly how it came to
// be mistaken for the blocker.
//
// They are declarations, not behaviour: nothing here was rewritten, only unnested. Every reference inside the
// controller still resolves, because it already carried `using SimplArchive.Api.Intray;` for the scope
// resolver, and nothing outside it named these types — the seven external references to IntrayController are
// all to its statics.
//
// Plain mutable classes with parameterless constructors, like every other DTO here: the XmlSerializer needs
// that shape for the JSON/XML content negotiation (ADR 0190).

public class IntrayItemResource : HypermediaResource
{
    public string Name { get; set; } = string.Empty;

    public long Size { get; set; }

    public DateTimeOffset LastModified { get; set; }

    // True when a `{name}.mask.json` sidecar exists — the item has a staged mask/index-data draft.
    public bool HasMask { get; set; }

    // True when a `{name}.signed` sidecar exists: the content carries a digital signature (#491). Answered
    // from the prefix listing rather than the bytes, the same way HasMask is — reading each item to paint a
    // list would cost one download per row. The clients badge these, and offer no page operation on them,
    // because any rewrite voids a signature.
    public bool Signed { get; set; }

    // The source of a group-intray item (ADR 0532): the group's id + name, so the client labels it `[GroupName]`
    // and its action links already carry `?group=`. Null for the caller's own intray items.
    public Guid? GroupId { get; set; }

    public string? GroupName { get; set; }

    // The source of another user's intray item (ADR 0532), shown only to a CanManageIntrays holder viewing a
    // user's intray: the user's id + name, so the client labels it and its links carry `?user=`. Null otherwise.
    public Guid? UserId { get; set; }

    public string? UserName { get; set; }
}

public class IntrayResource : HypermediaResource
{
    public List<IntrayItemResource> Items { get; set; } = [];
}

// A group the caller belongs to — an upload-target choice for a group intray (ADR 0532).
public class IntrayGroupResource
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

public class IntrayGroupsResource : HypermediaResource
{
    public List<IntrayGroupResource> Groups { get; set; } = [];
}

// A user in the tenant — a "Send to a user" / admin user-picker choice (ADR 0532).
public class IntrayUserResource
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

public class IntrayUsersResource : HypermediaResource
{
    public List<IntrayUserResource> Users { get; set; } = [];
}

public class UploadIntrayRequest
{
    public string FileName { get; set; } = string.Empty;
}

public class UploadIntrayResource : HypermediaResource
{
    public string Name { get; set; } = string.Empty;

    public Uri UploadUrl { get; set; } = null!;
}

public class FileIntrayRequest
{
    public Guid FolderId { get; set; }

    // When set, the item is filed as a new *version* of this existing document instead of as a new document
    // in FolderId (ADR "Context-aware inbox filing dialog").
    public Guid? DocumentId { get; set; }

    // Optional override for the filed document's name; defaults to the intray filename.
    public string? Name { get; set; }

    // Optional feed comment posted on the resulting document (ADR "Filing posts a feed comment"); when
    // blank, a default "@{DisplayName} filed a new document." is posted.
    public string? Comment { get; set; }
}

// Move an intray item into another intray (ADR 0532) — exactly one target: a group's intray (any group in the
// tenant) or a user's intray (any user). Both null / both set is a 400.
public class MoveIntrayRequest
{
    public Guid? TargetGroupId { get; set; }

    public Guid? TargetUserId { get; set; }
}

// The staged mask draft (also the on-the-wire shape and the sidecar JSON shape). MaskId null = "(No mask)".
// Name/DocumentDate are staged system fields (the filed Document.Name / DocumentVersion.DocumentDate) —
// DocumentDate is a "yyyy-MM-dd" string (ADR "Staged Name + Document date on inbox items").
public class IntrayMaskResource : HypermediaResource
{
    public string? Name { get; set; }

    public string? DocumentDate { get; set; }

    public Guid? MaskId { get; set; }

    public List<IntrayMaskFieldResource> Fields { get; set; } = [];

    // Staged OCR languages (ordered Tesseract codes) for a scannable item (.tif/.tiff/.pdf), consumed at
    // filing to set the version's OcrLanguages before the searchable-PDF conversion (ADR "Inbox OCR-language
    // staging"). Null/empty = the tenant default.
    public List<string>? OcrLanguages { get; set; }
}

public class IntrayMaskFieldResource
{
    public Guid FieldDefinitionId { get; set; }

    public List<string> Values { get; set; } = [];
}

public class IntrayFromDocumentRequest
{
    public Guid DocumentId { get; set; }
}
