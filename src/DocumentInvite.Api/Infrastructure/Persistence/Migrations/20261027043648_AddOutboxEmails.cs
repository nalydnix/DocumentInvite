using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocumentInvite.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboxEmails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OutboxEmails",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    To = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    HtmlBody = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TextBody = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ProviderMessageId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DeliveryError = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxEmails", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxEmails_CreatedAt_Id",
                table: "OutboxEmails",
                columns: new[] { "CreatedAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OutboxEmails");
        }
    }
}
