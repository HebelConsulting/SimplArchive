using System.Text.Json;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopUiEndToEndTests;

// The two structured editors' view-models (#564, ADR 0631). VM-level and display-free, like the rest of this
// suite: what is worth pinning here is what the form SENDS, because a field the form drops silently is
// indistinguishable from one the user cleared on purpose.
public class StructuredEditorViewModelTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    [Fact]
    public void An_appointment_sends_back_the_recurrence_it_was_given()
    {
        // The rule is READ-ONLY in the form, which is exactly why it has to be sent: the server's merge clears
        // a rule handed to it as null, so a form that omitted the field would silently un-repeat every
        // recurring appointment anybody opened and saved. Nothing in the UI would show it — the user would
        // find out when next week's occurrence did not arrive.
        var model = AppointmentEditViewModel.From(Json("""
            {
              "summary": "Weekly sync",
              "start": "2026-09-01T14:00:00",
              "end": "2026-09-01T15:00:00",
              "isAllDay": false,
              "timeZoneId": "Europe/Zurich",
              "recurrenceRule": "FREQ=WEEKLY;BYDAY=TU",
              "reminderCount": 2
            }
            """));

        var payload = JsonSerializer.Serialize(model.ToPayload());

        Assert.Contains("FREQ=WEEKLY;BYDAY=TU", payload);
        Assert.Contains("Europe/Zurich", payload);
    }

    [Fact]
    public void An_appointment_sends_its_own_wall_clock_without_an_offset()
    {
        // The zone travels in timeZoneId, so the time itself must carry no offset: an offset would assert a
        // zone of its own, and attaching one is how a floating time stops floating (ADR 0631 decision 5).
        var model = AppointmentEditViewModel.From(Json("""
            {"summary":"Floating","start":"2026-09-01T14:00:00","end":"2026-09-01T15:00:00","isAllDay":false}
            """));

        Assert.Null(model.TimeZoneId);

        var payload = JsonSerializer.Serialize(model.ToPayload());

        Assert.Contains("2026-09-01T14:00:00", payload);
        Assert.DoesNotContain("+0", payload);
        Assert.DoesNotContain("14:00:00Z", payload);
    }

    [Fact]
    public void An_appointment_reads_its_attendees_and_reminders_as_display_only()
    {
        var model = AppointmentEditViewModel.From(Json("""
            {
              "summary": "Weekly sync",
              "reminderCount": 2,
              "attendees": [{"name":"Tom Fischer","address":"tom@example.test","status":"ACCEPTED"}]
            }
            """));

        Assert.True(model.HasReminders);
        Assert.Equal("Tom Fischer", model.Attendees[0].Name);

        // Neither appears in what a save sends: this product never issues a scheduling message (decision 3),
        // and the reminder is the server's to keep (decision 4). A payload carrying either would invite the
        // endpoint to honour it later.
        var payload = JsonSerializer.Serialize(model.ToPayload());
        Assert.DoesNotContain("attendee", payload, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reminder", payload, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_contact_keeps_the_display_name_it_was_stored_with()
    {
        // FN is not recomposed from the name parts. A card whose display name is deliberately not
        // "given family" — a company, a person with one name — must not have it rewritten because somebody
        // edited their phone number.
        var model = ContactEditViewModel.From(Json("""
            {"formattedName":"Contoso AG","givenName":"","familyName":"","organization":"Contoso"}
            """));

        model.Phones.Add(new ContactFieldRowViewModel { Value = "+41 79 000 00 00", Type = "mobile" });

        Assert.Contains("Contoso AG", JsonSerializer.Serialize(model.ToPayload()));
    }

    [Fact]
    public void A_contact_drops_the_rows_the_user_left_blank()
    {
        // The form opens with an empty e-mail and phone row so New Contact shows it supports them. Sending
        // those as values would write blank properties onto the stored card.
        var model = ContactEditViewModel.From(Json("""{"givenName":"Tom","familyName":"Fischer"}"""));
        model.AddEmail();
        model.AddPhone();
        model.AddAddress();
        model.Emails[0].Value = "tom@example.test";

        var payload = JsonSerializer.Serialize(model.ToPayload());

        Assert.Contains("tom@example.test", payload);
        Assert.Contains("\"phones\":[]", payload);
        Assert.Contains("\"addresses\":[]", payload);
    }

    [Fact]
    public void A_contact_round_trips_every_modelled_field()
    {
        var model = ContactEditViewModel.From(Json("""
            {
              "formattedName": "Anna Meyer", "givenName": "Anna", "familyName": "Meyer",
              "organization": "Contoso", "title": "Head of Procurement",
              "emails": [{"value":"anna@example.test","type":"work"}],
              "phones": [{"value":"+41790000000","type":"mobile"}],
              "addresses": [{"type":"work","street":"Bahnhofstrasse 1","city":"Zurich","postalCode":"8001","country":"Switzerland"}],
              "birthday": "1990-02-15", "url": "https://contoso.example", "note": "Met at the trade fair."
            }
            """));

        Assert.Equal("Anna", model.GivenName);
        Assert.Equal("work", model.Emails[0].Type);
        Assert.Equal("Zurich", model.Addresses[0].City);

        // Parsed rather than substring-matched: System.Text.Json's default encoder escapes '+' as +, so a
        // phone number is present and correct in the payload while a naive Contains() for it fails. Asserting
        // on the decoded values tests what the server will actually receive.
        var payload = Json(JsonSerializer.Serialize(model.ToPayload()));

        Assert.Equal("Anna", payload.GetProperty("givenName").GetString());
        Assert.Equal("Meyer", payload.GetProperty("familyName").GetString());
        Assert.Equal("Contoso", payload.GetProperty("organization").GetString());
        Assert.Equal("Head of Procurement", payload.GetProperty("title").GetString());
        Assert.Equal("anna@example.test", payload.GetProperty("emails")[0].GetProperty("value").GetString());
        Assert.Equal("+41790000000", payload.GetProperty("phones")[0].GetProperty("value").GetString());
        Assert.Equal("Bahnhofstrasse 1", payload.GetProperty("addresses")[0].GetProperty("street").GetString());
        Assert.Equal("8001", payload.GetProperty("addresses")[0].GetProperty("postalCode").GetString());
        Assert.Equal("Switzerland", payload.GetProperty("addresses")[0].GetProperty("country").GetString());
        Assert.Equal("1990-02-15", payload.GetProperty("birthday").GetString());
        Assert.Equal("https://contoso.example", payload.GetProperty("url").GetString());
        Assert.Equal("Met at the trade fair.", payload.GetProperty("note").GetString());
    }
}
