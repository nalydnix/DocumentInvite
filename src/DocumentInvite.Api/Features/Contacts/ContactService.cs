using DocumentInvite.Api.Common.Paging;
using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Audit;
using DocumentInvite.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocumentInvite.Api.Features.Contacts;

/// <summary>The outcome of a write: a value, "not found", or a reason the change is refused.</summary>
public sealed record ContactResult(ContactResponse? Contact, bool NotFound, string? Conflict)
{
    public static ContactResult Ok(ContactResponse contact) => new(contact, false, null);
    public static ContactResult Missing() => new(null, true, null);
    public static ContactResult Refused(string reason) => new(null, false, reason);
}

public sealed class ContactService(AppDbContext db, IAuditLog audit, TimeProvider clock)
{
    public async Task<PagedResponse<ContactResponse>> ListAsync(ContactListQuery query, CancellationToken ct)
    {
        var contacts = db.Contacts.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            // Every word must match the name or the email, so "ann lee" finds Ann Lee.
            foreach (var term in query.Search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var pattern = "%" + EscapeLike(term) + "%";
                contacts = contacts.Where(c =>
                    EF.Functions.Like(c.FirstName, pattern, "\\") ||
                    EF.Functions.Like(c.LastName, pattern, "\\") ||
                    EF.Functions.Like(c.Email, pattern, "\\"));
            }
        }

        return await contacts
            .OrderBy(c => c.LastName).ThenBy(c => c.FirstName).ThenBy(c => c.Id)
            .ToPagedResponseAsync(query, ToResponse, ct);
    }

    public async Task<ContactResult> GetAsync(Guid id, CancellationToken ct)
    {
        var contact = await db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        return contact is null ? ContactResult.Missing() : ContactResult.Ok(ToResponse(contact));
    }

    public async Task<ContactResult> CreateAsync(ContactRequest request, CancellationToken ct)
    {
        var contact = new Contact { CreatedAt = clock.GetUtcNow() };
        Apply(contact, request);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.Contacts.Add(contact);
        await audit.RecordAsync(
            "contact.created", new AuditTarget("contact", contact.Id),
            AuditDetails.Empty.WithEnum("partyType", contact.PartyType), ct);
        await transaction.CommitAsync(ct);

        return ContactResult.Ok(ToResponse(contact));
    }

    public async Task<ContactResult> UpdateAsync(Guid id, ContactRequest request, CancellationToken ct)
    {
        var contact = await db.Contacts.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (contact is null)
        {
            return ContactResult.Missing();
        }

        if (contact.IsPseudonymised)
        {
            return ContactResult.Refused("A pseudonymised contact can no longer be edited.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        Apply(contact, request);
        await audit.RecordAsync(
            "contact.updated", new AuditTarget("contact", contact.Id),
            AuditDetails.Empty.WithEnum("partyType", contact.PartyType), ct);
        await transaction.CommitAsync(ct);

        return ContactResult.Ok(ToResponse(contact));
    }

    private static void Apply(Contact contact, ContactRequest request)
    {
        contact.FirstName = request.FirstName!.Trim();
        contact.LastName = request.LastName!.Trim();
        contact.Email = request.Email!.Trim();
        contact.Phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim();
        contact.PartyType = request.PartyType!.Value;
    }

    private static string EscapeLike(string term) =>
        term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");

    private static ContactResponse ToResponse(Contact c) =>
        new(c.Id, c.FirstName, c.LastName, c.Email, c.Phone, c.PartyType, c.IsPseudonymised);
}
