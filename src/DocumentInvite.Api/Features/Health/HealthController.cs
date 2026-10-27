using Microsoft.AspNetCore.Mvc;

namespace DocumentInvite.Api.Features.Health;

[ApiController]
public class HealthController : ControllerBase
{
    [HttpGet("/health")]
    public IActionResult Get() => Ok(new { status = "Healthy" });
}
