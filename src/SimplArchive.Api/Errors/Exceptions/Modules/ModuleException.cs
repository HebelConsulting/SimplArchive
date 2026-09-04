namespace SimplArchive.Api.Errors.Exceptions.Modules;

// Base class for industry-module activation errors (ADRs 0740/0741/0743). Inherits from ApiException so
// the global handler translates it to an RFC 7807 response; concrete errors inherit from this so a caller
// can `catch (ModuleException)` for the whole area. See the exception-type principle in CLAUDE.md.
public abstract class ModuleException : ApiException
{
    protected ModuleException(string errorCode, int statusCode, string message)
        : base(errorCode, statusCode, message)
    {
    }

    // With extension members riding the problem response (the ApiException.Extensions rule) — the
    // transition refusal's machine-readable diagnosis is the first user (ADR 0742's grammar on the wire).
    protected ModuleException(string errorCode, int statusCode, string message, IReadOnlyDictionary<string, object?> extensions)
        : base(errorCode, statusCode, message, extensions)
    {
    }
}
