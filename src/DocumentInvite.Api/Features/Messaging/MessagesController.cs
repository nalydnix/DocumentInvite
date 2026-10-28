using DocumentInvite.Api.Common.Paging;
using DocumentInvite.Api.Features.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DocumentInvite.Api.Features.Messaging;

/// <summary>
/// The thread on an invite, for staff. Everyone who can read a loan can read the thread, but only
/// Admins and Loan officers can post, and only they see internal notes.
/// </summary>
[ApiController]
public class MessagesController(MessagingService service) : ControllerBase
{
    private bool CanSeeInternal => User.IsInRole(StaffRoles.Admin) || User.IsInRole(StaffRoles.LoanOfficer);

    [HttpGet("api/invites/{id:guid}/messages")]
    [Authorize(Policy = AuthPolicies.CanRead)]
    public async Task<IActionResult> List(Guid id, [FromQuery] PageQuery query, CancellationToken ct) =>
        ToActionResult(await service.ListForStaffAsync(id, CanSeeInternal, query, ct), created: false);

    [HttpPost("api/invites/{id:guid}/messages")]
    [Authorize(Policy = AuthPolicies.RequireStaff)]
    public async Task<IActionResult> Post(Guid id, PostMessageRequest request, CancellationToken ct) =>
        ToActionResult(await service.PostTeamMessageAsync(id, request.Body!, ct), created: true);

    [HttpPost("api/invites/{id:guid}/notes")]
    [Authorize(Policy = AuthPolicies.RequireStaff)]
    public async Task<IActionResult> PostNote(Guid id, PostMessageRequest request, CancellationToken ct) =>
        ToActionResult(await service.PostInternalNoteAsync(id, request.Body!, ct), created: true);

    private IActionResult ToActionResult<T>(MessageResult<T> result, bool created)
    {
        if (result.NotFound)
        {
            return NotFound();
        }

        if (result.Conflict is not null)
        {
            return Problem(detail: result.Conflict, statusCode: StatusCodes.Status409Conflict);
        }

        if (result.DeliveryFailed)
        {
            return Problem(
                detail: "The party could not be emailed, so the message was not sent. Please try again.",
                statusCode: StatusCodes.Status502BadGateway);
        }

        return created ? StatusCode(StatusCodes.Status201Created, result.Value) : Ok(result.Value);
    }
}

public static class MessagingServiceCollectionExtensions
{
    public static IServiceCollection AddMessaging(this IServiceCollection services) =>
        services.AddScoped<MessagingService>();
}
