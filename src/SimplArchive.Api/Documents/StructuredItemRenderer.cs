using System.Globalization;
using System.Net;
using System.Text;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Api.Documents;

/// <summary>
/// Renders a stored contact card or appointment as an HTML card, for the preview pipeline to turn into a PDF.
/// </summary>
/// <remarks>
/// <para>
/// A <c>.vcf</c> and a <c>.ics</c> previewed as "No preview available" was the last place these items were
/// second-class: every other format the archive holds shows something in the preview pane, and the two the
/// Contacts and Calendar tabs are built on showed nothing at all.
/// </para>
/// <para>
/// It reuses the SAME composers the editors read through, so what the preview shows and what the edit form
/// shows are the same reading of the same file. A second parser here would drift from them, and the drift
/// would surface as a preview quietly disagreeing with the form beside it.
/// </para>
/// </remarks>
public sealed class StructuredItemRenderer : IStructuredItemRenderer
{
    private readonly IContactCardComposer _contacts;
    private readonly IAppointmentComposer _appointments;

    public StructuredItemRenderer(IContactCardComposer contacts, IAppointmentComposer appointments)
    {
        _contacts = contacts;
        _appointments = appointments;
    }

    public bool Handles(string extension) => extension is ".vcf" or ".ics";

    // The field labels are ENGLISH, deliberately, while the app ships EN/DE/IT/ES. A rendition is generated
    // once and CACHED beside the object it describes, so a localized one would mean either a cache per culture
    // — four PDFs per contact, invalidated together and mostly never read — or a preview whose language is
    // whoever happened to open it first. Stated here rather than discovered: the pane's own labels beside it
    // are translated, and only the rendered card is not.


    public string? ToHtml(string content, string extension) => extension switch
    {
        ".vcf" => Contact(content),
        ".ics" => Appointment(content),
        _ => null,
    };

    private string? Contact(string content)
    {
        ContactCard card;
        ContactPhoto? photo;
        try
        {
            card = _contacts.Read(content);
            photo = _contacts.ReadPhoto(content);
        }
        catch (Exception)
        {
            return null; // unparseable — "no preview available" is the honest answer
        }

        var name = FirstNonEmpty(card.FormattedName, Join(card.GivenName, card.FamilyName), card.Organization);
        if (name is null && card.Emails.Count == 0 && card.Phones.Count == 0)
        {
            return null; // nothing worth drawing; a card of empty rows is worse than no preview
        }

        var rows = new StringBuilder();
        Row(rows, "Organisation", card.Organization);
        Row(rows, "Title", card.Title);
        foreach (var email in card.Emails)
        {
            Row(rows, Labelled("Email", email.Type), email.Value);
        }

        foreach (var phone in card.Phones)
        {
            Row(rows, Labelled("Phone", phone.Type), phone.Value);
        }

        foreach (var address in card.Addresses)
        {
            Row(rows, Labelled("Address", address.Type), Address(address));
        }

        Row(rows, "Birthday", card.Birthday);
        Row(rows, "Website", card.Url);
        Row(rows, "Note", card.Note);

        // The picture is INLINED as a data URI rather than linked: the converter renders this document in its
        // own sandbox with no credentials and no route back to us, so a linked image would simply not load.
        var portrait = photo is { } picture
            ? $"<img class='portrait' src='data:{picture.ContentType};base64,{Convert.ToBase64String(picture.Bytes)}' alt='' />"
            : $"<div class='portrait initials'>{Escape(Presentation.ContactInitials.From(name))}</div>";

        return Document(Escape(name ?? "Contact"), portrait, rows.ToString());
    }

