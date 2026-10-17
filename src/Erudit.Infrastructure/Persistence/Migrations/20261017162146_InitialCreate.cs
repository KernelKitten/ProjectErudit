using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Erudit.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "academic_years",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_academic_years", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "audit_log",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    person_id = table.Column<int>(type: "integer", nullable: true),
                    entity_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    entity_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    changes = table.Column<string>(type: "jsonb", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "calendar_days",
                columns: table => new
                {
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    is_working_day = table.Column<bool>(type: "boolean", nullable: false),
                    is_manual = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_calendar_days", x => x.date);
                });

            migrationBuilder.CreateTable(
                name: "lesson_periods",
                columns: table => new
                {
                    number = table.Column<int>(type: "integer", nullable: false),
                    start = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    end = table.Column<TimeOnly>(type: "time without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lesson_periods", x => x.number);
                    table.CheckConstraint("ck_lesson_periods_number", "number BETWEEN 0 AND 9");
                    table.CheckConstraint("ck_lesson_periods_time", "\"end\" > start");
                });

            migrationBuilder.CreateTable(
                name: "people",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    last_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    first_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    middle_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    max_user_id = table.Column<long>(type: "bigint", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_people", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "rooms",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    floor = table.Column<int>(type: "integer", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rooms", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "subjects",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    short_name = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subjects", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "vacations",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: false),
                    grade_from = table.Column<int>(type: "integer", nullable: true),
                    grade_to = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vacations", x => x.id);
                    table.CheckConstraint("ck_vacations_dates", "end_date >= start_date");
                });

            migrationBuilder.CreateTable(
                name: "closed_periods",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    month = table.Column<DateOnly>(type: "date", nullable: false),
                    closed_by_id = table.Column<int>(type: "integer", nullable: false),
                    closed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_closed_periods", x => x.id);
                    table.CheckConstraint("ck_closed_periods_month", "EXTRACT(DAY FROM month) = 1");
                    table.ForeignKey(
                        name: "fk_closed_periods_people_closed_by_id",
                        column: x => x.closed_by_id,
                        principalTable: "people",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "parent_links",
                columns: table => new
                {
                    parent_id = table.Column<int>(type: "integer", nullable: false),
                    student_id = table.Column<int>(type: "integer", nullable: false),
                    notify_all = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_parent_links", x => new { x.parent_id, x.student_id });
                    table.ForeignKey(
                        name: "fk_parent_links_people_parent_id",
                        column: x => x.parent_id,
                        principalTable: "people",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_parent_links_people_student_id",
                        column: x => x.student_id,
                        principalTable: "people",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "person_roles",
                columns: table => new
                {
                    person_id = table.Column<int>(type: "integer", nullable: false),
                    role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_person_roles", x => new { x.person_id, x.role });
                    table.ForeignKey(
                        name: "fk_person_roles_people_person_id",
                        column: x => x.person_id,
                        principalTable: "people",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "school_classes",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    academic_year_id = table.Column<int>(type: "integer", nullable: false),
                    grade = table.Column<int>(type: "integer", nullable: false),
                    letter = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    class_teacher_id = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_school_classes", x => x.id);
                    table.CheckConstraint("ck_school_classes_grade", "grade BETWEEN 1 AND 11");
                    table.ForeignKey(
                        name: "fk_school_classes_academic_years_academic_year_id",
                        column: x => x.academic_year_id,
                        principalTable: "academic_years",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_school_classes_people_class_teacher_id",
                        column: x => x.class_teacher_id,
                        principalTable: "people",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "series",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    teacher_id = table.Column<int>(type: "integer", nullable: false),
                    subject_id = table.Column<int>(type: "integer", nullable: false),
                    room_id = table.Column<int>(type: "integer", nullable: false),
                    day_of_week = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    lesson_number = table.Column<int>(type: "integer", nullable: true),
                    start_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    duration_minutes = table.Column<int>(type: "integer", nullable: false),
                    capacity = table.Column<int>(type: "integer", nullable: false),
                    topic = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    from_date = table.Column<DateOnly>(type: "date", nullable: false),
                    to_date = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_series", x => x.id);
                    table.CheckConstraint("ck_series_capacity", "capacity >= 1");
                    table.CheckConstraint("ck_series_dates", "to_date >= from_date");
                    table.CheckConstraint("ck_series_duration", "duration_minutes > 0");
                    table.ForeignKey(
                        name: "fk_series_people_teacher_id",
                        column: x => x.teacher_id,
                        principalTable: "people",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_series_rooms_room_id",
                        column: x => x.room_id,
                        principalTable: "rooms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_series_subjects_subject_id",
                        column: x => x.subject_id,
                        principalTable: "subjects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "class_memberships",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    student_id = table.Column<int>(type: "integer", nullable: false),
                    school_class_id = table.Column<int>(type: "integer", nullable: false),
                    from_date = table.Column<DateOnly>(type: "date", nullable: false),
                    to_date = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_class_memberships", x => x.id);
                    table.CheckConstraint("ck_class_memberships_dates", "to_date IS NULL OR to_date >= from_date");
                    table.ForeignKey(
                        name: "fk_class_memberships_people_student_id",
                        column: x => x.student_id,
                        principalTable: "people",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_class_memberships_school_classes_school_class_id",
                        column: x => x.school_class_id,
                        principalTable: "school_classes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "lessons",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    series_id = table.Column<int>(type: "integer", nullable: true),
                    teacher_id = table.Column<int>(type: "integer", nullable: false),
                    subject_id = table.Column<int>(type: "integer", nullable: false),
                    room_id = table.Column<int>(type: "integer", nullable: false),
                    starts_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    duration_minutes = table.Column<int>(type: "integer", nullable: false),
                    actual_duration_minutes = table.Column<int>(type: "integer", nullable: true),
                    lesson_number = table.Column<int>(type: "integer", nullable: true),
                    capacity = table.Column<int>(type: "integer", nullable: false),
                    topic = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lessons", x => x.id);
                    table.CheckConstraint("ck_lessons_capacity", "capacity >= 1");
                    table.CheckConstraint("ck_lessons_duration", "duration_minutes > 0");
                    table.ForeignKey(
                        name: "fk_lessons_people_teacher_id",
                        column: x => x.teacher_id,
                        principalTable: "people",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_lessons_rooms_room_id",
                        column: x => x.room_id,
                        principalTable: "rooms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_lessons_series_series_id",
                        column: x => x.series_id,
                        principalTable: "series",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_lessons_subjects_subject_id",
                        column: x => x.subject_id,
                        principalTable: "subjects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "series_enrollments",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    series_id = table.Column<int>(type: "integer", nullable: false),
                    student_id = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_series_enrollments", x => x.id);
                    table.ForeignKey(
                        name: "fk_series_enrollments_people_student_id",
                        column: x => x.student_id,
                        principalTable: "people",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_series_enrollments_series_series_id",
                        column: x => x.series_id,
                        principalTable: "series",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "enrollments",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    lesson_id = table.Column<int>(type: "integer", nullable: false),
                    student_id = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attendance = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_enrollments", x => x.id);
                    table.ForeignKey(
                        name: "fk_enrollments_lessons_lesson_id",
                        column: x => x.lesson_id,
                        principalTable: "lessons",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_enrollments_people_student_id",
                        column: x => x.student_id,
                        principalTable: "people",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_academic_years_name",
                table: "academic_years",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_at",
                table: "audit_log",
                column: "at");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_entity_type_entity_id",
                table: "audit_log",
                columns: new[] { "entity_type", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "ix_class_memberships_school_class_id",
                table: "class_memberships",
                column: "school_class_id");

            migrationBuilder.CreateIndex(
                name: "ix_class_memberships_student_id_current",
                table: "class_memberships",
                column: "student_id",
                unique: true,
                filter: "to_date IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_class_memberships_student_id_from_date",
                table: "class_memberships",
                columns: new[] { "student_id", "from_date" });

            migrationBuilder.CreateIndex(
                name: "ix_closed_periods_closed_by_id",
                table: "closed_periods",
                column: "closed_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_closed_periods_month",
                table: "closed_periods",
                column: "month",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_enrollments_lesson_id_student_id",
                table: "enrollments",
                columns: new[] { "lesson_id", "student_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_enrollments_student_id",
                table: "enrollments",
                column: "student_id");

            migrationBuilder.CreateIndex(
                name: "ix_lessons_room_id",
                table: "lessons",
                column: "room_id");

            migrationBuilder.CreateIndex(
                name: "ix_lessons_series_id",
                table: "lessons",
                column: "series_id");

            migrationBuilder.CreateIndex(
                name: "ix_lessons_starts_at",
                table: "lessons",
                column: "starts_at");

            migrationBuilder.CreateIndex(
                name: "ix_lessons_subject_id",
                table: "lessons",
                column: "subject_id");

            migrationBuilder.CreateIndex(
                name: "ix_lessons_teacher_id_starts_at",
                table: "lessons",
                columns: new[] { "teacher_id", "starts_at" });

            migrationBuilder.CreateIndex(
                name: "ix_parent_links_student_id",
                table: "parent_links",
                column: "student_id");

            migrationBuilder.CreateIndex(
                name: "ix_people_last_name_first_name",
                table: "people",
                columns: new[] { "last_name", "first_name" });

            migrationBuilder.CreateIndex(
                name: "ix_people_max_user_id",
                table: "people",
                column: "max_user_id",
                unique: true,
                filter: "max_user_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_people_phone",
                table: "people",
                column: "phone",
                unique: true,
                filter: "phone IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_rooms_name",
                table: "rooms",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_school_classes_academic_year_id_grade_letter",
                table: "school_classes",
                columns: new[] { "academic_year_id", "grade", "letter" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_school_classes_class_teacher_id",
                table: "school_classes",
                column: "class_teacher_id");

            migrationBuilder.CreateIndex(
                name: "ix_series_room_id",
                table: "series",
                column: "room_id");

            migrationBuilder.CreateIndex(
                name: "ix_series_subject_id",
                table: "series",
                column: "subject_id");

            migrationBuilder.CreateIndex(
                name: "ix_series_teacher_id",
                table: "series",
                column: "teacher_id");

            migrationBuilder.CreateIndex(
                name: "ix_series_enrollments_series_id_student_id",
                table: "series_enrollments",
                columns: new[] { "series_id", "student_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_series_enrollments_student_id",
                table: "series_enrollments",
                column: "student_id");

            migrationBuilder.CreateIndex(
                name: "ix_subjects_name",
                table: "subjects",
                column: "name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_log");

            migrationBuilder.DropTable(
                name: "calendar_days");

            migrationBuilder.DropTable(
                name: "class_memberships");

            migrationBuilder.DropTable(
                name: "closed_periods");

            migrationBuilder.DropTable(
                name: "enrollments");

            migrationBuilder.DropTable(
                name: "lesson_periods");

            migrationBuilder.DropTable(
                name: "parent_links");

            migrationBuilder.DropTable(
                name: "person_roles");

            migrationBuilder.DropTable(
                name: "series_enrollments");

            migrationBuilder.DropTable(
                name: "vacations");

            migrationBuilder.DropTable(
                name: "school_classes");

            migrationBuilder.DropTable(
                name: "lessons");

            migrationBuilder.DropTable(
                name: "academic_years");

            migrationBuilder.DropTable(
                name: "series");

            migrationBuilder.DropTable(
                name: "people");

            migrationBuilder.DropTable(
                name: "rooms");

            migrationBuilder.DropTable(
                name: "subjects");
        }
    }
}
