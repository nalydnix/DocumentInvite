using System.Text.RegularExpressions;
using DocumentInvite.Api.Common;
using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocumentInvite.Api.Features.Audit;

public sealed partial class AuditLog(AppDbContext db, IRequestContext context, TimeProvider clock) : IAuditLog
{
    private const string AcquireLockSql =
        "DECLARE @result int; EXEC @result = sp_getapplock @Resource = N'DocumentInvite.AuditChain', " +
        "@LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 30000; " +
        "IF @result < 0 THROW 50000, 'Could not acquire the audit chain lock.', 1;";

    public async Task RecordAsync(
        string action, AuditTarget target, AuditDetails? details = null, CancellationToken cancellationToken = default)
    {
        if (action is null || !ActionPattern().IsMatch(action))
        {
            throw new ArgumentException("Audit actions are dotted lower-case codes such as 'item.approved'.", nameof(action));
        }

        if (target.Type is null || !TypePattern().IsMatch(target.Type))
        {
            throw new ArgumentException("Audit target types are lower-case identifiers such as 'loan'.", nameof(target));
        }

        var auditEvent = new AuditEvent
        {
            OccurredAt = clock.GetUtcNow(),
            ActorType = context.ActorType,
            ActorId = context.ActorId,
            Action = action,
            TargetType = target.Type,
            TargetId = target.Id,
            IpAddress = Truncate(context.IpAddress, 45),
            UserAgent = Truncate(context.UserAgent, 512),
            CorrelationId = Truncate(context.CorrelationId, 100)!,
            DetailsJson = (details ?? AuditDetails.Empty).ToJson(),
        };

        var ownTransaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            // The lock is released when the transaction ends, so concurrent appends queue up here.
            await db.Database.ExecuteSqlRawAsync(AcquireLockSql, cancellationToken);

            var prevHash = await db.AuditEvents.AsNoTracking()
                .OrderByDescending(x => x.Id)
                .Select(x => x.Hash)
                .FirstOrDefaultAsync(cancellationToken) ?? AuditHasher.GenesisHash;

            auditEvent.PrevHash = prevHash;
            auditEvent.Hash = AuditHasher.ComputeHash(prevHash, auditEvent);

            db.AuditEvents.Add(auditEvent);
            await db.SaveChangesAsync(cancellationToken);

            if (ownTransaction is not null)
            {
                await ownTransaction.CommitAsync(cancellationToken);
            }
        }
        finally
        {
            if (ownTransaction is not null)
            {
                await ownTransaction.DisposeAsync();
            }
        }
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];

    [GeneratedRegex(@"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$")]
    private static partial Regex ActionPattern();

    [GeneratedRegex(@"^[a-z][a-z0-9_]*$")]
    private static partial Regex TypePattern();
}
