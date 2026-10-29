using DocumentInvite.Api.Common;
using DocumentInvite.Api.Common.Paging;
using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Audit;
using DocumentInvite.Api.Features.Invites;
using DocumentInvite.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocumentInvite.Api.Features.Requests;

public sealed record RequestResult(RequestResponse? Request, bool NotFound, string? Conflict)
{
    public static RequestResult Ok(RequestResponse request) => new(request, false, null);
    public static RequestResult Missing() => new(null, true, null);
    public static RequestResult Refused(string reason) => new(null, false, reason);
}

public sealed class RequestService(AppDbContext db, IAuditLog audit, IRequestContext context, TimeProvider clock)
{
    private const string DuplicateReference = "A request with this reference already exists.";

    public async Task<PagedResponse<RequestSummaryResponse>> ListAsync(RequestListQuery query, CancellationToken ct)
    {
        var requests = db.Requests.AsNoTracking();

        if (query.Status is { } status)
        {
            requests = requests.Where(l => l.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            foreach (var term in query.Search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var pattern = "%" + EscapeLike(term) + "%";
                requests = requests.Where(l =>
                    EF.Functions.Like(l.Reference, pattern, "\\") ||
                    EF.Functions.Like(l.Name, pattern, "\\") ||
                    EF.Functions.Like(l.RequestType, pattern, "\\"));
            }
        }

        // Progress and due date count only active (not revoked) invites. A request is overdue when an
        // active invite is past its due date and still has items the party has to provide.
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        return await requests
            .Select(l => new
            {
                Request = l,
                Parties = l.Invites.Count,
                Total = l.Invites.Where(i => i.RevokedAt == null).SelectMany(i => i.Items).Count(),
                Approved = l.Invites.Where(i => i.RevokedAt == null).SelectMany(i => i.Items)
                    .Count(x => x.Status == RequestItemStatus.Approved),
                NextDue = l.Invites.Where(i => i.RevokedAt == null && i.Status != CollectionStatus.Completed)
                    .Min(i => (DateOnly?)i.DueDate),
                Overdue = l.Invites.Any(i => i.RevokedAt == null && i.DueDate < today &&
                    i.Items.Any(x => x.Status == RequestItemStatus.Outstanding || x.Status == RequestItemStatus.Rejected)),
            })
            .OrderByDescending(x => x.Request.CreatedAt).ThenBy(x => x.Request.Id)
            .ToPagedResponseAsync(query, x => ToSummary(x.Request, x.Parties, x.Total, x.Approved, x.NextDue, x.Overdue), ct);
    }

    public async Task<RequestResult> GetAsync(Guid id, CancellationToken ct)
    {
        var request = await db.Requests.AsNoTracking()
            .Include(l => l.Invites).ThenInclude(i => i.Items)
            .AsSplitQuery()
            .SingleOrDefaultAsync(l => l.Id == id, ct);
        if (request is null)
        {
            return RequestResult.Missing();
        }

        var contactIds = request.Invites.Select(i => i.ContactId).ToList();
        var contacts = await db.Contacts.AsNoTracking().Where(c => contactIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var itemIds = request.Invites.SelectMany(i => i.Items).Select(i => i.Id).ToList();
        var files = await InviteService.LoadFilesAsync(db, itemIds, ct);
        return RequestResult.Ok(ToResponse(request, contacts, files));
    }

    public async Task<RequestResult> CreateAsync(SaveRequestRequest input, CancellationToken ct)
    {
        var reference = input.Reference!.Trim();
        if (await db.Requests.AnyAsync(l => l.Reference == reference, ct))
        {
            return RequestResult.Refused(DuplicateReference);
        }

        var request = new Request
        {
            Reference = reference,
            Name = input.Name!.Trim(),
            Amount = input.Amount!.Value,
            RequestType = input.RequestType!.Trim(),
            CreatedBy = context.ActorId,
            CreatedAt = clock.GetUtcNow(),
        };

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            db.Requests.Add(request);
            await audit.RecordAsync("request.created", new AuditTarget("request", request.Id), null, ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (Database.IsUniqueViolation(ex))
        {
            return RequestResult.Refused(DuplicateReference);
        }

        return RequestResult.Ok(ToResponse(request, new Dictionary<Guid, Contact>()));
    }

    /// <summary>Stamps <c>CompletedAt</c> and locks the request. Allowed only when every item is approved.</summary>
    public async Task<RequestResult> CompleteAsync(Guid id, CancellationToken ct)
    {
        var request = await LoadForUpdateAsync(id, ct);
        if (request is null)
        {
            return RequestResult.Missing();
        }

        if (request.CompletedAt is not null)
        {
            return RequestResult.Refused("The request is already completed.");
        }

        StatusRecalculator.Apply(request);
        if (request.Status != CollectionStatus.ReadyToComplete)
        {
            return RequestResult.Refused("The request can only be completed when every item on every invite is approved.");
        }

        request.CompletedAt = clock.GetUtcNow();
        StatusRecalculator.Apply(request);
        return await SaveAsync(request, "request.completed", ct);
    }

    /// <summary>Clears <c>CompletedAt</c>, unlocks the request and tells each party's thread why.</summary>
    public async Task<RequestResult> ReopenAsync(Guid id, string reason, CancellationToken ct)
    {
        var request = await LoadForUpdateAsync(id, ct);
        if (request is null)
        {
            return RequestResult.Missing();
        }

        if (request.CompletedAt is null)
        {
            return RequestResult.Refused("Only a completed request can be reopened.");
        }

        var now = clock.GetUtcNow();
        request.CompletedAt = null;
        StatusRecalculator.Apply(request);
        foreach (var invite in request.Invites)
        {
            db.Messages.Add(new Message
            {
                InviteId = invite.Id,
                AuthorType = MessageAuthorType.System,
                Body = $"The request was reopened: {reason.Trim()}",
                IsInternal = false,
                CreatedAt = now,
            });
        }

        return await SaveAsync(request, "request.reopened", ct);
    }

    private Task<Request?> LoadForUpdateAsync(Guid id, CancellationToken ct) =>
        db.Requests.Include(l => l.Invites).ThenInclude(i => i.Items).AsSplitQuery().SingleOrDefaultAsync(l => l.Id == id, ct);

    private async Task<RequestResult> SaveAsync(Request request, string action, CancellationToken ct)
    {
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await audit.RecordAsync(action, new AuditTarget("request", request.Id), null, ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return RequestResult.Refused("The request was changed by someone else. Reload it and try again.");
        }

        return await GetAsync(request.Id, ct);
    }

    private static RequestSummaryResponse ToSummary(
        Request l, int partyCount, int total, int approved, DateOnly? dueDate, bool overdue) => new(
        l.Id, l.Reference, l.Name, l.Amount, l.RequestType, l.Status, l.CreatedAt, l.CompletedAt, partyCount,
        total, approved, total == 0 ? 0 : (int)Math.Round(approved * 100.0 / total), dueDate, overdue);

    private static string EscapeLike(string term) =>
        term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");

    private static RequestResponse ToResponse(
        Request request, IReadOnlyDictionary<Guid, Contact> contacts, ILookup<Guid, StoredFile>? files = null) => new(
        request.Id, request.Reference, request.Name, request.Amount, request.RequestType, request.Status, request.CreatedAt, request.CompletedAt,
        request.Invites.OrderBy(i => i.SentAt).ThenBy(i => i.Id)
            .Select(i => InviteService.ToResponse(i, contacts[i.ContactId], files)).ToList());
}
