using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erudit.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AllowPreparatoryGrade : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_school_classes_grade",
                table: "school_classes");

            migrationBuilder.AddCheckConstraint(
                name: "ck_school_classes_grade",
                table: "school_classes",
                sql: "grade BETWEEN 0 AND 11");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_school_classes_grade",
                table: "school_classes");

            migrationBuilder.AddCheckConstraint(
                name: "ck_school_classes_grade",
                table: "school_classes",
                sql: "grade BETWEEN 1 AND 11");
        }
    }
}
