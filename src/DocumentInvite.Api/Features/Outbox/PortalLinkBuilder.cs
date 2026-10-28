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
        return $"{Origin()}/p/{Uri.EscapeDataString(token)}";
    }

    public string ForLoan(Guid loanId) => $"{Origin()}/loans/{loanId:D}";

    private string Origin()
    {
        var baseUrl = options.Value.BaseUrl;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException(
                "Portal:BaseUrl must be set to the web app origin (for example Portal__BaseUrl=http://localhost:5173) to build e-mail links.");
        }

        return baseUrl.TrimEnd('/');
    }
}
