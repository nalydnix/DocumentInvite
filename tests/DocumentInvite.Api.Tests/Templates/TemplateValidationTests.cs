using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Templates;
using FluentAssertions;
using FluentValidation.TestHelper;

namespace DocumentInvite.Api.Tests.Templates;

public class TemplateValidationTests
{
    private readonly SaveTemplateRequestValidator _validator = new();

    private static TemplateItemRequest Item(ItemKind? kind = ItemKind.FileUpload, FieldType? type = null, bool multiple = false, string? name = "Item") =>
        new(null, name, kind, null, multiple, type, true);

    private static SaveTemplateRequest Request(params TemplateItemRequest[] items) => new("Name", null, items);

    [Fact]
    public void A_valid_template_passes()
    {
        var result = _validator.TestValidate(Request(
            Item(), Item(multiple: true), Item(ItemKind.FormField, FieldType.Date)));

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void A_form_field_requires_a_field_type()
    {
        var result = _validator.TestValidate(Request(Item(ItemKind.FormField, null)));

        result.ShouldHaveValidationErrorFor("Items[0].FieldType").WithErrorMessage("A form field requires a field type.");
    }

    [Theory]
    [InlineData(FieldType.Text)]
    [InlineData(FieldType.Number)]
    [InlineData(FieldType.Date)]
    [InlineData(FieldType.YesNo)]
    public void A_form_field_accepts_every_field_type(FieldType type) =>
        _validator.TestValidate(Request(Item(ItemKind.FormField, type))).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void A_form_field_with_an_undefined_field_type_is_rejected() =>
        _validator.TestValidate(Request(Item(ItemKind.FormField, (FieldType)99)))
            .ShouldHaveValidationErrorFor("Items[0].FieldType");

    [Fact]
    public void A_file_upload_cannot_have_a_field_type() =>
        _validator.TestValidate(Request(Item(ItemKind.FileUpload, FieldType.Text)))
            .ShouldHaveValidationErrorFor("Items[0].FieldType");

    [Fact]
    public void Only_file_uploads_can_allow_multiple_files() =>
        _validator.TestValidate(Request(Item(ItemKind.FormField, FieldType.Text, multiple: true)))
            .ShouldHaveValidationErrorFor("Items[0].AllowMultiple");

    [Fact]
    public void An_item_needs_a_kind_and_a_name()
    {
        var result = _validator.TestValidate(Request(Item(kind: null, name: " ")));

        result.ShouldHaveValidationErrorFor("Items[0].Kind");
        result.ShouldHaveValidationErrorFor("Items[0].Name");
    }

    [Fact]
    public void The_template_needs_a_name_and_an_item_list()
    {
        var result = _validator.TestValidate(new SaveTemplateRequest(" ", null, null));

        result.ShouldHaveValidationErrorFor(x => x.Name);
        result.ShouldHaveValidationErrorFor(x => x.Items);
    }

    [Fact]
    public void Too_many_items_are_rejected()
    {
        var items = Enumerable.Range(0, SaveTemplateRequestValidator.MaxItems + 1).Select(_ => Item()).ToArray();

        _validator.TestValidate(Request(items)).Errors.Should().Contain(e => e.ErrorMessage.Contains("at most"));
    }

    [Fact]
    public void A_repeated_item_id_is_rejected()
    {
        var id = Guid.NewGuid();
        var items = new[] { Item() with { Id = id }, Item() with { Id = id } };

        _validator.TestValidate(Request(items)).ShouldHaveValidationErrorFor(x => x.Items);
    }
}
