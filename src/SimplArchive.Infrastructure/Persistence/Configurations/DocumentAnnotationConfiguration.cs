using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.ServiceAccounts;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

public class DocumentAnnotationConfiguration : IEntityTypeConfiguration<DocumentAnnotation>
{
    public void Configure(EntityTypeBuilder<DocumentAnnotation> builder)
    {
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Text).IsRequired();
        builder.Property(a => a.Color).IsRequired();
        builder.Property(a => a.Points); // Freehand stroke path ("x,y x,y …"); null for every other kind (ADR 0525).

        // Text styling (ADR 0542) — an OWNED type, so it groups in code but stays flat in the table: seven
        // additive nullable "TextStyle_*" columns (EF's default owned-type naming, kept as-is). Optional as a
        // whole: an annotation with no styling has all seven null and reads back as a null TextStyle.
        //
        // Bold/Italic/Underline/Strikethrough are non-nullable in the CLR on purpose. An optional owned type
        // whose properties are ALL nullable gives EF no way to tell "no style" from "a style that is entirely
        // defaults" (it warns: OptionalDependentWithoutIdentifyingProperty, and nested values are lost); a
        // required property gives it that signal. The columns themselves are still nullable in the database —
        // EF relaxes them because the dependent is optional — which is what makes the migration purely additive
        // over existing rows (ADR 0345).
        builder.OwnsOne(a => a.TextStyle, style =>
        {
            style.Property(s => s.FontFamily);
            style.Property(s => s.FontSizePx);
            style.Property(s => s.SizeBasis); // stored as the enum's int; 0 = CellHeight, 1 = CharacterHeight
            style.Property(s => s.Bold);
            style.Property(s => s.Italic);
            style.Property(s => s.Underline);
            style.Property(s => s.Strikethrough);
        });

        // Lists a version's notes (per page); TenantId leads for tenant-scoped locality.
        builder.HasIndex(a => new { a.TenantId, a.DocumentVersionId, a.PageIndex });

        // Exactly one creator — same CASE WHEN "exactly one" shape as ChatMessage/DocumentVersion.
        builder.ToTable(t =>
        {
            t.HasCheckConstraint(
                "CK_DocumentAnnotations_ExactlyOneCreator",
                "(CASE WHEN \"CreatedByUserId\" IS NOT NULL THEN 1 ELSE 0 END + " +
                "CASE WHEN \"CreatedByServiceAccountId\" IS NOT NULL THEN 1 ELSE 0 END) = 1");

            // Defense-in-depth backstops (the controller validates too): a normalized position in [0,1]
            // and a non-negative page index.
            t.HasCheckConstraint(
                "CK_DocumentAnnotations_Position",
                "\"PositionX\" >= 0 AND \"PositionX\" <= 1 AND \"PositionY\" >= 0 AND \"PositionY\" <= 1");
            t.HasCheckConstraint(
                "CK_DocumentAnnotations_PageIndex",
                "\"PageIndex\" >= 0");

            // Markup extent (ADR "Annotation markup: highlight + shapes"): a normalized Width/Height each in
            // [-1,1] (signed for arrows), and a shape (Kind <> 0) must carry an extent while a Note must not.
            t.HasCheckConstraint(
                "CK_DocumentAnnotations_Extent",
                "(\"Width\" IS NULL OR (\"Width\" >= -1 AND \"Width\" <= 1)) AND " +
                "(\"Height\" IS NULL OR (\"Height\" >= -1 AND \"Height\" <= 1))");
            // A box shape must carry an extent; Note (0) is a point and Freehand (7) uses Points instead — both
            // are exempt (ADR 0525).
            t.HasCheckConstraint(
                "CK_DocumentAnnotations_ShapeExtent",
                "\"Kind\" IN (0, 7) OR (\"Width\" IS NOT NULL AND \"Height\" IS NOT NULL)");

            // Freehand (7) must carry its stroke path; every other kind leaves Points null (ADR 0525).
            t.HasCheckConstraint(
                "CK_DocumentAnnotations_FreehandPoints",
                "(\"Kind\" = 7 AND \"Points\" IS NOT NULL) OR (\"Kind\" <> 7 AND \"Points\" IS NULL)");
        });

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(a => a.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // Deleting a document deletes its notes, same as its versions/comments/field values.
        builder.HasOne<Document>()
            .WithMany()
            .HasForeignKey(a => a.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        // The version anchor — Restrict (not Cascade): the document-delete cascade above already removes the
        // note, and a version is never deleted on its own, so a second cascade path isn't needed.
        builder.HasOne<DocumentVersion>()
            .WithMany()
            .HasForeignKey(a => a.DocumentVersionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(a => a.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ServiceAccount>()
            .WithMany()
            .HasForeignKey(a => a.CreatedByServiceAccountId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
