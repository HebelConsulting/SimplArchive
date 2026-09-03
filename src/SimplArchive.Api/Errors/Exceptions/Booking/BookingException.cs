namespace SimplArchive.Api.Errors.Exceptions.Booking;

// Base class for inventory-booking errors (ADR 0735). Inherits from ApiException so the global handler
// translates it to an RFC 7807 response; concrete errors inherit from this so a caller can
// `catch (BookingException)`. See the exception-type principle in CLAUDE.md.
public abstract class BookingException : ApiException
{
    protected BookingException(string errorCode, int statusCode, string message)
        : base(errorCode, statusCode, message)
    {
    }
}
