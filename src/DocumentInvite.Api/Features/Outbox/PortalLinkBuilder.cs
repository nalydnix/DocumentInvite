using Microsoft.Extensions.Options;

namespace DocumentInvite.Api.Features.Outbox;

public interface IPortalLinkBuilder
{
    /// <summary>The link a party opens, <c>{Portal:BaseUrl}/p/{token}</c>.</summary>
    string For(string token);
}

public sealed class PortalLinkBuilder(IOptions<PortalOptions> options) : IPortalLinkBuilder
{
    public string For(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return $"{options.Value.BaseUrl.TrimEnd('/')}/p/{Uri.EscapeDataString(token)}";
    }
}
