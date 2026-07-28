using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Tenant;

public sealed class WebhookNotConfiguredException : TenantException
{
    public WebhookNotConfiguredException()
        : base("WEBHOOK_NOT_CONFIGURED", StatusCodes.Status400BadRequest, "Configure and save the audit webhook URL + secret before sending a test event.")
    {
    }
}
