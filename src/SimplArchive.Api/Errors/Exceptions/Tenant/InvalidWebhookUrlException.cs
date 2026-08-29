using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Tenant;

public sealed class InvalidWebhookUrlException : TenantException
{
    public InvalidWebhookUrlException()
        : base("INVALID_WEBHOOK_URL", StatusCodes.Status400BadRequest, "The audit webhook URL must be an absolute http(s) URL.")
    {
    }

    /// <summary>
    /// The refusal an outbound-address check produced (ADR 0717). The reason is told to the administrator who
    /// typed the URL — they have to be able to fix it, and what it says is a property of the CONFIGURED policy
    /// (which ranges are refused), not of the network behind it.
    /// </summary>
    public InvalidWebhookUrlException(string reason)
        : base("INVALID_WEBHOOK_URL", StatusCodes.Status400BadRequest, $"The audit webhook URL was refused: {reason}.")
    {
    }
}
