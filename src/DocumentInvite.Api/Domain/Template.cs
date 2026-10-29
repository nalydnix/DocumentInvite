using System.Text.Json.Serialization;

namespace DocumentInvite.Api.Domain;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ItemKind
{
    FileUpload,
    FormField,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FieldType
{
    Text,
    Number,
    Date,
    YesNo,
}

/// <summary>A reusable checklist of documents and data to request for a type of request.</summary>
public class Template
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsSeeded { get; set; }

    /// <summary>Set when the template is archived. Archived templates cannot be edited or newly applied.</summary>
    public DateTimeOffset? ArchivedAt { get; set; }

    public byte[] RowVersion { get; set; } = [];
    public List<TemplateItem> Items { get; set; } = [];
}

public class TemplateItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TemplateId { get; set; }
    public string Name { get; set; } = string.Empty;
    public ItemKind Kind { get; set; }
    public string Instructions { get; set; } = string.Empty;

    /// <summary>FileUpload only: whether the party may upload more than one file.</summary>
    public bool AllowMultiple { get; set; }

    /// <summary>FormField only: the type of answer expected.</summary>
    public FieldType? FieldType { get; set; }

    public bool IsRequired { get; set; }
    public int SortOrder { get; set; }
}
