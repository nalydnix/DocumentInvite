using DocumentInvite.Api.Common;
using DocumentInvite.Api.Common.Paging;
using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Audit;
using DocumentInvite.Api.Features.Invites;
using DocumentInvite.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocumentInvite.Api.Features.Loans;

public sealed record LoanResult(LoanResponse? Loan, bool NotFound, string? Conflict)
{
    public static LoanResult Ok(LoanResponse loan) => new(loan, false, null);
    public static LoanResult Missing() => new(null, true, null);
    public static LoanResult Refused(string reason) => new(null, false, reason);
}

public sealed class LoanService(AppDbContext db, IAuditLog audit, IRequestContext context, TimeProvider clock)
{
    private const string DuplicateReference = "A loan with this reference already exists.";

    public async Task<PagedResponse<LoanSummaryResponse>> ListAsync(LoanListQuery query, CancellationToken ct)
    {
        var loans = db.Loans.AsNoTracking();

        if (query.Status is { } status)
        {
            loans = loans.Where(l => l.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            foreach (var term in query.Search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var pattern = "%" + EscapeLike(term) + "%";
                loans = loans.Where(l =>
                    EF.Functions.Like(l.Reference, pattern, "\\") ||
                    EF.Functions.Like(l.Name, pattern, "\\") ||
                    EF.Functions.Like(l.LoanType, pattern, "\\"));
            }
        }

        // Progress and due date count only active (not revoked) invites. A loan is overdue when an
        // active invite is past its due date and still has items the party has to provide.
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        return await loans
            .Select(l => new
            {
                Loan = l,
                Parties = l.Invites.Count,
                Total = l.Invites.Where(i => i.RevokedAt == null).SelectMany(i => i.Items).Count(),
                Approved = l.Invites.Where(i => i.RevokedAt == null).SelectMany(i => i.Items)
                    .Count(x => x.Status == RequestItemStatus.Approved),
                NextDue = l.Invites.Where(i => i.RevokedAt == null && i.Status != CollectionStatus.Completed)
                    .Min(i => (DateOnly?)i.DueDate),
                Overdue = l.Invites.Any(i => i.RevokedAt == null && i.DueDate < today &&
                    i.Items.Any(x => x.Status == RequestItemStatus.Outstanding || x.Status == RequestItemStatus.Rejected)),
            })
            .OrderByDescending(x => x.Loan.CreatedAt).ThenBy(x => x.Loan.Id)
            .ToPagedResponseAsync(query, x => ToSummary(x.Loan, x.Parties, x.Total, x.Approved, x.NextDue, x.Overdue), ct);
    }

    public async Task<LoanResult> GetAsync(Guid id, CancellationToken ct)
    {
        var loan = await db.Loans.AsNoTracking()
            .Include(l => l.Invites).ThenInclude(i => i.Items)
            .AsSplitQuery()
            .SingleOrDefaultAsync(l => l.Id == id, ct);
        if (loan is null)
        {
            return LoanResult.Missing();
        }

        var contactIds = loan.Invites.Select(i => i.ContactId).ToList();
        var contacts = await db.Contacts.AsNoTracking().Where(c => contactIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var itemIds = loan.Invites.SelectMany(i => i.Items).Select(i => i.Id).ToList();
        var files = await InviteService.LoadFilesAsync(db, itemIds, ct);
        return LoanResult.Ok(ToResponse(loan, contacts, files));
    }

    public async Task<LoanResult> CreateAsync(LoanRequest request, CancellationToken ct)
    {
        var reference = request.Reference!.Trim();
        if (await db.Loans.AnyAsync(l => l.Reference == reference, ct))
        {
            return LoanResult.Refused(DuplicateReference);
        }

        var loan = new Loan
        {
            Reference = reference,
            Name = request.Name!.Trim(),
            Amount = request.Amount!.Value,
            LoanType = request.LoanType!.Trim(),
            CreatedBy = context.ActorId,
            CreatedAt = clock.GetUtcNow(),
        };

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            db.Loans.Add(loan);
            await audit.RecordAsync("loan.created", new AuditTarget("loan", loan.Id), null, ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (Database.IsUniqueViolation(ex))
        {
            return LoanResult.Refused(DuplicateReference);
        }

        return LoanResult.Ok(ToResponse(loan, new Dictionary<Guid, Contact>()));
    }

    /// <summary>Stamps <c>CompletedAt</c> and locks the loan. Allowed only when every item is approved.</summary>
    public async Task<LoanResult> CompleteAsync(Guid id, CancellationToken ct)
    {
        var loan = await LoadForUpdateAsync(id, ct);
        if (loan is null)
        {
            return LoanResult.Missing();
        }

        if (loan.CompletedAt is not null)
        {
            return LoanResult.Refused("The loan is already completed.");
        }

        StatusRecalculator.Apply(loan);
        if (loan.Status != CollectionStatus.ReadyToComplete)
        {
            return LoanResult.Refused("The loan can only be completed when every item on every invite is approved.");
        }

        loan.CompletedAt = clock.GetUtcNow();
        StatusRecalculator.Apply(loan);
        return await SaveAsync(loan, "loan.completed", ct);
    }

    /// <summary>Clears <c>CompletedAt</c>, unlocks the loan and tells each party's thread why.</summary>
    public async Task<LoanResult> ReopenAsync(Guid id, string reason, CancellationToken ct)
    {
        var loan = await LoadForUpdateAsync(id, ct);
        if (loan is null)
        {
            return LoanResult.Missing();
        }

        if (loan.CompletedAt is null)
        {
            return LoanResult.Refused("Only a completed loan can be reopened.");
        }

        var now = clock.GetUtcNow();
        loan.CompletedAt = null;
        StatusRecalculator.Apply(loan);
        foreach (var invite in loan.Invites)
        {
            db.Messages.Add(new Message
            {
                InviteId = invite.Id,
                AuthorType = MessageAuthorType.System,
                Body = $"The loan was reopened: {reason.Trim()}",
                IsInternal = false,
                CreatedAt = now,
            });
        }

        return await SaveAsync(loan, "loan.reopened", ct);
    }

    private Task<Loan?> LoadForUpdateAsync(Guid id, CancellationToken ct) =>
        db.Loans.Include(l => l.Invites).ThenInclude(i => i.Items).AsSplitQuery().SingleOrDefaultAsync(l => l.Id == id, ct);

    private async Task<LoanResult> SaveAsync(Loan loan, string action, CancellationToken ct)
    {
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await audit.RecordAsync(action, new AuditTarget("loan", loan.Id), null, ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return LoanResult.Refused("The loan was changed by someone else. Reload it and try again.");
        }

        return await GetAsync(loan.Id, ct);
    }

    private static LoanSummaryResponse ToSummary(
        Loan l, int partyCount, int total, int approved, DateOnly? dueDate, bool overdue) => new(
        l.Id, l.Reference, l.Name, l.Amount, l.LoanType, l.Status, l.CreatedAt, l.CompletedAt, partyCount,
        total, approved, total == 0 ? 0 : (int)Math.Round(approved * 100.0 / total), dueDate, overdue);

    private static string EscapeLike(string term) =>
        term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");

    private static LoanResponse ToResponse(
        Loan loan, IReadOnlyDictionary<Guid, Contact> contacts, ILookup<Guid, StoredFile>? files = null) => new(
        loan.Id, loan.Reference, loan.Name, loan.Amount, loan.LoanType, loan.Status, loan.CreatedAt, loan.CompletedAt,
        loan.Invites.OrderBy(i => i.SentAt).ThenBy(i => i.Id)
            .Select(i => InviteService.ToResponse(i, contacts[i.ContactId], files)).ToList());
}
