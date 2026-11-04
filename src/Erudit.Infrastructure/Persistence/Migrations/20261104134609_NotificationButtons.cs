using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erudit.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NotificationButtons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "buttons",
                table: "notifications",
                type: "jsonb",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "buttons",
                table: "notifications");
        }
    }
}
