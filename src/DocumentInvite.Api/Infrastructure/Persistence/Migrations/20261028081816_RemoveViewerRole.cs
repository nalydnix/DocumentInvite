using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocumentInvite.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveViewerRole : Migration
    {
        private const string ViewerRoleId = "A1000000-0000-0000-0000-000000000003";
        private const string LoanOfficerRoleId = "A1000000-0000-0000-0000-000000000002";

        /// <summary>Moves users whose only role is Viewer to LoanOfficer, then drops any leftover Viewer assignments.</summary>
        public const string ReassignViewersSql = @"
UPDATE ur SET RoleId = '" + LoanOfficerRoleId + @"'
FROM AspNetUserRoles ur
WHERE ur.RoleId = '" + ViewerRoleId + @"'
  AND NOT EXISTS (SELECT 1 FROM AspNetUserRoles o WHERE o.UserId = ur.UserId AND o.RoleId <> ur.RoleId);

DELETE FROM AspNetUserRoles WHERE RoleId = '" + ViewerRoleId + "';";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Viewer users become LoanOfficers before the role row is deleted.
            migrationBuilder.Sql(ReassignViewersSql);

            migrationBuilder.DeleteData(
                table: "AspNetRoles",
                keyColumn: "Id",
                keyValue: new Guid("a1000000-0000-0000-0000-000000000003"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "AspNetRoles",
                columns: new[] { "Id", "ConcurrencyStamp", "Name", "NormalizedName" },
                values: new object[] { new Guid("a1000000-0000-0000-0000-000000000003"), "a1000000-0000-0000-0000-000000000003", "Viewer", "VIEWER" });
        }
    }
}
