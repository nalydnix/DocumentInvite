using DocumentInvite.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DocumentInvite.Api.Infrastructure.Persistence;

public sealed class AuditImmutableException()
    : InvalidOperationException("Audit events are append-only and cannot be modified or deleted.");

/// <summary>Rejects any save that would modify or delete an <see cref="AuditEvent"/>.</summary>
public sealed class AuditImmutabilityInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Guard(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Guard(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private static void Guard(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        if (context.ChangeTracker.Entries<AuditEvent>()
            .Any(x => x.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new AuditImmutableException();
        }
    }
}
