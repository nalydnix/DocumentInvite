using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocumentInvite.Api.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Renames the Loans table, its columns, keys and indexes to Requests with rename operations, so existing
    /// data is kept. The staff role LoanOfficer becomes Requester. The audit trail is cleared (owner decision):
    /// event actions and target types changed names, so the chain restarts from the genesis hash under the new
    /// names. Clearing needs DELETE on dbo.AuditEvents, which audit_writer (di_app) is denied by design, so this
    /// migration must run under the admin connection (ConnectionStrings__Migrations), as the deploy workflow does.
    /// </summary>
    public partial class RenameLoansToRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(name: "FK_Invites_Loans_LoanId", table: "Invites");

            migrationBuilder.RenameTable(name: "Loans", newName: "Requests");
            migrationBuilder.Sql("EXEC sp_rename N'[dbo].[PK_Loans]', N'PK_Requests', N'OBJECT';");
            migrationBuilder.RenameColumn(name: "LoanType", table: "Requests", newName: "RequestType");
            migrationBuilder.RenameIndex(name: "IX_Loans_CreatedAt_Id", table: "Requests", newName: "IX_Requests_CreatedAt_Id");
            migrationBuilder.RenameIndex(name: "IX_Loans_Reference", table: "Requests", newName: "IX_Requests_Reference");

            migrationBuilder.RenameColumn(name: "LoanId", table: "Invites", newName: "RequestId");
            migrationBuilder.RenameIndex(name: "IX_Invites_LoanId_ContactId", table: "Invites", newName: "IX_Invites_RequestId_ContactId");

            migrationBuilder.AddForeignKey(
                name: "FK_Invites_Requests_RequestId",
                table: "Invites",
                column: "RequestId",
                principalTable: "Requests",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.UpdateData(
                table: "AspNetRoles",
                keyColumn: "Id",
                keyValue: new Guid("a1000000-0000-0000-0000-000000000002"),
                columns: new[] { "Name", "NormalizedName" },
                values: new object[] { "Requester", "REQUESTER" });

            // Reset the audit trail. The first new event chains from the genesis hash.
            migrationBuilder.Sql("DELETE FROM [dbo].[AuditEvents];");
            migrationBuilder.Sql("DBCC CHECKIDENT (N'[dbo].[AuditEvents]', RESEED, 0);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The audit events deleted by Up cannot be restored.
            migrationBuilder.DropForeignKey(name: "FK_Invites_Requests_RequestId", table: "Invites");

            migrationBuilder.RenameIndex(name: "IX_Invites_RequestId_ContactId", table: "Invites", newName: "IX_Invites_LoanId_ContactId");
            migrationBuilder.RenameColumn(name: "RequestId", table: "Invites", newName: "LoanId");

            migrationBuilder.RenameIndex(name: "IX_Requests_Reference", table: "Requests", newName: "IX_Loans_Reference");
            migrationBuilder.RenameIndex(name: "IX_Requests_CreatedAt_Id", table: "Requests", newName: "IX_Loans_CreatedAt_Id");
            migrationBuilder.RenameColumn(name: "RequestType", table: "Requests", newName: "LoanType");
            migrationBuilder.Sql("EXEC sp_rename N'[dbo].[PK_Requests]', N'PK_Loans', N'OBJECT';");
            migrationBuilder.RenameTable(name: "Requests", newName: "Loans");

            migrationBuilder.AddForeignKey(
                name: "FK_Invites_Loans_LoanId",
                table: "Invites",
                column: "LoanId",
                principalTable: "Loans",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.UpdateData(
                table: "AspNetRoles",
                keyColumn: "Id",
                keyValue: new Guid("a1000000-0000-0000-0000-000000000002"),
                columns: new[] { "Name", "NormalizedName" },
                values: new object[] { "LoanOfficer", "LOANOFFICER" });
        }
    }
}
