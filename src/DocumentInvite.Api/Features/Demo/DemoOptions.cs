namespace DocumentInvite.Api.Features.Demo;

/// <summary>Demo mode settings. Credentials come from configuration only (for example <c>Demo__Accounts__Admin__Password</c>).</summary>
public sealed class DemoOptions
{
    public const string Section = "Demo";

    public bool Enabled { get; set; }

    /// <summary>Keyed by role name: Admin, LoanOfficer, Viewer, Auditor.</summary>
    public Dictionary<string, DemoAccountOptions> Accounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class DemoAccountOptions
{
    public string? Email { get; set; }
    public string? Password { get; set; }

    public bool IsComplete => !string.IsNullOrWhiteSpace(Email) && !string.IsNullOrWhiteSpace(Password);
}
