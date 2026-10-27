using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Auth;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DocumentInvite.Api.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, AppRole, Guid>(options)
{
    // Fixed ids and stamps keep the seeded roles identical in every database and migration.
    private static readonly (string Name, Guid Id)[] SeededRoles =
    [
        (StaffRoles.Admin, new Guid("a1000000-0000-0000-0000-000000000001")),
        (StaffRoles.LoanOfficer, new Guid("a1000000-0000-0000-0000-000000000002")),
        (StaffRoles.Viewer, new Guid("a1000000-0000-0000-0000-000000000003")),
        (StaffRoles.Auditor, new Guid("a1000000-0000-0000-0000-000000000004")),
    ];

    public DbSet<AppSettings> Settings => Set<AppSettings>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<Template> Templates => Set<Template>();
    public DbSet<TemplateItem> TemplateItems => Set<TemplateItem>();
    public DbSet<Contact> Contacts => Set<Contact>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<AppUser>(e => e.Property(x => x.DisplayName).HasMaxLength(200).IsRequired());

        modelBuilder.Entity<AppRole>().HasData(SeededRoles.Select(r => new AppRole
        {
            Id = r.Id,
            Name = r.Name,
            NormalizedName = r.Name.ToUpperInvariant(),
            ConcurrencyStamp = r.Id.ToString("D"),
        }));

        modelBuilder.Entity<AppSettings>(e =>
        {
            e.ToTable("AppSettings");
            e.HasKey(x => x.Id);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasData(new AppSettings());
        });

        modelBuilder.Entity<Template>(e =>
        {
            e.ToTable("Templates");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasMaxLength(2000).IsRequired();
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TemplateItem>(e =>
        {
            e.ToTable("TemplateItems");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Instructions).HasMaxLength(2000).IsRequired();
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.FieldType).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.TemplateId, x.SortOrder });
        });

        modelBuilder.Entity<Contact>(e =>
        {
            e.ToTable("Contacts");
            e.HasKey(x => x.Id);
            e.Property(x => x.FirstName).HasMaxLength(100).IsRequired();
            e.Property(x => x.LastName).HasMaxLength(100).IsRequired();
            e.Property(x => x.Email).HasMaxLength(256).IsRequired();
            e.Property(x => x.Phone).HasMaxLength(30);
            e.Property(x => x.PartyType).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasIndex(x => new { x.LastName, x.FirstName });
            e.HasIndex(x => x.Email);
        });

        modelBuilder.Entity<AuditEvent>(e =>
        {
            e.ToTable("AuditEvents");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.ActorType).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Action).HasMaxLength(100);
            e.Property(x => x.TargetType).HasMaxLength(100);
            e.Property(x => x.IpAddress).HasMaxLength(45);
            e.Property(x => x.UserAgent).HasMaxLength(512);
            e.Property(x => x.CorrelationId).HasMaxLength(100);
            e.Property(x => x.DetailsJson).HasColumnType("nvarchar(max)");
            e.Property(x => x.PrevHash).HasColumnType("binary(32)");
            e.Property(x => x.Hash).HasColumnType("binary(32)");
            // A second event can never claim the same predecessor, so the chain cannot fork.
            e.HasIndex(x => x.PrevHash).IsUnique();
        });
    }
}
