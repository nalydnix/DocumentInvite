using System.Security.Cryptography;
using System.Text;
using DocumentInvite.Api.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace DocumentInvite.Api.Features.Reminders;

public sealed class JobsOptions
{
    public const string Section = "Jobs";

    /// <summary>Read from configuration only (<c>Jobs__ApiKey</c>). Never committed or logged. When empty, every call is refused.</summary>
    public string? ApiKey { get; set; }
}

/// <summary>
/// Protects the internal job endpoints with the <c>X-Api-Key</c> header. The key is compared in
/// constant time (over SHA-256 digests, so the length of the key is not leaked either). A missing,
/// wrong or unconfigured key returns 401. A successful call is attributed to the System actor.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class JobApiKeyAttribute : Attribute, IAuthorizationFilter
{
    public const string HeaderName = "X-Api-Key";

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var configured = context.HttpContext.RequestServices.GetRequiredService<IOptions<JobsOptions>>().Value.ApiKey;
        var supplied = context.HttpContext.Request.Headers[HeaderName].ToString();

        if (!IsValid(configured, supplied))
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        context.HttpContext.Items[HttpRequestContext.SystemActorItemKey] = true;
    }

    public static bool IsValid(string? configured, string? supplied)
    {
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(configured ?? string.Empty));
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(supplied ?? string.Empty));
        var equal = CryptographicOperations.FixedTimeEquals(expected, actual);
        return equal && !string.IsNullOrEmpty(configured);
    }
}
