namespace DocumentInvite.Api.Features.Audit;

/// <summary>The thing an audit event is about, for example <c>("loan", loanId)</c>.</summary>
public readonly record struct AuditTarget(string Type, Guid? Id = null);

/// <summary>The only way to write audit events.</summary>
public interface IAuditLog
{
    /// <summary>
    /// Appends an event to the hash chain. Runs inside the current database transaction when one
    /// is open (the chain lock is then held until that transaction ends), otherwise in its own.
    /// Pending changes on the shared <c>AppDbContext</c> are saved with the event.
    /// </summary>
    /// <param name="action">A dotted lower-case code such as <c>item.approved</c>.</param>
    Task RecordAsync(string action, AuditTarget target, AuditDetails? details = null, CancellationToken cancellationToken = default);
}

public interface IAuditVerifier
{
    Task<AuditVerificationResult> VerifyAsync(CancellationToken cancellationToken = default);
}

/// <param name="IsValid">True when every event links to the previous one and hashes correctly.</param>
/// <param name="FirstBrokenEventId">The id of the first event that fails verification, if any.</param>
/// <param name="EventsChecked">How many events were examined.</param>
public sealed record AuditVerificationResult(bool IsValid, long? FirstBrokenEventId, long EventsChecked);
