using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocumentInvite.Api.Features.Templates;

/// <summary>
/// Creates the two lending templates when they are missing. A template that already exists is
/// left exactly as it is, so staff edits (or archiving) survive a restart. Seeding is reference
/// data setup rather than a staff action, so it is not written to the audit trail.
/// </summary>
public sealed class TemplateSeeder(AppDbContext db)
{
    public static readonly Guid SbaTemplateId = new("b2000000-0000-0000-0000-000000000001");
    public static readonly Guid CommercialRealEstateTemplateId = new("b2000000-0000-0000-0000-000000000002");

    public async Task SeedAsync(CancellationToken ct = default)
    {
        var existing = await db.Templates.Select(x => x.Id).ToListAsync(ct);
        var missing = Definitions().Where(t => !existing.Contains(t.Id)).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        db.Templates.AddRange(missing);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another instance may have seeded the same templates at the same moment.
            db.ChangeTracker.Clear();
            var ids = missing.Select(m => m.Id).ToList();
            if (await db.Templates.CountAsync(x => ids.Contains(x.Id), ct) != ids.Count)
            {
                throw;
            }
        }
    }

    private static IEnumerable<Template> Definitions()
    {
        yield return Build(
            SbaTemplateId,
            "SBA 7(a) business acquisition",
            "Documents and data to request from the buyer and the seller for an SBA 7(a) loan that finances the purchase of an existing business.",
            [
                File("Business tax returns (last 3 years)", "Complete federal returns for the business with all schedules, for the last three years.", true, true),
                File("Personal tax returns (last 3 years)", "Complete federal returns for each owner of 20 percent or more, for the last three years.", true, true),
                File("Year-to-date profit and loss statement", "Prepared within the last 90 days.", true, false),
                File("Year-to-date balance sheet", "Prepared within the last 90 days.", true, false),
                File("Personal financial statement (SBA Form 413)", "Signed and dated by each owner of 20 percent or more.", true, false),
                File("Executed purchase agreement or letter of intent", "The full signed agreement, including all schedules and exhibits.", true, false),
                File("Business valuation or appraisal", "Independent valuation report, if one has been completed.", false, false),
                File("Entity documents", "Articles of organization or incorporation, operating agreement or bylaws, and any amendments.", true, true),
                File("Business debt schedule", "A list of every business debt with lender, balance, payment and maturity date.", true, false),
                File("Government-issued photo ID", "A clear copy of the front of each owner's ID.", true, true),
                Form("Years in business", FieldType.Number, "How many years the business has operated.", true),
                Form("Planned closing date", FieldType.Date, "The date the buyer expects to close the acquisition.", true),
                Form("Is the seller staying on as a consultant?", FieldType.YesNo, "Answer yes if the seller will remain involved after closing.", false),
                Form("Primary contact phone number", FieldType.Text, "The best number to reach the borrower during underwriting.", false),
            ]);

        yield return Build(
            CommercialRealEstateTemplateId,
            "Commercial real estate purchase",
            "Documents and data to request for a loan that finances the purchase of an income-producing commercial property.",
            [
                File("Executed purchase and sale agreement", "The full signed agreement, including all addenda.", true, false),
                File("Current rent roll", "Dated within the last 30 days, showing tenant, unit, rent, deposit and lease dates.", true, false),
                File("Leases for major tenants", "Signed leases and amendments for every tenant above 10 percent of the rentable area.", true, true),
                File("Trailing 12-month operating statement", "Income and expenses by month for the last 12 months.", true, false),
                File("Operating statements for the prior 2 years", "Annual income and expense statements for the two years before the trailing 12 months.", true, true),
                File("Appraisal", "Ordered through the lender; upload the report here once received.", true, false),
                File("Phase I environmental site assessment", "Report dated within 180 days of closing.", true, false),
                File("Property survey and title commitment", "ALTA survey and the preliminary title commitment.", true, true),
                File("Property insurance quote", "Quote or binder showing the required coverages.", false, false),
                File("Entity documents for the borrowing entity", "Formation documents, operating agreement and a certificate of good standing.", true, true),
                File("Personal financial statement and tax returns for each guarantor", "Signed financial statement and the last two years of personal tax returns.", true, true),
                Form("Purchase price", FieldType.Number, "The contract purchase price in US dollars.", true),
                Form("Expected closing date", FieldType.Date, "The date set for closing in the purchase agreement.", true),
                Form("Will the borrower occupy part of the property?", FieldType.YesNo, "Answer yes if the borrower will use part of the property for its own business.", true),
                Form("Property manager name", FieldType.Text, "The company that will manage the property, if any.", false),
            ]);
    }

    private static Template Build(Guid id, string name, string description, IEnumerable<TemplateItem> items)
    {
        var template = new Template { Id = id, Name = name, Description = description, IsSeeded = true };
        var order = 0;
        foreach (var item in items)
        {
            item.TemplateId = id;
            item.SortOrder = order++;
            template.Items.Add(item);
        }

        return template;
    }

    private static TemplateItem File(string name, string instructions, bool required, bool allowMultiple) =>
        new() { Name = name, Kind = ItemKind.FileUpload, Instructions = instructions, IsRequired = required, AllowMultiple = allowMultiple };

    private static TemplateItem Form(string name, FieldType type, string instructions, bool required) =>
        new() { Name = name, Kind = ItemKind.FormField, FieldType = type, Instructions = instructions, IsRequired = required };
}

/// <summary>Seeds the lending templates on startup. A failure (for example an unmigrated database) is logged, not fatal.</summary>
public sealed class TemplateSeedingService(IServiceProvider services, ILogger<TemplateSeedingService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<TemplateSeeder>().SeedAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Seeding the checklist templates failed");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class TemplatesServiceCollectionExtensions
{
    public static IServiceCollection AddTemplates(this IServiceCollection services)
    {
        services.AddScoped<TemplateService>();
        services.AddScoped<TemplateSeeder>();
        services.AddHostedService<TemplateSeedingService>();
        return services;
    }
}
