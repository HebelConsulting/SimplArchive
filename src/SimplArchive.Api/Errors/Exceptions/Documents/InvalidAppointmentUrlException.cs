using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Documents;

// Thrown when an appointment's URL is not an absolute URI (#733, ADR 0690).
//
// iCalendar defines URL as a URI and Ical.Net models it as one, so a value like "meet.example" or "www.x.dev"
// cannot be stored at all. The alternative to refusing is dropping it — which the form would report as a
// successful save and the user would discover only by reopening it, the silent degradation ADR 0626 forbids.
//
// The offending value is named because the whole difficulty for the person typing it is that "example.com/x"
// looks like a URL: the message has to say what was missing rather than merely that something was.
public sealed class InvalidAppointmentUrlException(string value)
    : DocumentException("INVALID_APPOINTMENT_URL", StatusCodes.Status400BadRequest,
        $"'{value}' is not a complete web address. Include the scheme — for example https://{value.TrimStart('/')}.");
