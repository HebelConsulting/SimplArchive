using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Tenant;

public sealed class WebhookSecretRequiredException : TenantException
{
    public WebhookSecretRequiredException()
        : base("WEBHOOK_SECRET_REQUIRED", StatusCodes.Status400BadRequest, "A signing secret is required to enable the audit webhook.")
    {
    }
}