    private string? Appointment(string content)
    {
        Appointment appointment;
        try
        {
            appointment = _appointments.Read(content);
        }
        catch (Exception)
        {
            return null;
        }

        if (appointment.Summary is null && appointment.Start is null)
        {
            return null;
        }

        var rows = new StringBuilder();
        Row(rows, "When", When(appointment));
        Row(rows, "Where", appointment.Location);

        // The rule verbatim, exactly as the detail panes show it: nothing here interprets a recurrence, and
        // inventing prose for one would state more than we know.
        Row(rows, "Repeats", appointment.RecurrenceRule);
        Row(rows, "Description", appointment.Description);

        return Document(
            Escape(appointment.Summary ?? "Appointment"),
            "<div class='portrait initials'>&#128197;</div>",
            rows.ToString());
    }

    /// <summary>The whole span in one line, in the shape the detail panes already use.</summary>
    /// <remarks>
    /// An all-day entry says so rather than being given a time it does not have — the same refusal to invent
    /// midnight that ADR 0647 makes everywhere else.
    /// </remarks>
    private static string? When(Appointment appointment)
    {
        if (appointment.Start is not { } start)
        {
            return null;
        }

        if (appointment.IsAllDay)
        {
            // DTEND is exclusive, so a one-day entry's end is the next morning; showing the range would read as
            // two days. The last covered day is what a reader means by "until".
            var last = appointment.End is { } stops && stops.Date > start.Date ? stops.Date.AddDays(-1) : start.Date;
            return last == start.Date
                ? $"{start:d} (all day)"
                : $"{start:d} – {last:d} (all day)";
        }

        return appointment.End is { } end
            ? $"{start:g} – {(end.Date == start.Date ? end.ToString("t", CultureInfo.CurrentCulture) : end.ToString("g", CultureInfo.CurrentCulture))}"
            : start.ToString("g", CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// The card itself. A raw string with DOUBLED interpolation braces, so the CSS keeps its own single ones.
    /// </summary>
    private static string Document(string title, string portrait, string rows) =>
        $$"""
          <!doctype html>
          <html><head><meta charset="utf-8" />
          <style>
            body { font-family: 'Helvetica Neue', Helvetica, Arial, sans-serif; color: #14161c; margin: 2.5rem; }
            .head { display: flex; align-items: center; gap: 1rem; margin-bottom: 1.75rem; }
            .portrait { width: 84px; height: 84px; border-radius: 50%; object-fit: cover; flex: 0 0 auto; }
            .initials { background: #e6e7ec; display: flex; align-items: center; justify-content: center;
                        font-size: 1.75rem; font-weight: 600; color: #2b3a4a; }
            h1 { font-size: 1.5rem; margin: 0; }
            table { border-collapse: collapse; }
            th { text-align: left; font-weight: 500; color: #5a5f6e; padding: .35rem 1.25rem .35rem 0;
                 vertical-align: top; white-space: nowrap; }
            td { padding: .35rem 0; vertical-align: top; }
          </style></head>
          <body>
            <div class="head">{{portrait}}<h1>{{title}}</h1></div>
            <table>{{rows}}</table>
          </body></html>
          """;

    private static void Row(StringBuilder rows, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return; // an empty row states that a field exists and is blank, which is not what the file says
        }

        rows.Append("<tr><th>").Append(Escape(label)).Append("</th><td>")
            .Append(Escape(value).Replace("\n", "<br />", StringComparison.Ordinal))
            .Append("</td></tr>");
    }

    /// <summary>"Email (work)" — the vCard TYPE, where the card gave one.</summary>
    private static string Labelled(string label, string? type) =>
        string.IsNullOrWhiteSpace(type) ? label : $"{label} ({type})";

    private static string? Address(ContactAddress address) =>
        Join(address.Street, Join(address.PostalCode, address.City), address.Region, address.Country);

    private static string? Join(params string?[] parts) =>
        string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p))) is { Length: > 0 } joined ? joined : null;

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    /// <summary>
    /// HTML-escaped, always.
    /// </summary>
    /// <remarks>
    /// Every value here comes out of a file somebody else wrote — imported, synced from a phone, or dropped in
    /// by a colleague — so a contact could otherwise be named <c>&lt;script&gt;</c> and have it rendered. The
    /// converter runs the document, which makes this the boundary that matters.
    /// </remarks>
    private static string Escape(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
