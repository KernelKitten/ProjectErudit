using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erudit.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PersonPasswords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "person_passwords",
                columns: table => new
                {
                    person_id = table.Column<int>(type: "integer", nullable: false),
                    hash = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    must_change = table.Column<bool>(type: "boolean", nullable: false),
                    changed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    failed_count = table.Column<int>(type: "integer", nullable: false),
                    locked_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_person_passwords", x => x.person_id);
                    table.ForeignKey(
                        name: "fk_person_passwords_people_person_id",
                        column: x => x.person_id,
                        principalTable: "people",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "person_passwords");
        }
    }
}
