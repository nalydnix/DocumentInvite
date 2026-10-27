using DocumentInvite.Api.Features.Auth;
using DocumentInvite.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DocumentInvite.Api.Features.Outbox;

public sealed record OutboxEmailSummary(
    Guid Id, string To, string Subject, DateTimeOffset CreatedAt, string Provider, string? DeliveryError);

public sealed record OutboxEmailDetail(
    Guid Id, string To, string Subject, DateTimeOffset CreatedAt, string Provider, string? ProviderMessageId,
    string? DeliveryError, string HtmlBody, string TextBody);

public sealed record OutboxPage(IReadOnlyList<OutboxEmailSummary> Items, int Total);

// The outbox holds live portal links, so only staff who can act for a party may read it.
[ApiController]
[Route("api/outbox")]
[Authorize(Policy = AuthPolicies.RequireStaff)]
public class OutboxController(AppDbContext db) : ControllerBase
{
    private const int MaxTake = 100;

    /// <summary>Emails, newest first.</summary>
    [HttpGet]
    public async Task<OutboxPage> List(int skip = 0, int take = 50, CancellationToken ct = default)
    {
        skip = Math.Max(skip, 0);
        take = Math.Clamp(take, 1, MaxTake);

        var total = await db.OutboxEmails.CountAsync(ct);
        var items = await db.OutboxEmails.AsNoTracking()
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Skip(skip).Take(take)
            .Select(x => new OutboxEmailSummary(
                x.Id, x.To, x.Subject, x.CreatedAt, x.Provider.ToString(), x.DeliveryError))
            .ToListAsync(ct);
        return new OutboxPage(items, total);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var email = await db.OutboxEmails.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new OutboxEmailDetail(
                x.Id, x.To, x.Subject, x.CreatedAt, x.Provider.ToString(), x.ProviderMessageId,
                x.DeliveryError, x.HtmlBody, x.TextBody))
            .SingleOrDefaultAsync(ct);
        return email is null ? NotFound() : Ok(email);
    }
}
