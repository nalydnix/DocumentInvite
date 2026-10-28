using Microsoft.Extensions.Options;

namespace DocumentInvite.Api.Features.Outbox;

public interface IPortalLinkBuilder
{
    /// <summary>The link a party opens, <c>{Portal:BaseUrl}/p/{token}</c>.</summary>
    string For(string token);

    /// <summary>The link a staff member opens to see a loan, <c>{Portal:BaseUrl}/loans/{loanId}</c>.</summary>
    string ForLoan(Guid loanId);
}

public sealed class PortalLinkBuilder(IOptions<PortalOptions> options) : IPortalLinkBuilder
{
    public string For(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return $"{options.Value.BaseUrl.TrimEnd('/')}/p/{Uri.EscapeDataString(token)}";
    }

    public string ForLoan(Guid loanId) => $"{options.Value.BaseUrl.TrimEnd('/')}/loans/{loanId:D}";
}
