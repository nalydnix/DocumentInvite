using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace DocumentInvite.Api.Common.Errors;

public static class ValidationProblems
{
    public static ObjectResult Create(HttpContext http, ModelStateDictionary modelState)
    {
        var factory = http.RequestServices.GetRequiredService<ProblemDetailsFactory>();
        var details = factory.CreateValidationProblemDetails(http, modelState, statusCode: StatusCodes.Status400BadRequest);
        details.Extensions["correlationId"] = http.Items[CorrelationIdMiddleware.ItemKey];
        return new ObjectResult(details)
        {
            StatusCode = StatusCodes.Status400BadRequest,
            ContentTypes = { "application/problem+json" },
        };
    }
}
