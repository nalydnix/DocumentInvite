using System.Security.Claims;
using DocumentInvite.Api.Common.Paging;
using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Auth;
using DocumentInvite.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DocumentInvite.Api.Features.Outbox;

public sealed record OutboxEmailSummary(
    Guid Id, string To, string Subject, DateTimeOffset CreatedAt, string Provider, string? DeliveryError, bool IsRead);

public sealed record OutboxEmailDetail(
    Guid Id, string To, string Subject, DateTimeOffset CreatedAt, string Provider, string? ProviderMessageId,
    string? DeliveryError, string HtmlBody, string TextBody, bool IsRead);

public sealed record UnreadCountResponse(int Count);

// The outbox holds live portal links, so only staff who can act for a party may read it.
[ApiController]
[Route("api/outbox")]
[Authorize(Policy = AuthPolicies.RequireStaff)]
public class OutboxController(AppDbContext db) : ControllerBase
{
    /// <summary>Emails, newest first, with the signed-in user's read state.</summary>
    [HttpGet]
    public async Task<PagedResponse<OutboxEmailSummary>> List([FromQuery] PageQuery query, CancellationToken ct)
    {
        var userId = CurrentUserId;
        return await db.OutboxEmails.AsNoTracking()
            .Select(x => new Row
            {
                Id = x.Id,
                To = x.To,
                Subject = x.Subject,
                CreatedAt = x.CreatedAt,
                Provider = x.Provider,
                DeliveryError = x.DeliveryError,
                IsRead = db.OutboxEmailReads.Any(r => r.OutboxEmailId == x.Id && r.UserId == userId),
            })
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .ToPagedResponseAsync(query,
                x => new OutboxEmailSummary(x.Id, x.To, x.Subject, x.CreatedAt, x.Provider.ToString(), x.DeliveryError, x.IsRead),
                ct);
    }

    // Only the list columns are read, so the (large) bodies never leave the database.
    private sealed class Row
    {
        public Guid Id { get; init; }
        public string To { get; init; } = string.Empty;
        public string Subject { get; init; } = string.Empty;
        public DateTimeOffset CreatedAt { get; init; }
        public EmailProvider Provider { get; init; }
        public string? DeliveryError { get; init; }
        public bool IsRead { get; init; }
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var userId = CurrentUserId;
        var email = await db.OutboxEmails.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new OutboxEmailDetail(
                x.Id, x.To, x.Subject, x.CreatedAt, x.Provider.ToString(), x.ProviderMessageId,
                x.DeliveryError, x.HtmlBody, x.TextBody,
                db.OutboxEmailReads.Any(r => r.OutboxEmailId == x.Id && r.UserId == userId)))
            .SingleOrDefaultAsync(ct);
        return email is null ? NotFound() : Ok(email);
    }

    /// <summary>How many emails the signed-in user has not read yet.</summary>
    [HttpGet("unread-count")]
    public async Task<UnreadCountResponse> UnreadCount(CancellationToken ct)
    {
        var userId = CurrentUserId;
        return new UnreadCountResponse(
            await db.OutboxEmails.CountAsync(x => !db.OutboxEmailReads.Any(r => r.OutboxEmailId == x.Id && r.UserId == userId), ct));
    }

    /// <summary>Marks the email read for the signed-in user. Repeating it changes nothing.</summary>
    [HttpPost("{id:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken ct)
    {
        if (!await db.OutboxEmails.AnyAsync(x => x.Id == id, ct)) return NotFound();
        var userId = CurrentUserId;
        if (!await db.OutboxEmailReads.AnyAsync(r => r.OutboxEmailId == id && r.UserId == userId, ct))
        {
            db.OutboxEmailReads.Add(new OutboxEmailRead { OutboxEmailId = id, UserId = userId, ReadAt = DateTimeOffset.UtcNow });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                // A concurrent request marked it read first; the end state is the same.
                if (!await db.OutboxEmailReads.AnyAsync(r => r.OutboxEmailId == id && r.UserId == userId, ct)) throw;
            }
        }
        return NoContent();
    }

    /// <summary>Marks the email unread for the signed-in user. Repeating it changes nothing.</summary>
    [HttpPost("{id:guid}/unread")]
    public async Task<IActionResult> MarkUnread(Guid id, CancellationToken ct)
    {
        if (!await db.OutboxEmails.AnyAsync(x => x.Id == id, ct)) return NotFound();
        var userId = CurrentUserId;
        await db.OutboxEmailReads.Where(r => r.OutboxEmailId == id && r.UserId == userId).ExecuteDeleteAsync(ct);
        return NoContent();
    }

    private Guid CurrentUserId =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;
}
