using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Documents;

// Thrown when a stored appointment cannot be parsed as iCalendar, so the structured editor refuses to touch it
// (#564, ADR 0631 decision 4).
//
// Refusing is the whole point. The editor's contract is that everything it does not model survives an edit, and
// it keeps that promise by reading the component, changing six fields and writing it back. If the read fails
// there is nothing to write back INTO — so composing a fresh component instead would replace whatever the
// originating client wrote with a stub, silently, at the moment the user pressed Save on an unrelated typo.
//
// The contact side never faces this: a vCard merge is line-level, so text it cannot interpret is carried
// through verbatim. Using a library here buys correct component surgery and costs exactly this one case, which
// is why it is an explicit refusal rather than a fallback.
public sealed class UnreadableAppointmentException()
    : DocumentException("UNREADABLE_APPOINTMENT", StatusCodes.Status409Conflict,
        "This appointment is not readable as a calendar entry, so it cannot be edited here. "
        + "Editing it in the application that created it will leave its contents intact.");
