using DocumentInvite.Api.Common.Paging;
using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Audit;
using DocumentInvite.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocumentInvite.Api.Features.Templates;

/// <summary>The outcome of a template operation: a value, "not found", or a conflict message.</summary>
public sealed record TemplateResult(TemplateResponse? Template, bool NotFound, string? Conflict)
{
    public static TemplateResult Ok(TemplateResponse template) => new(template, false, null);
    public static TemplateResult Missing() => new(null, true, null);
    public static TemplateResult Conflicting(string message) => new(null, false, message);
}

public sealed class TemplateService(AppDbContext db, IAuditLog audit, TimeProvider clock)
{
    public async Task<PagedResponse<TemplateSummaryResponse>> ListAsync(TemplateListQuery query, CancellationToken ct)
    {
        var templates = db.Templates.AsNoTracking();
        if (!query.IncludeArchived)
        {
            templates = templates.Where(x => x.ArchivedAt == null);
        }

        return await templates
            .Select(x => new Row
            {
                Id = x.Id,
                Name = x.Name,
                Description = x.Description,
                IsSeeded = x.IsSeeded,
                IsArchived = x.ArchivedAt != null,
                ItemCount = x.Items.Count,
            })
            .OrderBy(x => x.Name).ThenBy(x => x.Id)
            .ToPagedResponseAsync(
                query, x => new TemplateSummaryResponse(x.Id, x.Name, x.Description, x.IsSeeded, x.IsArchived, x.ItemCount), ct);
    }

    // A projection EF can order by (a constructor call cannot be translated inside OrderBy).
    private sealed class Row
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public bool IsSeeded { get; init; }
        public bool IsArchived { get; init; }
        public int ItemCount { get; init; }
    }

    public async Task<TemplateResult> GetAsync(Guid id, CancellationToken ct)
    {
        var template = await db.Templates.AsNoTracking().Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == id, ct);
        return template is null ? TemplateResult.Missing() : TemplateResult.Ok(ToResponse(template));
    }

    public async Task<TemplateResult> CreateAsync(SaveTemplateRequest request, CancellationToken ct)
    {
        var template = new Template { Name = request.Name!.Trim(), Description = request.Description?.Trim() ?? string.Empty };
        template.Items.AddRange(request.Items!.Select((item, index) => NewItem(template.Id, item, index)));

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.Templates.Add(template);
        await audit.RecordAsync(
            "template.created", new AuditTarget("template", template.Id),
            AuditDetails.Empty.WithCount("itemCount", template.Items.Count), ct);
        await transaction.CommitAsync(ct);

        return TemplateResult.Ok(ToResponse(template));
    }

    public async Task<TemplateResult> UpdateAsync(Guid id, SaveTemplateRequest request, CancellationToken ct)
    {
        var template = await db.Templates.Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (template is null)
        {
            return TemplateResult.Missing();
        }

        if (template.ArchivedAt is not null)
        {
            return TemplateResult.Conflicting("An archived template cannot be edited. Restore it first.");
        }

        var requested = request.Items!;
        if (requested.Any(i => i.Id is not null && template.Items.All(e => e.Id != i.Id)))
        {
            return TemplateResult.Conflicting("An item in the request does not belong to this template.");
        }

        var keptIds = requested.Where(i => i.Id is not null).Select(i => i.Id!.Value).ToHashSet();
        var removed = template.Items.Where(e => !keptIds.Contains(e.Id)).ToList();
        var added = 0;

        template.Name = request.Name!.Trim();
        template.Description = request.Description?.Trim() ?? string.Empty;
        db.TemplateItems.RemoveRange(removed);
        foreach (var item in removed)
        {
            template.Items.Remove(item);
        }

        for (var index = 0; index < requested.Count; index++)
        {
            var item = requested[index];
            if (item.Id is { } existingId)
            {
                Apply(template.Items.Single(e => e.Id == existingId), item, index);
            }
            else
            {
                var created = NewItem(template.Id, item, index);
                template.Items.Add(created);
                db.TemplateItems.Add(created);
                added++;
            }
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await audit.RecordAsync(
            "template.updated", new AuditTarget("template", template.Id),
            AuditDetails.Empty
                .WithCount("itemCount", template.Items.Count)
                .WithCount("itemsAdded", added)
                .WithCount("itemsRemoved", removed.Count), ct);
        await transaction.CommitAsync(ct);

        return TemplateResult.Ok(ToResponse(template));
    }

    public Task<TemplateResult> ArchiveAsync(Guid id, CancellationToken ct) => SetArchivedAsync(id, true, ct);

    public Task<TemplateResult> RestoreAsync(Guid id, CancellationToken ct) => SetArchivedAsync(id, false, ct);

    private async Task<TemplateResult> SetArchivedAsync(Guid id, bool archive, CancellationToken ct)
    {
        var template = await db.Templates.Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (template is null)
        {
            return TemplateResult.Missing();
        }

        if ((template.ArchivedAt is not null) == archive)
        {
            return TemplateResult.Ok(ToResponse(template));
        }

        template.ArchivedAt = archive ? clock.GetUtcNow() : null;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await audit.RecordAsync(
            archive ? "template.archived" : "template.restored", new AuditTarget("template", template.Id), null, ct);
        await transaction.CommitAsync(ct);

        return TemplateResult.Ok(ToResponse(template));
    }

    private static TemplateItem NewItem(Guid templateId, TemplateItemRequest request, int index)
    {
        var item = new TemplateItem { TemplateId = templateId };
        Apply(item, request, index);
        return item;
    }

    private static void Apply(TemplateItem item, TemplateItemRequest request, int index)
    {
        item.Name = request.Name!.Trim();
        item.Kind = request.Kind!.Value;
        item.Instructions = request.Instructions?.Trim() ?? string.Empty;
        item.AllowMultiple = request.Kind == ItemKind.FileUpload && request.AllowMultiple;
        item.FieldType = request.Kind == ItemKind.FormField ? request.FieldType : null;
        item.IsRequired = request.IsRequired;
        item.SortOrder = index;
    }

    private static TemplateResponse ToResponse(Template template) =>
        new(template.Id, template.Name, template.Description, template.IsSeeded, template.ArchivedAt is not null,
            template.ArchivedAt,
            template.Items.OrderBy(i => i.SortOrder)
                .Select(i => new TemplateItemResponse(
                    i.Id, i.Name, i.Kind, i.Instructions, i.AllowMultiple, i.FieldType, i.IsRequired, i.SortOrder))
                .ToList());
}
