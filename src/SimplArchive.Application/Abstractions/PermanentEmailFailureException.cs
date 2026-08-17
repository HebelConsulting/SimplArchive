namespace SimplArchive.Application.Abstractions;

/// <summary>
/// The mail server rejected the message in a way that retrying cannot fix — an SMTP 5xx: no such mailbox, the
/// domain does not exist, the message was refused outright (ADR 0612).
/// </summary>
/// <remarks>
/// It exists so the dispatcher can tell "this will never work" from "this did not work just now" WITHOUT
/// referencing the mail library: the abstraction is <see cref="IEmailSender"/>, and a caller that had to catch a
/// vendor exception type to make a policy decision would be reaching straight through it.
/// </remarks>
public sealed class PermanentEmailFailureException : Exception
{
    public PermanentEmailFailureException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
