#nullable disable
namespace CmsEvents.Infrastructure.Migrations;

using System;
using Microsoft.EntityFrameworkCore.Migrations;

/// <inheritdoc />
public partial class InitialSchema : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Entities",
            columns: table => new
            {
                Id = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                IsDisabled = table.Column<bool>(type: "bit", nullable: false),
                LastProcessedVersion = table.Column<int>(type: "int", nullable: false),
                LastProcessedTimestamp = table.Column<DateTime>(type: "datetime2", nullable: false),
                Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Entities", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Users",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Username = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                PasswordHash = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                Role = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Users", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Entities_Status_IsDisabled",
            table: "Entities",
            columns: ["Status", "IsDisabled"]);

        migrationBuilder.CreateIndex(
            name: "IX_Users_Username",
            table: "Users",
            column: "Username",
            unique: true);

        // Defense-in-depth per ADR-007 § Persistence: reject non-JSON values at the DB level.
        // The API validates payloads before storage (per ADR-008) — this constraint is a safety net.
        migrationBuilder.Sql(
            "ALTER TABLE [Entities] ADD CONSTRAINT [CK_Entities_Payload_IsJson] CHECK (ISJSON([Payload]) = 1);");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            "ALTER TABLE [Entities] DROP CONSTRAINT [CK_Entities_Payload_IsJson];");

        migrationBuilder.DropTable(
            name: "Entities");

        migrationBuilder.DropTable(
            name: "Users");
    }
}
