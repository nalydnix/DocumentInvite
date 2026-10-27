using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocumentInvite.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuditAppendOnlyPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Members of audit_writer can only append to and read the audit trail. DENY overrides
            // any broader grant (such as db_datawriter), so the runtime application user must be
            // added to this role. Migrations run under a separate privileged identity.
            migrationBuilder.Sql(
                "IF DATABASE_PRINCIPAL_ID(N'audit_writer') IS NULL CREATE ROLE [audit_writer];");
            migrationBuilder.Sql("GRANT SELECT, INSERT ON OBJECT::[dbo].[AuditEvents] TO [audit_writer];");
            migrationBuilder.Sql("DENY UPDATE, DELETE ON OBJECT::[dbo].[AuditEvents] TO [audit_writer];");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("REVOKE SELECT, INSERT, UPDATE, DELETE ON OBJECT::[dbo].[AuditEvents] FROM [audit_writer];");
            migrationBuilder.Sql(
                "IF DATABASE_PRINCIPAL_ID(N'audit_writer') IS NOT NULL AND NOT EXISTS " +
                "(SELECT 1 FROM sys.database_role_members WHERE role_principal_id = DATABASE_PRINCIPAL_ID(N'audit_writer')) " +
                "DROP ROLE [audit_writer];");
        }
    }
}
