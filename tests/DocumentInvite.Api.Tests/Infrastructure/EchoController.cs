using FluentValidation;
using Microsoft.AspNetCore.Mvc;

namespace DocumentInvite.Api.Tests.Infrastructure;

public record EchoRequest(string? Email, int Quantity);

public class EchoRequestValidator : AbstractValidator<EchoRequest>
{
    public EchoRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Quantity).GreaterThan(0);
    }
}

/// <summary>Test-only endpoints used to exercise the platform pipeline.</summary>
[ApiController]
[Route("test")]
public class EchoController : ControllerBase
{
    [HttpPost("echo")]
    public IActionResult Echo(EchoRequest request) => Ok(request);

    [HttpGet("boom")]
    public IActionResult Boom() => throw new InvalidOperationException("secret internal failure");
}
