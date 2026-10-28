namespace DocumentInvite.Api.Common;

/// <summary>A completed loan is locked: nothing on it changes until staff reopen it.</summary>
public static class LoanLock
{
    public const string Message = "This loan is completed and locked. Reopen the loan before making changes.";
}
