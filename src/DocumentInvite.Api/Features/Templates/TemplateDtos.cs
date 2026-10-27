using DocumentInvite.Api.Common.Paging;
using DocumentInvite.Api.Domain;
using FluentValidation;

namespace DocumentInvite.Api.Features.Templates;

public sealed record TemplateSummaryResponse(
    Guid Id, string Name, string Description, bool IsSeeded, bool IsArchived, int ItemCount);

/// <summary>Query-string parameters for the template list.</summary>
public sealed record TemplateListQuery(
    bool IncludeArchived = false,
    int Page = PagingDefaults.DefaultPage,
    int PageSize = PagingDefaults.DefaultPageSize) : IPagedQuery;

public sealed class TemplateListQueryValidator : AbstractValidator<TemplateListQuery>
{
    public TemplateListQueryValidator() => Include(new PagedQueryValidator<TemplateListQuery>());
}

public sealed record TemplateItemResponse(
    Guid Id,
    string Name,
    ItemKind Kind,
    string Instructions,
    bool AllowMultiple,
    FieldType? FieldType,
    bool IsRequired,
    int SortOrder);

public sealed record TemplateResponse(
    Guid Id,
    string Name,
    string Description,
    bool IsSeeded,
    bool IsArchived,
    DateTimeOffset? ArchivedAt,
    IReadOnlyList<TemplateItemResponse> Items);

/// <summary>An item in a create or edit request. The order of the list is the order of the items.</summary>
/// <param name="Id">The id of an existing item to keep, or null for a new item.</param>
public sealed record TemplateItemRequest(
    Guid? Id,
    string? Name,
    ItemKind? Kind,
    string? Instructions,
    bool AllowMultiple,
    FieldType? FieldType,
    bool IsRequired);

public sealed record SaveTemplateRequest(
    string? Name, string? Description, IReadOnlyList<TemplateItemRequest>? Items);

public sealed class SaveTemplateRequestValidator : AbstractValidator<SaveTemplateRequest>
{
    public const int MaxItems = 100;

    public SaveTemplateRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(2000);
        RuleFor(x => x.Items).NotNull();
        RuleFor(x => x.Items!.Count).LessThanOrEqualTo(MaxItems)
            .When(x => x.Items is not null)
            .WithName("items").WithMessage($"A template can have at most {MaxItems} items.");
        RuleForEach(x => x.Items).SetValidator(new TemplateItemRequestValidator()!);
        RuleFor(x => x.Items)
            .Must(items => items!.Where(i => i.Id is not null).Select(i => i.Id).Distinct().Count()
                == items!.Count(i => i.Id is not null))
            .When(x => x.Items is not null)
            .WithMessage("An item id must not be repeated.");
    }
}

public sealed class TemplateItemRequestValidator : AbstractValidator<TemplateItemRequest>
{
    public TemplateItemRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Instructions).MaximumLength(2000);
        RuleFor(x => x.Kind).NotNull().IsInEnum();

        RuleFor(x => x.FieldType).NotNull().WithMessage("A form field requires a field type.").IsInEnum()
            .When(x => x.Kind == ItemKind.FormField);
        RuleFor(x => x.FieldType).Null()
            .When(x => x.Kind == ItemKind.FileUpload)
            .WithMessage("A file upload item cannot have a field type.");
        RuleFor(x => x.AllowMultiple).Equal(false)
            .When(x => x.Kind == ItemKind.FormField)
            .WithMessage("Only file upload items can allow multiple files.");
    }
}
