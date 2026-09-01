using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>One typed multi-value row (an e-mail or a phone number).</summary>
public sealed partial class ContactFieldRowViewModel : ObservableObject
{
    [ObservableProperty] private string _value = string.Empty;
    [ObservableProperty] private string _type = "work";

    /// <summary>The three the form offers. vCard allows many more; these are the ones a person picks.</summary>
    public static IReadOnlyList<string> Types { get; } = ["home", "work", "mobile"];
}

/// <summary>One postal address. Every part optional — a card may carry only a city.</summary>
public sealed partial class ContactAddressRowViewModel : ObservableObject
{
    [ObservableProperty] private string _type = "home";
    [ObservableProperty] private string _street = string.Empty;
    [ObservableProperty] private string _city = string.Empty;
    [ObservableProperty] private string _region = string.Empty;
    [ObservableProperty] private string _postalCode = string.Empty;
    [ObservableProperty] private string _country = string.Empty;

    public static IReadOnlyList<string> Types { get; } = ["home", "work"];
}

/// <summary>
/// The contact edit form's state (#564, ADR 0631). Holds only the fields the form models — everything else on
/// the stored card is preserved by the server's merge and never travels through here.
/// </summary>
public sealed partial class ContactEditViewModel : StructuredEditFormViewModel
{
    [ObservableProperty] private string _givenName = string.Empty;
    [ObservableProperty] private string _familyName = string.Empty;
    [ObservableProperty] private string _organization = string.Empty;
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _birthday = string.Empty;
    [ObservableProperty] private string _url = string.Empty;
    [ObservableProperty] private string _note = string.Empty;

    public ObservableCollection<ContactFieldRowViewModel> Emails { get; } = [];

    public ObservableCollection<ContactFieldRowViewModel> Phones { get; } = [];

    public ObservableCollection<ContactAddressRowViewModel> Addresses { get; } = [];

    /// <summary>
    /// The card's FN as stored. Kept rather than recomposed: a contact whose display name is deliberately not
    /// "given family" — a company, a person with one name, a name in an order we do not model — must not have
    /// it rewritten because somebody edited their phone number.
    /// </summary>
    public string? StoredFormattedName { get; set; }

    [RelayCommand]
    public void AddEmail() => Emails.Add(new ContactFieldRowViewModel { Type = "work" });

    [RelayCommand]
    public void RemoveEmail(ContactFieldRowViewModel row) => Emails.Remove(row);

    [RelayCommand]
    public void AddPhone() => Phones.Add(new ContactFieldRowViewModel { Type = "mobile" });

    [RelayCommand]
    public void RemovePhone(ContactFieldRowViewModel row) => Phones.Remove(row);

    [RelayCommand]
    public void AddAddress() => Addresses.Add(new ContactAddressRowViewModel());

    [RelayCommand]
    public void RemoveAddress(ContactAddressRowViewModel row) => Addresses.Remove(row);

    /// <summary>Reads the API's contact-card resource into the form.</summary>
    public static ContactEditViewModel From(JsonElement body)
    {
        var model = new ContactEditViewModel
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
            model.Emails.Add(new ContactFieldRowViewModel { Value = Text(row, "value"), Type = Text(row, "type") is { Length: > 0 } t ? t : "work" });
        }

        foreach (var row in Array(body, "phones"))
        {
            model.Phones.Add(new ContactFieldRowViewModel { Value = Text(row, "value"), Type = Text(row, "type") is { Length: > 0 } t ? t : "mobile" });
        }

        foreach (var row in Array(body, "addresses"))
        {
            model.Addresses.Add(new ContactAddressRowViewModel
            {
                Type = Text(row, "type") is { Length: > 0 } t ? t : "home",
                Street = Text(row, "street"),
                City = Text(row, "city"),
                Region = Text(row, "region"),
                PostalCode = Text(row, "postalCode"),
                Country = Text(row, "country"),
            });
        }

        return model;
    }

    /// <summary>The body a PUT sends back. Empty rows are dropped rather than saved as blank properties.</summary>
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
        addresses = Addresses.Where(a => !a.IsEmpty())
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

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static IEnumerable<JsonElement> Array(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : [];
}

internal static class ContactAddressRowExtensions
{
    public static bool IsEmpty(this ContactAddressRowViewModel a) =>
        string.IsNullOrWhiteSpace(a.Street) && string.IsNullOrWhiteSpace(a.City) && string.IsNullOrWhiteSpace(a.Region)
        && string.IsNullOrWhiteSpace(a.PostalCode) && string.IsNullOrWhiteSpace(a.Country);
}
