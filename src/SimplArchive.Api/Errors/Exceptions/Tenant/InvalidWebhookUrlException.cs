using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Tenant;

public sealed class InvalidWebhookUrlException : TenantException
{
    public InvalidWebhookUrlException()
        : base("INVALID_WEBHOOK_URL", StatusCodes.Status400BadRequest, "The audit webhook URL must be an absolute http(s) URL.")
    {
    }
}
