using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.CalDav;

// One user's colour override for a typed collection (#564 slice 2, ADR 0620). The collection's OWN default
// lives as a "Colour" FieldValue on the folder (its Addressbook / Calendar mask carries the field),
// so everyone who can see it starts from the same colour; a row here means this user chose differently.
// Absence is not "no colour" — it means "the collection's default applies", so a reset is a delete.
public class DavCollectionColor : ITenantScoped
{
    public Guid UserId { get; set; }

    /// <summary>The typed folder this colour is for.</summary>
    public Guid DocumentId { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>A CSS colour as the client wrote it (e.g. <c>#3f51b5</c>) — opaque to the server.</summary>
    public required string Color { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
