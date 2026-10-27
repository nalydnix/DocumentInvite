using DocumentInvite.Api.Common.Paging;
using DocumentInvite.Api.Domain;
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

// The outbox holds live portal links, so only staff who can act for a party may read it.
[ApiController]
[Route("api/outbox")]
[Authorize(Policy = AuthPolicies.RequireStaff)]
public class OutboxController(AppDbContext db) : ControllerBase
{
    /// <summary>Emails, newest first.</summary>
    [HttpGet]
    public async Task<PagedResponse<OutboxEmailSummary>> List([FromQuery] PageQuery query, CancellationToken ct) =>
        await db.OutboxEmails.AsNoTracking()
            .Select(x => new Row { Id = x.Id, To = x.To, Subject = x.Subject, CreatedAt = x.CreatedAt, Provider = x.Provider, DeliveryError = x.DeliveryError })
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .ToPagedResponseAsync(query,
                x => new OutboxEmailSummary(x.Id, x.To, x.Subject, x.CreatedAt, x.Provider.ToString(), x.DeliveryError),
                ct);

    // Only the list columns are read, so the (large) bodies never leave the database.
    private sealed class Row
    {
        public Guid Id { get; init; }
        public string To { get; init; } = string.Empty;
        public string Subject { get; init; } = string.Empty;
        public DateTimeOffset CreatedAt { get; init; }
        public EmailProvider Provider { get; init; }
        public string? DeliveryError { get; init; }
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
