using System.Text.Json;

namespace SimplArchive.Client.Services;

/// <summary>Where a new contact or appointment will be filed, and the advertised address to create it at.</summary>
/// <remarks>
/// The href is always one the collection advertised (<c>contacts</c> / <c>appointments</c>), so the dialog
/// composes no API URL and can offer no target the server would refuse: a collection that withholds the rel
/// simply produces no target (ADR 0543).
/// </remarks>
public sealed record CreateTarget(Guid CollectionId, string DisplayName, string CreateHref);

/// <summary>One typed multi-value row — an e-mail address or a phone number.</summary>
public sealed class ContactFieldRow
{
    public string Value { get; set; } = string.Empty;

    public string Type { get; set; } = "work";

    /// <summary>The three the form offers. vCard allows many more; these are the ones a person picks.</summary>
    public static IReadOnlyList<string> Types { get; } = ["home", "work", "mobile"];
}

/// <summary>One postal address. Every part optional — a card may carry only a city.</summary>
public sealed class ContactAddressRow
{
    public string Type { get; set; } = "home";

    public string Street { get; set; } = string.Empty;

    public string City { get; set; } = string.Empty;

    public string Region { get; set; } = string.Empty;

    public string PostalCode { get; set; } = string.Empty;

    public string Country { get; set; } = string.Empty;

    public static IReadOnlyList<string> Types { get; } = ["home", "work"];

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Street) && string.IsNullOrWhiteSpace(City) && string.IsNullOrWhiteSpace(Region)
        && string.IsNullOrWhiteSpace(PostalCode) && string.IsNullOrWhiteSpace(Country);
}

/// <summary>
/// The web contact form's state (#631) — the twin of the desktop's <c>ContactEditViewModel</c>, modelling the
/// same field set because the two clients are one surface (ADR 0511).
/// </summary>
/// <remarks>
/// Holds only what the form shows. Everything else on the stored card — the photo, custom labels, extensions,
/// anniversary — is preserved by the server's merge and never travels through here, which is what lets a
/// contact authored on somebody's phone survive an edit made in a browser.
/// </remarks>
public sealed class ContactCardForm
{
    public string GivenName { get; set; } = string.Empty;

    public string FamilyName { get; set; } = string.Empty;

    public string Organization { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Birthday { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public string Note { get; set; } = string.Empty;

    public List<ContactFieldRow> Emails { get; } = [];

    public List<ContactFieldRow> Phones { get; } = [];

    public List<ContactAddressRow> Addresses { get; } = [];

    /// <summary>
    /// The card's FN as stored. Kept rather than recomposed: a contact whose display name is deliberately not
    /// "given family" — a company, a person with one name, a name in an order we do not model — must not have
    /// it rewritten because somebody edited their phone number.
    /// </summary>
    public string? StoredFormattedName { get; set; }

    /// <summary>False when the caller may read the card but not save it, so the form opens read-only.</summary>
    public bool CanEdit { get; set; } = true;

    /// <summary>Reads the API's contact-card resource into the form.</summary>
    public static ContactCardForm From(JsonElement body)
    {
        var form = new ContactCardForm
        {
            GivenName = Text(body, "givenName"),
            FamilyName = Text(body, "familyName"),
            Organization = Text(body, "organization"),
            Title = Text(body, "title"),
            Birthday = Text(body, "birthday"),
            Url = Text(body, "url"),
            Note = Text(body, "note"),
            StoredFormattedName = Text(body, "formattedName") is { Length: > 0 } fn ? fn : null,
        };

        foreach (var row in Array(body, "emails"))
        {
            form.Emails.Add(new ContactFieldRow { Value = Text(row, "value"), Type = TypeOr(row, "work") });
        }

        foreach (var row in Array(body, "phones"))
        {
            form.Phones.Add(new ContactFieldRow { Value = Text(row, "value"), Type = TypeOr(row, "mobile") });
        }

        foreach (var row in Array(body, "addresses"))
        {
            form.Addresses.Add(new ContactAddressRow
            {
                Type = TypeOr(row, "home"),
                Street = Text(row, "street"),
                City = Text(row, "city"),
                Region = Text(row, "region"),
                PostalCode = Text(row, "postalCode"),
                Country = Text(row, "country"),
            });
        }

        return form;
    }

    /// <summary>The body a save or a create sends. Empty rows are dropped rather than stored as blanks.</summary>
    /// <remarks>
    /// One payload for both, because the create takes the editor's own resource — so there is no create-shaped
    /// subset for a second phone number or a birthday to fall through.
    /// </remarks>
    public object ToPayload() => new
    {
        formattedName = StoredFormattedName,
        givenName = Null(GivenName),
        familyName = Null(FamilyName),
        organization = Null(Organization),
        title = Null(Title),
        emails = Emails.Where(e => !string.IsNullOrWhiteSpace(e.Value))
            .Select(e => new { value = e.Value.Trim(), type = e.Type }).ToArray(),
        phones = Phones.Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .Select(p => new { value = p.Value.Trim(), type = p.Type }).ToArray(),
        addresses = Addresses.Where(a => !a.IsEmpty)
            .Select(a => new
            {
                type = a.Type,
                street = Null(a.Street),
                city = Null(a.City),
                region = Null(a.Region),
                postalCode = Null(a.PostalCode),
                country = Null(a.Country),
            }).ToArray(),
        birthday = Null(Birthday),
        url = Null(Url),
        note = Null(Note),
    };

    private static string? Null(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string TypeOr(JsonElement row, string fallback) =>
        Text(row, "type") is { Length: > 0 } type ? type : fallback;

    internal static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    internal static IEnumerable<JsonElement> Array(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : [];
}
