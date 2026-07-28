namespace SimplArchive.Api.Errors.Exceptions.LegalHolds;

// Base class for legal-hold errors (ADR "Legal hold and retention enforcement") — both the hold-management
// endpoints (LegalHoldsController) and the cross-cutting "this document is frozen by an active hold" refusal
// thrown at every mutation site. Inherits from ApiException so the global handler translates it to an RFC 7807
// response; concrete errors inherit from this so a caller can `catch (LegalHoldException)`. See the
// exception-type principle in CLAUDE.md.
public abstract class LegalHoldException : ApiException
{
    protected LegalHoldException(string errorCode, int statusCode, string message)
        : base(errorCode, statusCode, message)
    {
    }
}
