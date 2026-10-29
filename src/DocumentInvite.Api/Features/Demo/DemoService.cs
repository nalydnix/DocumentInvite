using DocumentInvite.Api.Features.Audit;
using DocumentInvite.Api.Features.Auth;
using DocumentInvite.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DocumentInvite.Api.Features.Demo;

public sealed class DemoService(
    AppDbContext db, DemoSeeder seeder, IAuditLog audit, IOptions<DemoOptions> options, TimeProvider clock)
{
    public DemoConfigResponse GetConfig()
    {
        if (!options.Value.Enabled)
        {
            return new DemoConfigResponse(false, []);
        }

        var logins = new List<DemoLogin>();
        foreach (var role in new[] { StaffRoles.Admin, StaffRoles.Requester, StaffRoles.Auditor })
        {
            if (options.Value.Accounts.TryGetValue(role, out var account) && account.IsComplete)
            {
                logins.Add(new DemoLogin(role, account.Email!, account.Password!));
            }
        }

        return new DemoConfigResponse(true, logins);
    }

    /// <summary>
    /// Deletes the requests, contacts, emails and files, then seeds them again. The audit log is kept
    /// and the reset is recorded in it. Returns null when demo mode is off.
    /// </summary>
    public async Task<DemoResetResponse?> ResetAsync(CancellationToken ct)
    {
        if (!options.Value.Enabled)
        {
            return null;
        }

        var seedAudit = new AuditLog(db, new SystemRequestContext(), clock);
        await seeder.SeedUsersAsync(seedAudit, ct);

        var oldBlobs = await db.StoredFiles.AsNoTracking().Select(f => f.BlobName).ToListAsync(ct);
        var newBlobs = new List<string>();

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            // Invites, items, files and messages are removed by the database cascade.
            await db.Requests.ExecuteDeleteAsync(ct);
            await db.Contacts.ExecuteDeleteAsync(ct);
            await db.OutboxEmails.ExecuteDeleteAsync(ct);
            db.ChangeTracker.Clear();

            await seeder.SeedRequestsAsync(seedAudit, newBlobs, ct);

            await audit.RecordAsync(
                "demo.reset", new AuditTarget("demo"),
                AuditDetails.Empty.WithCount("requestCount", DemoSeeder.References.Length), ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await seeder.DeleteBlobsAsync(newBlobs);
            throw;
        }

        await seeder.DeleteBlobsAsync(oldBlobs);
        var contacts = await db.Contacts.CountAsync(ct);
        return new DemoResetResponse(DemoSeeder.References.Length, contacts);
    }
}
