namespace DocumentInvite.Api.Features.Demo;

/// <summary>A demo login shown on the sign-in page.</summary>
public sealed record DemoLogin(string Role, string Email, string Password);

/// <summary>What the sign-in page needs. Empty when demo mode is off.</summary>
public sealed record DemoConfigResponse(bool Enabled, IReadOnlyList<DemoLogin> Logins);

public sealed record DemoResetResponse(int Requests, int Contacts);
