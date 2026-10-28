using DocumentInvite.Api.Common.Paging;
using DocumentInvite.Api.Features.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DocumentInvite.Api.Features.Loans;

[ApiController]
[Route("api/loans")]
public class LoansController(LoanService service) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = AuthPolicies.CanRead)]
    public async Task<PagedResponse<LoanSummaryResponse>> List([FromQuery] LoanListQuery query, CancellationToken ct) =>
        await service.ListAsync(query, ct);

    [HttpGet("{id:guid}")]
    [Authorize(Policy = AuthPolicies.CanRead)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => ToActionResult(await service.GetAsync(id, ct));

    [HttpPost]
    [Authorize(Policy = AuthPolicies.RequireStaff)]
    public async Task<IActionResult> Create(LoanRequest request, CancellationToken ct)
    {
        var result = await service.CreateAsync(request, ct);
        return result.Loan is { } loan ? CreatedAtAction(nameof(Get), new { id = loan.Id }, loan) : ToActionResult(result);
    }

    private IActionResult ToActionResult(LoanResult result)
    {
        if (result.NotFound)
        {
            return NotFound();
        }

        if (result.Conflict is not null)
        {
            return Problem(detail: result.Conflict, statusCode: StatusCodes.Status409Conflict);
        }

        return Ok(result.Loan);
    }
}

public static class LoansServiceCollectionExtensions
{
    public static IServiceCollection AddLoans(this IServiceCollection services) =>
        services.AddScoped<LoanService>();
}
