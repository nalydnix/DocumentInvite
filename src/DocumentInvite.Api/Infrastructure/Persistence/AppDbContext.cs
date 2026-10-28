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
        (StaffRoles.Auditor, new Guid("a1000000-0000-0000-0000-000000000004")),
    ];

    public DbSet<AppSettings> Settings => Set<AppSettings>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<Template> Templates => Set<Template>();
    public DbSet<TemplateItem> TemplateItems => Set<TemplateItem>();
    public DbSet<OutboxEmail> OutboxEmails => Set<OutboxEmail>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<Loan> Loans => Set<Loan>();
    public DbSet<Invite> Invites => Set<Invite>();
    public DbSet<RequestItem> RequestItems => Set<RequestItem>();
    public DbSet<JobRun> JobRuns => Set<JobRun>();
    public DbSet<StoredFile> StoredFiles => Set<StoredFile>();
    public DbSet<Message> Messages => Set<Message>();

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

        modelBuilder.Entity<Loan>(e =>
        {
            e.ToTable("Loans");
            e.HasKey(x => x.Id);
            e.Property(x => x.Reference).HasMaxLength(50).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.Property(x => x.LoanType).HasMaxLength(100).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasIndex(x => x.Reference).IsUnique();
            e.HasIndex(x => new { x.CreatedAt, x.Id });
            e.HasMany(x => x.Invites).WithOne().HasForeignKey(x => x.LoanId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Invite>(e =>
        {
            e.ToTable("Invites");
            e.HasKey(x => x.Id);
            e.Property(x => x.TokenHash).HasColumnType("binary(32)");
            e.Property(x => x.PasswordHash).HasMaxLength(500);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasIndex(x => x.TokenHash).IsUnique();
            // One invite per party per loan.
            e.HasIndex(x => new { x.LoanId, x.ContactId }).IsUnique();
            e.HasOne<Contact>().WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.InviteId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RequestItem>(e =>
        {
            e.ToTable("RequestItems");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Instructions).HasMaxLength(2000).IsRequired();
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.FieldType).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.RejectionReason).HasMaxLength(2000);
            e.Property(x => x.FieldValue).HasMaxLength(4000);
            e.Property(x => x.PartyNote).HasMaxLength(2000);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasIndex(x => new { x.InviteId, x.SortOrder });
        });

        modelBuilder.Entity<JobRun>(e =>
        {
            e.ToTable("JobRuns");
            e.HasKey(x => x.Id);
            e.Property(x => x.JobName).HasMaxLength(100).IsRequired();
            e.Property(x => x.WindowKey).HasMaxLength(100).IsRequired();
            e.Property(x => x.Result).HasMaxLength(2000).IsRequired();
            // One run per job and window: a second call for the same window cannot start another.
            e.HasIndex(x => new { x.JobName, x.WindowKey }).IsUnique();
        });

        modelBuilder.Entity<StoredFile>(e =>
        {
            e.ToTable("StoredFiles");
            e.HasKey(x => x.Id);
            e.Property(x => x.BlobName).HasMaxLength(200).IsRequired();
            e.Property(x => x.OriginalFileName).HasMaxLength(255).IsRequired();
            e.Property(x => x.StoredFileName).HasMaxLength(255);
            e.Property(x => x.ContentType).HasMaxLength(150).IsRequired();
            e.Property(x => x.Sha256).HasColumnType("binary(32)");
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => x.BlobName).IsUnique();
            // Two files of one item can never share a sequence number, even under concurrent completion.
            e.HasIndex(x => new { x.RequestItemId, x.Sequence }).IsUnique().HasFilter("[Sequence] IS NOT NULL");
            e.HasOne<RequestItem>().WithMany().HasForeignKey(x => x.RequestItemId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Message>(e =>
        {
            e.ToTable("Messages");
            e.HasKey(x => x.Id);
            e.Property(x => x.AuthorType).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Body).HasMaxLength(4000).IsRequired();
            e.HasIndex(x => new { x.InviteId, x.CreatedAt });
            e.HasOne<Invite>().WithMany().HasForeignKey(x => x.InviteId).OnDelete(DeleteBehavior.Cascade);
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

        modelBuilder.Entity<OutboxEmail>(e =>
        {
            e.ToTable("OutboxEmails");
            e.HasKey(x => x.Id);
            e.Property(x => x.To).HasMaxLength(320).IsRequired();
            e.Property(x => x.Subject).HasMaxLength(500).IsRequired();
            e.Property(x => x.HtmlBody).HasColumnType("nvarchar(max)");
            e.Property(x => x.TextBody).HasColumnType("nvarchar(max)");
            e.Property(x => x.Provider).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ProviderMessageId).HasMaxLength(200);
            e.Property(x => x.DeliveryError).HasMaxLength(500);
            e.HasIndex(x => new { x.CreatedAt, x.Id });
        });
    }
}
