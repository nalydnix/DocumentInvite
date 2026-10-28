using DocumentInvite.Api.Domain;

namespace DocumentInvite.Api.Features.Outbox;

public sealed class EmailOptions
{
    public const string Section = "Email";

    /// <summary>Which sender is used. <c>Demo</c> (the default) only stores emails.</summary>
    public EmailProvider Provider { get; set; } = EmailProvider.Demo;

    /// <summary>The From address used by real providers.</summary>
    public string From { get; set; } = "DocumentInvite <onboarding@resend.dev>";
}

public sealed class ResendOptions
{
    public const string Section = "Resend";

    /// <summary>Read from configuration only (<c>Resend__ApiKey</c>). Never committed or logged.</summary>
    public string? ApiKey { get; set; }

    public string BaseUrl { get; set; } = "https://api.resend.com";
}

public sealed class PortalOptions
{
    public const string Section = "Portal";

    /// <summary>
    /// The web app origin that portal links point at (<c>Portal__BaseUrl</c>). Set per instance; there is no default
    /// because the web port varies (see the README's side-by-side section).
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;
}
