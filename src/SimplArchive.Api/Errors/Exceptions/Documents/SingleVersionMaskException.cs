using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Documents;

/// <summary>
/// A second version was filed against a document whose mask takes exactly one (ADR 0848).
/// </summary>
/// <remarks>
/// <c>409</c> rather than <c>400</c>: the request is well-formed and would be legal against almost any other
/// document — what refuses it is the state of THIS one, which is the conflict semantics.
/// </remarks>
public sealed class SingleVersionMaskException(string message)
    : DocumentException("DOCUMENT_TAKES_ONE_VERSION", StatusCodes.Status409Conflict, message);
