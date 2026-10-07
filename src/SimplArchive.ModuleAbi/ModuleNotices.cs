namespace SimplArchive.ModuleAbi;

/// <summary>
/// A notice a module sends from an ACTION rather than from the escalation sweep (ABI 1.10, core ADR 0902): a renewal
/// confirmation, with the signed licence attached. Inject it into a controller.
/// </summary>
/// <remarks>
/// <para>
/// A user of the tenant gets an in-app notification (and mail, as their settings say); any other address is mailed only
/// if the module declares <see cref="IIndustryModule.SendsExternalNotices"/>, and only when the tenant can send mail.
/// </para>
/// <para>
/// The notice is QUEUED, not sent: it is written in the current transaction and leaves after the commit, so an act that
/// rolls back sends nothing (core ADR 0794). Wrap the act in <see cref="IModuleArchiveFacade.InTransactionAsync"/>.
/// </para>
/// </remarks>
public interface IModuleNotices
{
    /// <summary>Queues <paramref name="notice"/>; false when it was refused (an undeclared external address, or no mail
    /// configured), which the host logs at Warning.</summary>
    Task<bool> QueueAsync(ModuleNotice notice, CancellationToken cancellationToken = default);
}

/// <summary>A notice from an action: who, what, and optionally where replies go and a document to attach.</summary>
public sealed record ModuleNotice(string RecipientEmail, string Subject, string Message)
{
    /// <summary>Where a reply should go (external mail only).</summary>
    public string? ReplyTo { get; init; }

    /// <summary>A document whose current content is attached, frozen at queue time (external mail only).</summary>
    public Guid? AttachmentDocumentId { get; init; }
}
