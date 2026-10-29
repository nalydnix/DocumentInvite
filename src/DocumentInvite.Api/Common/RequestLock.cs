namespace DocumentInvite.Api.Common;

/// <summary>A completed request is locked: nothing on it changes until staff reopen it.</summary>
public static class RequestLock
{
    public const string Message = "This request is completed and locked. Reopen the request before making changes.";
}
