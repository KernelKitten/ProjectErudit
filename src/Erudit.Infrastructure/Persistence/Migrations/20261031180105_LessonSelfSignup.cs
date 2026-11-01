using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erudit.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LessonSelfSignup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "self_signup",
                table: "series",
                type: "boolean",
                nullable: false,
                defaultValue: true); // вручную: уже созданные занятия остаются открытыми для всех

            migrationBuilder.AddColumn<int>(
                name: "signup_grade_from",
                table: "series",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "signup_grade_to",
                table: "series",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "self_signup",
                table: "lessons",
                type: "boolean",
                nullable: false,
                defaultValue: true); // вручную: уже созданные занятия остаются открытыми для всех

            migrationBuilder.AddColumn<int>(
                name: "signup_grade_from",
                table: "lessons",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "signup_grade_to",
                table: "lessons",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "self_signup",
                table: "series");

            migrationBuilder.DropColumn(
                name: "signup_grade_from",
                table: "series");

            migrationBuilder.DropColumn(
                name: "signup_grade_to",
                table: "series");

            migrationBuilder.DropColumn(
                name: "self_signup",
                table: "lessons");

            migrationBuilder.DropColumn(
                name: "signup_grade_from",
                table: "lessons");

            migrationBuilder.DropColumn(
                name: "signup_grade_to",
                table: "lessons");
        }
    }
}
