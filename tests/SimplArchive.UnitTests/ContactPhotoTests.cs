using SimplArchive.Api.Documents;

namespace SimplArchive.UnitTests;

// Reading a contact's picture out of its own vCard (#658 follow-on). Two of these cases are about what we
// REFUSE, which is the half a happy-path test would miss: a vCard is user-supplied data that arrives by import,
// by DAV sync and by drag-and-drop, and this decides both what the server fetches and what it serves back.
public class ContactPhotoTests
{
    // A 1×1 JPEG is not needed — nothing decodes the image, and the magic bytes are what the sniffer reads.
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly IContactCardComposer Composer = new ContactCardComposer();

    private static string Card(string? photoLine) =>
        string.Join("\r\n",
            photoLine is null
                ? ["BEGIN:VCARD", "VERSION:3.0", "UID:u-1", "FN:Ada Lovelace", "END:VCARD"]
                : ["BEGIN:VCARD", "VERSION:3.0", "UID:u-1", "FN:Ada Lovelace", photoLine, "END:VCARD"]);

    [Fact]
    public void An_inline_vcard_3_photo_is_decoded_with_its_type_sniffed()
    {
        var photo = Composer.ReadPhoto(Card($"PHOTO;ENCODING=b;TYPE=JPEG:{Convert.ToBase64String(Jpeg)}"));

        Assert.NotNull(photo);
        Assert.Equal("image/jpeg", photo!.ContentType);
        Assert.Equal(Jpeg, photo.Bytes);
    }

    [Fact]
    public void A_data_uri_photo_is_decoded_too()
    {
        // The vCard 4.0 spelling. Same picture, different syntax — a client should not care which its peer used.
        var photo = Composer.ReadPhoto(Card($"PHOTO:data:image/png;base64,{Convert.ToBase64String(Png)}"));

        Assert.NotNull(photo);
        Assert.Equal("image/png", photo!.ContentType);
        Assert.Equal(Png, photo.Bytes);
    }

    [Fact]
    public void An_external_photo_url_is_not_followed()
    {
        // Cards exported from some address books carry PHOTO;VALUE=URI:https://… . Following it would mean this
        // server issuing requests to arbitrary hosts named by whoever imported the card. The card simply reads
        // as having no photo, and the contact shows initials — a stated consequence, not a silent failure.
        Assert.Null(Composer.ReadPhoto(Card("PHOTO;VALUE=URI:https://photos.example.com/ada.jpg")));
        Assert.Null(Composer.ReadPhoto(Card("PHOTO:http://169.254.169.254/latest/meta-data/")));
    }

    [Fact]
    public void A_photo_whose_bytes_are_not_a_known_raster_image_is_refused()
    {
        // The type the card DECLARES is never trusted: `image/svg+xml` is a scriptable document, and echoing one
        // back from our own origin is a stored-XSS delivery mechanism. The sniffer decides, and it only knows
        // raster formats — so this is refused however it is labelled.
        var svg = System.Text.Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>");

        Assert.Null(Composer.ReadPhoto(Card($"PHOTO;ENCODING=b;TYPE=JPEG:{Convert.ToBase64String(svg)}")));
        Assert.Null(Composer.ReadPhoto(Card($"PHOTO:data:image/svg+xml;base64,{Convert.ToBase64String(svg)}")));
    }

    [Fact]
    public void A_card_with_no_photo_or_an_unreadable_one_has_none()
    {
        Assert.Null(Composer.ReadPhoto(Card(null)));
        Assert.Null(Composer.ReadPhoto(Card("PHOTO;ENCODING=b;TYPE=JPEG:not base64 at all !!")));
        Assert.Null(Composer.ReadPhoto(string.Empty));
    }

    [Fact]
    public void An_edit_preserves_the_photo()
    {
        // The whole reason a picture survives at all: PHOTO is not modelled by the structured form, so an edit
        // must carry it through verbatim. If Merge ever dropped it, the photo would vanish the first time
        // somebody corrected a phone number — silently, and only for the contacts people actually maintain.
        var original = Card($"PHOTO;ENCODING=b;TYPE=JPEG:{Convert.ToBase64String(Jpeg)}");
        var card = Composer.Read(original);

        var merged = Composer.Merge(original, card with { Phones = [new ContactField("+41 44 000 00 00", "work")] }, "u-1");

        Assert.NotNull(Composer.ReadPhoto(merged));
        Assert.Equal(Jpeg, Composer.ReadPhoto(merged)!.Bytes);
    }
}
