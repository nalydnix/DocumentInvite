using Microsoft.AspNetCore.Identity;

namespace DocumentInvite.Api.Domain;

/// <summary>A staff account (Admin, LoanOfficer or Auditor).</summary>
public class AppUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// When set, sign-in and every authenticated request are rejected from this moment on.
    /// Admins set it for Auditor accounts.
    /// </summary>
    public DateTimeOffset? AccessExpiresAt { get; set; }
}

public class AppRole : IdentityRole<Guid>
{
    public AppRole()
    {
    }

    public AppRole(string name) : base(name)
    {
    }
}
