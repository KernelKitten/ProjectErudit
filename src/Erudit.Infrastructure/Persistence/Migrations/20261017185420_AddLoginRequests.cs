using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Erudit.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLoginRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "login_requests",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    token = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    secret_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    device_info = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    max_user_id = table.Column<long>(type: "bigint", nullable: true),
                    person_id = table.Column<int>(type: "integer", nullable: true),
                    confirmed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    used_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_login_requests", x => x.id);
                    table.ForeignKey(
                        name: "fk_login_requests_people_person_id",
                        column: x => x.person_id,
                        principalTable: "people",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_login_requests_created_at",
                table: "login_requests",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_login_requests_person_id",
                table: "login_requests",
                column: "person_id");

            migrationBuilder.CreateIndex(
                name: "ix_login_requests_token",
                table: "login_requests",
                column: "token",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "login_requests");
        }
    }
}
