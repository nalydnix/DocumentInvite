namespace DocumentInvite.Api.Features.Auth;

/// <summary>The staff roles, as stored in the Identity role table.</summary>
public static class StaffRoles
{
    public const string Admin = nameof(StaffRole.Admin);
    public const string LoanOfficer = nameof(StaffRole.LoanOfficer);
    public const string Viewer = nameof(StaffRole.Viewer);
    public const string Auditor = nameof(StaffRole.Auditor);

    public static readonly IReadOnlyList<string> All = Enum.GetNames<StaffRole>();
}

/// <summary>Role names as an enum, so they can be recorded in audit details.</summary>
public enum StaffRole
{
    Admin,
    LoanOfficer,
    Viewer,
    Auditor,
}

public enum RoleChange
{
    Added,
    Removed,
}

public enum ExpiryChange
{
    Set,
    Cleared,
}

public enum SignInFailure
{
    InvalidCredentials,
    LockedOut,
    NotAllowed,
    AccessExpired,
}

/// <summary>Authorisation policy names (see architecture section 5).</summary>
public static class AuthPolicies
{
    public const string RequireAdmin = nameof(RequireAdmin);
    public const string RequireStaff = nameof(RequireStaff);
    public const string CanRead = nameof(CanRead);
    public const string CanAudit = nameof(CanAudit);
}
