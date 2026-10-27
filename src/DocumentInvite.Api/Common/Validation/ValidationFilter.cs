using DocumentInvite.Api.Common.Errors;
using FluentValidation;
using Microsoft.AspNetCore.Mvc.Filters;

namespace DocumentInvite.Api.Common.Validation;

/// <summary>Runs the registered FluentValidation validator for every action argument.</summary>
public sealed class ValidationFilter : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        foreach (var argument in context.ActionArguments.Values)
        {
            if (argument is null) continue;

            var validatorType = typeof(IValidator<>).MakeGenericType(argument.GetType());
            if (context.HttpContext.RequestServices.GetService(validatorType) is not IValidator validator) continue;

            var result = await validator.ValidateAsync(
                new ValidationContext<object>(argument), context.HttpContext.RequestAborted);

            foreach (var failure in result.Errors)
            {
                context.ModelState.AddModelError(ToCamelCase(failure.PropertyName), failure.ErrorMessage);
            }
        }

        if (!context.ModelState.IsValid)
        {
            context.Result = ValidationProblems.Create(context.HttpContext, context.ModelState);
            return;
        }

        await next();
    }

    private static string ToCamelCase(string path) =>
        string.Join('.', path.Split('.').Select(s => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..]));
}
