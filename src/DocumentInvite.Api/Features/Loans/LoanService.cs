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

        return await loans
            .Select(l => new { Loan = l, Parties = l.Invites.Count })
            .OrderByDescending(x => x.Loan.CreatedAt).ThenBy(x => x.Loan.Id)
            .ToPagedResponseAsync(query, x => ToSummary(x.Loan, x.Parties), ct);
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
        return LoanResult.Ok(ToResponse(loan, contacts));
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

    private static LoanSummaryResponse ToSummary(Loan l, int partyCount) => new(
        l.Id, l.Reference, l.Name, l.Amount, l.LoanType, l.Status, l.CreatedAt, l.CompletedAt, partyCount);

    private static string EscapeLike(string term) =>
        term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");

    private static LoanResponse ToResponse(Loan loan, IReadOnlyDictionary<Guid, Contact> contacts) => new(
        loan.Id, loan.Reference, loan.Name, loan.Amount, loan.LoanType, loan.Status, loan.CreatedAt, loan.CompletedAt,
        loan.Invites.OrderBy(i => i.SentAt).ThenBy(i => i.Id)
            .Select(i => InviteService.ToResponse(i, contacts[i.ContactId])).ToList());
}
