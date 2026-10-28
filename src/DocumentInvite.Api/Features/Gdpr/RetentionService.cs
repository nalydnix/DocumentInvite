using DocumentInvite.Api.Common.Paging;
using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Audit;
using DocumentInvite.Api.Infrastructure.Blob;
using DocumentInvite.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocumentInvite.Api.Features.Gdpr;

/// <summary>
/// The retention purge: loans completed before the cut-off are deleted together with their files, and
/// contacts left without any loan are pseudonymised exactly as an erasure does. Audit rows are never touched.
/// </summary>
public sealed class RetentionService(AppDbContext db, IBlobStore blobs, IAuditLog audit, TimeProvider clock)
{
    private const string Stale = "A loan changed while the purge was running. Nothing was deleted; try again.";

    /// <summary>Lists what would be purged. Changes no data; the preview itself is recorded in the audit log.</summary>
    public async Task<RetentionPreviewResponse> PreviewAsync(PageQuery query, CancellationToken ct)
    {
        var plan = await PlanAsync(tracking: false, ct);

        await audit.RecordAsync(
            "retention.previewed", new AuditTarget("retention"),
            AuditDetails.Empty.WithCount("loans", plan.Loans.Count).WithCount("files", plan.BlobNames.Count)
                .WithCount("contacts", plan.Contacts.Count), ct);

        var page = plan.Loans
            .Skip(PagingExtensions.Offset(query.Page, query.PageSize)).Take(query.PageSize)
            .Select(l => new RetentionLoan(l.Id, l.Reference, l.Name, l.CompletedAt!.Value, plan.FileCounts.GetValueOrDefault(l.Id)))
            .ToList();

        return new RetentionPreviewResponse(
            plan.Days, plan.Cutoff, plan.Loans.Count, plan.BlobNames.Count, plan.Contacts.Count,
            new PagedResponse<RetentionLoan>(page, plan.Loans.Count, query.Page, query.PageSize));
    }

    public async Task<GdprResult<RetentionRunResponse>> RunAsync(CancellationToken ct)
    {
        var plan = await PlanAsync(tracking: true, ct);
        var now = clock.GetUtcNow();

        foreach (var contact in plan.Contacts)
        {
            GdprService.Pseudonymise(contact, now);
        }

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            // Invites, items, files and messages go with the loan through the database cascade.
            db.Loans.RemoveRange(plan.Loans);
            foreach (var loan in plan.Loans)
            {
                await audit.RecordAsync("loan.purged", new AuditTarget("loan", loan.Id), cancellationToken: ct);
            }

            foreach (var contact in plan.Contacts)
            {
                await audit.RecordAsync("contact.pseudonymised", new AuditTarget("contact", contact.Id), cancellationToken: ct);
            }

            // Every run is recorded, including one that finds nothing to do.
            await audit.RecordAsync(
                "retention.run", new AuditTarget("retention"),
                AuditDetails.Empty.WithCount("loans", plan.Loans.Count).WithCount("files", plan.BlobNames.Count)
                    .WithCount("contacts", plan.Contacts.Count), ct);

            // Blobs go before the commit: if deletion fails nothing is recorded as purged and the run can be repeated.
            foreach (var blob in plan.BlobNames)
            {
                await blobs.DeleteAsync(blob, ct);
            }

            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return GdprResult<RetentionRunResponse>.Refused(Stale);
        }

        return GdprResult<RetentionRunResponse>.Ok(new RetentionRunResponse(
            plan.Days, plan.Cutoff, plan.Loans.Count, plan.BlobNames.Count, plan.Contacts.Count));
    }

    private sealed record Plan(
        int Days,
        DateTimeOffset Cutoff,
        List<Loan> Loans,
        List<string> BlobNames,
        Dictionary<Guid, int> FileCounts,
        List<Contact> Contacts);

    /// <summary>Works out what is past the cut-off. Loans are ordered oldest completion first, then by id.</summary>
    private async Task<Plan> PlanAsync(bool tracking, CancellationToken ct)
    {
        var days = await db.Settings.AsNoTracking()
            .Where(s => s.Id == AppSettings.SingletonId).Select(s => s.RetentionDaysAfterCompletion).SingleAsync(ct);
        var cutoff = clock.GetUtcNow().AddDays(-days);

        var loansQuery = db.Loans.Where(l => l.CompletedAt != null && l.CompletedAt < cutoff)
            .OrderBy(l => l.CompletedAt).ThenBy(l => l.Id);
        var loans = await (tracking ? loansQuery : loansQuery.AsNoTracking()).ToListAsync(ct);
        var loanIds = loans.Select(l => l.Id).ToList();

        var files = await (
            from f in db.StoredFiles.AsNoTracking()
            join i in db.RequestItems.AsNoTracking() on f.RequestItemId equals i.Id
            join v in db.Invites.AsNoTracking() on i.InviteId equals v.Id
            where loanIds.Contains(v.LoanId)
            orderby f.Id
            select new { v.LoanId, f.BlobName }).ToListAsync(ct);

        // Contacts on a purged loan with no invite on any loan that stays. Contacts that never joined a
        // loan are left alone, and already-pseudonymised ones are skipped, so a repeat run changes nothing.
        var candidateIds = await db.Invites.AsNoTracking()
            .Where(i => loanIds.Contains(i.LoanId)).Select(i => i.ContactId).Distinct().ToListAsync(ct);
        var stillInUse = await db.Invites.AsNoTracking()
            .Where(i => candidateIds.Contains(i.ContactId) && !loanIds.Contains(i.LoanId))
            .Select(i => i.ContactId).Distinct().ToListAsync(ct);
        var contactIds = candidateIds.Except(stillInUse).ToList();
        var contactsQuery = db.Contacts.Where(c => contactIds.Contains(c.Id) && c.ErasedAt == null)
            .OrderBy(c => c.Id);
        var contacts = await (tracking ? contactsQuery : contactsQuery.AsNoTracking()).ToListAsync(ct);

        return new Plan(
            days, cutoff, loans,
            files.Select(f => f.BlobName).ToList(),
            files.GroupBy(f => f.LoanId).ToDictionary(g => g.Key, g => g.Count()),
            contacts);
    }
}
