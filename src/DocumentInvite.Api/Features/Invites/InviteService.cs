using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Audit;
using DocumentInvite.Api.Features.Outbox;
using DocumentInvite.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DocumentInvite.Api.Features.Invites;

/// <summary>The outcome of an invite operation.</summary>
public sealed record InviteResult<T>(
    T? Value, bool NotFound, string? Conflict, string? InvalidField, string? InvalidMessage)
{
    public static InviteResult<T> Ok(T value) => new(value, false, null, null, null);
    public static InviteResult<T> Missing() => new(default, true, null, null, null);
    public static InviteResult<T> Refused(string reason) => new(default, false, reason, null, null);
    public static InviteResult<T> Invalid(string field, string message) => new(default, false, null, field, message);
}

public sealed class InviteService(
    AppDbContext db,
    IAuditLog audit,
    IEmailSender email,
    IPortalLinkBuilder links,
    IPasswordHasher<Invite> hasher,
    TimeProvider clock)
{
    /// <summary>How long a link stays valid after the due date, unless it is reissued.</summary>
    public const int LinkValidDaysAfterDueDate = 30;

    public async Task<InviteResult<InviteResponse>> GetAsync(Guid inviteId, CancellationToken ct)
    {
        var invite = await db.Invites.AsNoTracking().Include(i => i.Items).SingleOrDefaultAsync(i => i.Id == inviteId, ct);
        if (invite is null)
        {
            return InviteResult<InviteResponse>.Missing();
        }

        var contact = await db.Contacts.AsNoTracking().SingleAsync(c => c.Id == invite.ContactId, ct);
        return InviteResult<InviteResponse>.Ok(ToResponse(invite, contact));
    }

    /// <summary>Creates one invite per party, emails each party its link, and updates the loan status.</summary>
    public async Task<InviteResult<IReadOnlyList<InviteResponse>>> SendAsync(
        Guid loanId, SendInvitesRequest request, CancellationToken ct)
    {
        var loan = await LoadLoanAsync(db.Loans.Where(l => l.Id == loanId), ct);
        if (loan is null)
        {
            return InviteResult<IReadOnlyList<InviteResponse>>.Missing();
        }

        var parties = request.Parties!;
        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        var contactIds = parties.Select(p => p.ContactId!.Value).ToList();
        var contacts = await db.Contacts.Where(c => contactIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);

        for (var index = 0; index < parties.Count; index++)
        {
            var party = parties[index];
            if (!contacts.TryGetValue(party.ContactId!.Value, out var contact))
            {
                return InviteResult<IReadOnlyList<InviteResponse>>.Invalid(
                    $"parties[{index}].contactId", "The contact does not exist.");
            }

            if (contact.IsPseudonymised)
            {
                return InviteResult<IReadOnlyList<InviteResponse>>.Invalid(
                    $"parties[{index}].contactId", "A pseudonymised contact cannot be invited.");
            }

            if (party.DueDate!.Value < today)
            {
                return InviteResult<IReadOnlyList<InviteResponse>>.Invalid(
                    $"parties[{index}].dueDate", "The due date cannot be in the past.");
            }

            if (loan.Invites.Any(i => i.ContactId == contact.Id))
            {
                return InviteResult<IReadOnlyList<InviteResponse>>.Refused(
                    "A party that already has an invite on this loan cannot be invited again. Reissue the link instead.");
            }
        }

        var defaultInterval = await db.Settings.AsNoTracking()
            .Select(s => s.DefaultReminderIntervalDays).SingleAsync(ct);

        var created = new List<(Invite Invite, Contact Contact, string Token)>();
        foreach (var party in parties)
        {
            var token = PortalTokens.Generate();
            var invite = new Invite
            {
                LoanId = loan.Id,
                ContactId = party.ContactId!.Value,
                DueDate = party.DueDate!.Value,
                ReminderIntervalDays = party.ReminderIntervalDays ?? defaultInterval,
                TokenHash = PortalTokens.Hash(token),
                TokenExpiresAt = DefaultExpiry(party.DueDate.Value),
                SentAt = now,
                Items = party.Items!.Select((item, order) => new RequestItem
                {
                    Name = item.Name!.Trim(),
                    Kind = item.Kind!.Value,
                    Instructions = item.Instructions?.Trim() ?? string.Empty,
                    AllowMultiple = item.AllowMultiple,
                    FieldType = item.FieldType,
                    IsRequired = item.IsRequired,
                    SortOrder = order,
                }).ToList(),
            };
            if (!string.IsNullOrEmpty(party.Password))
            {
                invite.PasswordHash = hasher.HashPassword(invite, party.Password);
            }

            loan.Invites.Add(invite);
            db.Invites.Add(invite);
            created.Add((invite, contacts[invite.ContactId], token));
        }

        StatusRecalculator.Apply(loan, now);

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            foreach (var (invite, contact, token) in created)
            {
                await audit.RecordAsync(
                    "invite.sent", new AuditTarget("invite", invite.Id),
                    AuditDetails.Empty
                        .WithId("loanId", loan.Id)
                        .WithId("contactId", contact.Id)
                        .WithCount("itemCount", invite.Items.Count), ct);
                await SendEmailAsync(invite, contact, loan, token, ct);
            }

            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return InviteResult<IReadOnlyList<InviteResponse>>.Refused(
                "The loan was changed by someone else. Reload it and try again.");
        }
        catch (DbUpdateException ex) when (Database.IsUniqueViolation(ex))
        {
            return InviteResult<IReadOnlyList<InviteResponse>>.Refused(
                "A party that already has an invite on this loan cannot be invited again. Reload and try again.");
        }

        return InviteResult<IReadOnlyList<InviteResponse>>.Ok(
            created.Select(c => ToResponse(c.Invite, c.Contact)).ToList());
    }

    public async Task<InviteResult<InviteResponse>> RevokeAsync(Guid inviteId, CancellationToken ct)
    {
        var loan = await LoadLoanAsync(db.Loans.Where(l => l.Invites.Any(i => i.Id == inviteId)), ct);
        var invite = loan?.Invites.SingleOrDefault(i => i.Id == inviteId);
        if (loan is null || invite is null)
        {
            return InviteResult<InviteResponse>.Missing();
        }

        if (invite.RevokedAt is not null)
        {
            return InviteResult<InviteResponse>.Refused("The invite is already revoked.");
        }

        var now = clock.GetUtcNow();
        invite.RevokedAt = now;
        StatusRecalculator.Apply(loan, now);

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await audit.RecordAsync(
                "invite.revoked", new AuditTarget("invite", invite.Id),
                AuditDetails.Empty.WithId("loanId", loan.Id), ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return InviteResult<InviteResponse>.Refused("The invite was changed by someone else. Reload it and try again.");
        }

        return InviteResult<InviteResponse>.Ok(
            ToResponse(invite, await db.Contacts.AsNoTracking().SingleAsync(c => c.Id == invite.ContactId, ct)));
    }

    /// <summary>
    /// Replaces the invite's token and emails the new link. The previous link stops working at once
    /// because only the new token's hash is kept. A revoked invite becomes active again.
    /// </summary>
    public async Task<InviteResult<InviteResponse>> ReissueAsync(Guid inviteId, CancellationToken ct)
    {
        var loan = await LoadLoanAsync(db.Loans.Where(l => l.Invites.Any(i => i.Id == inviteId)), ct);
        var invite = loan?.Invites.SingleOrDefault(i => i.Id == inviteId);
        if (loan is null || invite is null)
        {
            return InviteResult<InviteResponse>.Missing();
        }

        if (invite.Status == CollectionStatus.Completed)
        {
            return InviteResult<InviteResponse>.Refused("A completed invite cannot be reissued.");
        }

        var contact = await db.Contacts.SingleAsync(c => c.Id == invite.ContactId, ct);
        if (contact.IsPseudonymised)
        {
            return InviteResult<InviteResponse>.Refused("A pseudonymised contact cannot be invited.");
        }

        var now = clock.GetUtcNow();
        var expiry = DefaultExpiry(invite.DueDate);
        var token = PortalTokens.Generate();
        invite.TokenHash = PortalTokens.Hash(token);
        invite.TokenExpiresAt = expiry > now ? expiry : now.AddDays(LinkValidDaysAfterDueDate);
        invite.RevokedAt = null;
        invite.SentAt = now;
        invite.LastReminderAt = null;
        StatusRecalculator.Apply(loan, now);

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await audit.RecordAsync(
                "invite.reissued", new AuditTarget("invite", invite.Id),
                AuditDetails.Empty.WithId("loanId", loan.Id), ct);
            await SendEmailAsync(invite, contact, loan, token, ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return InviteResult<InviteResponse>.Refused("The invite was changed by someone else. Reload it and try again.");
        }

        return InviteResult<InviteResponse>.Ok(ToResponse(invite, contact));
    }

    /// <summary>Thirty days after the due date (midnight UTC).</summary>
    internal static DateTimeOffset DefaultExpiry(DateOnly dueDate) =>
        new DateTimeOffset(dueDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            .AddDays(LinkValidDaysAfterDueDate);

    private Task SendEmailAsync(Invite invite, Contact contact, Loan loan, string token, CancellationToken ct)
    {
        var content = EmailTemplates.Invite(new InviteEmailModel(
            contact.FirstName, loan.Name, links.For(token), invite.DueDate, invite.Items.Count));
        return email.SendAsync(EmailMessage.From(contact.Email, content), ct);
    }

    private static Task<Loan?> LoadLoanAsync(IQueryable<Loan> query, CancellationToken ct) =>
        query.Include(l => l.Invites).ThenInclude(i => i.Items).AsSplitQuery().SingleOrDefaultAsync(ct);

    internal static InviteResponse ToResponse(Invite invite, Contact contact) => new(
        invite.Id, invite.LoanId, invite.ContactId,
        $"{contact.FirstName} {contact.LastName}", contact.Email, contact.PartyType,
        invite.DueDate, invite.ReminderIntervalDays, invite.SentAt, invite.TokenExpiresAt, invite.RevokedAt,
        invite.Status, invite.PasswordHash is not null,
        invite.Items.OrderBy(i => i.SortOrder).Select(i => new InviteItemResponse(
            i.Id, i.Name, i.Kind, i.Instructions, i.AllowMultiple, i.FieldType, i.IsRequired, i.SortOrder, i.Status))
            .ToList());
}
