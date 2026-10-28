using DocumentInvite.Api.Features.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DocumentInvite.Api.Features.Review;

/// <summary>Approving and rejecting items is for staff only.</summary>
[ApiController]
[Authorize(Policy = AuthPolicies.RequireStaff)]
public class ReviewController(ReviewService service) : ControllerBase
{
    [HttpPost("api/items/{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id, CancellationToken ct) => ToActionResult(await service.ApproveAsync(id, ct));

    [HttpPost("api/items/{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id, RejectItemRequest request, CancellationToken ct) =>
        ToActionResult(await service.RejectAsync(id, request.Reason!, ct));

    private IActionResult ToActionResult(ReviewResult result)
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
                detail: "The party could not be emailed, so the item was not rejected. Please try again.",
                statusCode: StatusCodes.Status502BadGateway);
        }

        return Ok(result.Value);
    }
}

public static class ReviewServiceCollectionExtensions
{
    public static IServiceCollection AddReview(this IServiceCollection services) =>
        services.AddScoped<ReviewService>();
}
