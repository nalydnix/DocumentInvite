using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocumentInvite.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SingleUserRole : Migration
    {
        /// <summary>Removes every role from a user except their highest one.</summary>
        public const string KeepHighestRoleSql = @"
WITH ranked AS (
    SELECT ur.UserId, ur.RoleId,
           ROW_NUMBER() OVER (
               PARTITION BY ur.UserId
               ORDER BY CASE r.Name WHEN 'Admin' THEN 1 WHEN 'LoanOfficer' THEN 2 WHEN 'Viewer' THEN 3 WHEN 'Auditor' THEN 4 ELSE 5 END,
                        r.Name) AS Position
    FROM AspNetUserRoles ur
    JOIN AspNetRoles r ON r.Id = ur.RoleId)
DELETE ur
FROM AspNetUserRoles ur
JOIN ranked ON ranked.UserId = ur.UserId AND ranked.RoleId = ur.RoleId
WHERE ranked.Position > 1;";

        /// <summary>Clears the access expiry of every user who is not an Auditor.</summary>
        public const string ClearNonAuditorExpirySql = @"
UPDATE u SET AccessExpiresAt = NULL
FROM AspNetUsers u
WHERE u.AccessExpiresAt IS NOT NULL
  AND NOT EXISTS (
      SELECT 1 FROM AspNetUserRoles ur JOIN AspNetRoles r ON r.Id = ur.RoleId
      WHERE ur.UserId = u.Id AND r.Name = 'Auditor');";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Each user keeps only their highest role: Admin, then LoanOfficer, then Viewer, then Auditor.
            migrationBuilder.Sql(KeepHighestRoleSql);

            // An access expiry only applies to an Auditor.
            migrationBuilder.Sql(ClearNonAuditorExpirySql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
