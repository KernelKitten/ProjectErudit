using Erudit.Domain;
using Erudit.Domain.Accounting;
using Erudit.Domain.Calendar;
using Erudit.Domain.Lessons;
using Erudit.Domain.People;
using Erudit.Domain.School;
using Microsoft.EntityFrameworkCore;

namespace Erudit.Infrastructure.Persistence.Seed;

/// <summary>
/// Заполняет пустую базу выдуманными, но правдоподобными данными для разработки и тестов:
/// учебный год, звонки, предметы, кабинеты, учителя, классы, ученики, родители, каникулы, серии и занятия.
///
/// Все люди выдуманы, телефоны в формате +7000XXXXXXX — заведомо несуществующие.
/// Генератор случайных чисел с фиксированным зерном: при каждом запуске получаются одни и те же люди.
/// </summary>
public static class DevDataSeeder
{
    /// <summary>Учебный год тестовых данных.</summary>
    public static readonly DateOnly YearStart = new(2026, 9, 1);
    public static readonly DateOnly YearEnd = new(2027, 8, 31);

    /// <summary>
    /// Заполняет базу, если в ней ещё нет ни одного человека. Возвращает true, если данные добавлены.
    /// </summary>
    /// <param name="now">Текущий момент (UTC): занятия до него считаются прошедшими и получают отметки.</param>
    public static async Task<bool> SeedAsync(EruditDbContext db, DateTime now, CancellationToken ct = default)
    {
        if (await db.People.AnyAsync(ct))
        {
            return false;
        }

        var random = new Random(2026);
        var phones = new PhoneSequence();

        var year = new AcademicYear { Name = "2026/27", StartDate = YearStart, EndDate = YearEnd };
        db.AcademicYears.Add(year);

        db.LessonPeriods.AddRange(LessonPeriods());
        var subjects = Subjects().ToDictionary(s => s.ShortName!);
        db.Subjects.AddRange(subjects.Values);
        var rooms = Rooms().ToDictionary(r => r.Name);
        db.Rooms.AddRange(rooms.Values);

        var calendarDays = CalendarDays().ToList();
        db.CalendarDays.AddRange(calendarDays);
        db.Vacations.AddRange(Vacations());

        // --- Учителя ---
        var teachers = TeacherNames
            .Select(n => NewPerson(n.Last, n.First, n.Middle, phones.Next(), Role.Teacher))
            .ToList();
        teachers[0].Roles.Add(new PersonRole { Role = Role.Admin });       // учитель информатики — администратор
        teachers[1].Roles.Add(new PersonRole { Role = Role.HeadTeacher }); // завуч
        db.People.AddRange(teachers);
        var headTeacher = teachers[1];

        // --- Классы: у некоторых учителей по два класса ---
        var classes = new List<SchoolClass>();
        for (var i = 0; i < ClassNames.Length; i++)
        {
            var (grade, letter) = ClassNames[i];
            classes.Add(new SchoolClass
            {
                AcademicYear = year,
                Grade = grade,
                Letter = letter,
                ClassTeacher = teachers[(i + 2) % teachers.Count],
            });
        }
        db.SchoolClasses.AddRange(classes);

        // --- Ученики и родители ---
        var studentsByClass = new Dictionary<SchoolClass, List<Person>>();
        foreach (var schoolClass in classes)
        {
            var list = new List<Person>();
            for (var i = 0; i < StudentsPerClass; i++)
            {
                var (last, first, middle) = RandomChildName(random);
                // У младших (0–4) телефона чаще нет: ими занимаются родители.
                var phone = schoolClass.Grade >= 5 || random.Next(4) == 0 ? phones.Next() : null;
                var student = NewPerson(last, first, middle, phone, Role.Student);
                list.Add(student);
                db.ClassMemberships.Add(new ClassMembership
                {
                    Student = student,
                    SchoolClass = schoolClass,
                    FromDate = YearStart,
                });

                // Родители: у всех младших и у части старших.
                if (schoolClass.Grade <= 4 || random.Next(3) == 0)
                {
                    // Мама: фамилия ребёнка в женской форме.
                    var parent = NewPerson(ToFemale(last), RandomFrom(random, FemaleNames),
                        RandomFrom(random, FemaleMiddleNames), phones.Next(), Role.Parent);
                    db.ParentLinks.Add(new ParentLink
                    {
                        Parent = parent,
                        Student = student,
                        NotifyAll = schoolClass.Grade <= 4,
                        CreatedAt = now,
                    });
                }
            }
            studentsByClass[schoolClass] = list;
        }

        // Перевод: первый ученик 7а с 1 октября учится в 7б.
        var class7a = classes.Single(c => c.Name == "7а");
        var class7b = classes.Single(c => c.Name == "7б");
        var transferred = studentsByClass[class7a][0];
        var oldMembership = db.ClassMemberships.Local.Single(m => m.Student == transferred);
        oldMembership.ToDate = new DateOnly(2026, 9, 30);
        db.ClassMemberships.Add(new ClassMembership
        {
            Student = transferred,
            SchoolClass = class7b,
            FromDate = new DateOnly(2026, 10, 1),
        });
        studentsByClass[class7a].Remove(transferred);
        studentsByClass[class7b].Add(transferred);

        // --- Регулярные занятия ---
        var calendar = new WorkingDayCalendar(calendarDays);
        var periods = LessonPeriods().ToDictionary(p => p.Number);
        Person ByLast(string lastName) => teachers.Single(t => t.LastName == lastName);
        List<Person> From(string className, int count) =>
            studentsByClass[classes.Single(c => c.Name == className)].Take(count).ToList();

        var seriesPlan = new (Person Teacher, string Subject, string Room, DayOfWeek Day, int Lesson, int Capacity, string? Topic, List<Person> Students)[]
        {
            (ByLast("Соколова"), "Матем", "Каб.31", DayOfWeek.Wednesday, 8, 8, "Подготовка к ОГЭ",
                [.. From("9а", 4), .. From("9б", 3)]),
            (ByLast("Белова"), "Русский", "Каб.31", DayOfWeek.Monday, 9, 6, "Подготовка к ЕГЭ",
                From("11", 5)),
            (ByLast("Орлов"), "Физика", "Каб.Естеств", DayOfWeek.Thursday, 8, 5, "Олимпиадные задачи",
                [.. From("10", 2), .. From("11", 2)]),
            (ByLast("Морозова"), "Чтение", "Каб.12", DayOfWeek.Tuesday, 0, 4, null,
                From("1а", 3)),
            (ByLast("Морозова"), "Матем", "Каб.12", DayOfWeek.Thursday, 0, 4, null,
                From("1б", 4)),
            (ByLast("Ким"), "Англ.яз", "Каб.А1", DayOfWeek.Friday, 7, 3, null,
                From("5а", 2)),
        };

        foreach (var plan in seriesPlan)
        {
            var period = periods[plan.Lesson];
            var series = new Series
            {
                Teacher = plan.Teacher,
                Subject = subjects[plan.Subject],
                Room = rooms[plan.Room],
                DayOfWeek = plan.Day,
                LessonNumber = plan.Lesson,
                StartTime = period.Start,
                DurationMinutes = period.DurationMinutes,
                Capacity = plan.Capacity,
                Topic = plan.Topic,
                FromDate = new DateOnly(2026, 9, 7),
                ToDate = new DateOnly(2026, 12, 25),
            };
            db.Series.Add(series);

            foreach (var student in plan.Students)
            {
                db.SeriesEnrollments.Add(new SeriesEnrollment { Series = series, Student = student, CreatedAt = now });
            }

            foreach (var lesson in SeriesPlanner.CreateLessons(series, calendar))
            {
                lesson.Teacher = series.Teacher;
                lesson.Subject = series.Subject;
                lesson.Room = series.Room;
                foreach (var student in plan.Students)
                {
                    lesson.Enrollments.Add(new Enrollment { Student = student, Source = EnrollmentSource.Series, CreatedAt = now });
                }
                MarkIfPast(lesson, now, random);
                db.Lessons.Add(lesson);
            }
        }

        // --- Разовые занятия: индивидуальное, отменённое, на каникулах ---
        var single = new[]
        {
            NewSingleLesson(ByLast("Соколова"), subjects["Матем"], rooms["Каб.31"], new DateOnly(2026, 10, 30), new TimeOnly(10, 0), 60,
                "Разбор пробника (каникулы)", [From("9а", 6)[5]], now, random),
            NewSingleLesson(ByLast("Белова"), subjects["Литература"], rooms["Каб.21"], new DateOnly(2026, 10, 21), periods[8].Start, 40,
                "Сочинение", [From("11", 6)[5]], now, random),
            NewSingleLesson(ByLast("Орлов"), subjects["Физика"], rooms["Каб.Естеств"], new DateOnly(2026, 10, 22), periods[7].Start, 40,
                null, [], now, random, cancelled: true),
            NewSingleLesson(ByLast("Зайцев"), subjects["ИКТ"], rooms["Каб.ИКТ"], new DateOnly(2026, 11, 12), periods[8].Start, 40,
                "Подготовка к олимпиаде", From("8б", 2), now, random),
        };
        db.Lessons.AddRange(single);

        // Сентябрь закрыт завучем.
        db.ClosedPeriods.Add(new ClosedPeriod { Month = new DateOnly(2026, 9, 1), ClosedBy = headTeacher, ClosedAt = now });

        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Тестовый администратор — ему по умолчанию достаётся номер из Dev:MyPhone.</summary>
    public const string DefaultDeveloperPerson = "Зайцев Андрей";

    /// <summary>
    /// Отдаёт настоящий номер тестовому человеку «Фамилия Имя», чтобы войти через MAX под ним.
    /// Если номер уже есть у кого-то в базе (например, данные поправили вручную) — ничего не меняет.
    /// Никогда не останавливает запуск: возвращает текст для лога.
    /// </summary>
    public static async Task<string> AssignDeveloperPhoneAsync(EruditDbContext db, string lastAndFirstName, string phone,
        CancellationToken ct = default)
    {
        var normalized = PhoneNumber.Normalize(phone);
        if (normalized is null)
        {
            return $"номер для «{lastAndFirstName}» не похож на российский — пропущен";
        }

        var owner = await db.People.SingleOrDefaultAsync(p => p.Phone == normalized, ct);
        if (owner is not null)
        {
            return $"номер уже у {owner.ShortName} — оставлен как есть";
        }

        var parts = lastAndFirstName.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var people = parts.Length != 2 ? []
            : await db.People.Where(p => p.LastName == parts[0] && p.FirstName == parts[1]).ToListAsync(ct);
        if (people.Count != 1)
        {
            return $"«{lastAndFirstName}»: найдено {people.Count} человек, нужен ровно один — номер не привязан";
        }

        people[0].Phone = normalized;
        await db.SaveChangesAsync(ct);
        return $"номер привязан к {people[0].ShortName}";
    }

    private const int StudentsPerClass = 10;

    private static readonly (int Grade, string Letter)[] ClassNames =
    [
        (1, "а"), (1, "б"), (4, "а"), (5, "а"), (5, "б"), (7, "а"), (7, "б"), (8, "б"), (9, "а"), (9, "б"), (10, ""), (11, ""),
    ];

    private static readonly (string Last, string First, string Middle)[] TeacherNames =
    [
        ("Зайцев", "Андрей", "Петрович"),      // информатика, администратор
        ("Белова", "Ольга", "Сергеевна"),      // русский язык, завуч
        ("Соколова", "Марина", "Викторовна"),  // математика
        ("Орлов", "Дмитрий", "Николаевич"),    // физика
        ("Морозова", "Елена", "Александровна"),// начальные классы
        ("Ким", "Анна", "Юрьевна"),            // английский язык
        ("Лебедев", "Игорь", "Олегович"),      // история
        ("Ершова", "Наталья", "Ивановна"),     // химия, биология
    ];

    private static IEnumerable<LessonPeriod> LessonPeriods() =>
    [
        new() { Number = 0, Start = new(7, 30), End = new(7, 50) },  // до первого урока (начальная школа)
        new() { Number = 1, Start = new(8, 0), End = new(8, 40) },
        new() { Number = 2, Start = new(8, 55), End = new(9, 35) },
        new() { Number = 3, Start = new(9, 50), End = new(10, 30) },
        new() { Number = 4, Start = new(10, 40), End = new(11, 20) },
        new() { Number = 5, Start = new(11, 30), End = new(12, 10) },
        new() { Number = 6, Start = new(12, 20), End = new(13, 0) },
        new() { Number = 7, Start = new(13, 25), End = new(14, 5) },
        new() { Number = 8, Start = new(14, 15), End = new(14, 55) }, // время 8-го и 9-го — предположение,
        new() { Number = 9, Start = new(15, 5), End = new(15, 45) },  // настоящее задаст администратор
    ];

    private static IEnumerable<Subject> Subjects() =>
    [
        new() { Name = "Математика", ShortName = "Матем" },
        new() { Name = "Русский язык", ShortName = "Русский" },
        new() { Name = "Литература", ShortName = "Литература" },
        new() { Name = "Литературное чтение", ShortName = "Чтение" },
        new() { Name = "Английский язык", ShortName = "Англ.яз" },
        new() { Name = "Физика", ShortName = "Физика" },
        new() { Name = "Химия", ShortName = "Химия" },
        new() { Name = "Биология", ShortName = "Биолог" },
        new() { Name = "История", ShortName = "Истор" },
        new() { Name = "Информатика", ShortName = "ИКТ" },
    ];

    private static IEnumerable<Room> Rooms() =>
    [
        new() { Name = "Каб.12", Floor = 1 },
        new() { Name = "Каб.21", Floor = 2 },
        new() { Name = "Каб.31", Floor = 3 },
        new() { Name = "Каб.А1", Floor = 4 },
        new() { Name = "Каб.ИКТ", Floor = 2 },
        new() { Name = "Каб.Естеств", Floor = 3 },
        new() { Name = "Спортзал", Floor = 0 },
    ];

    /// <summary>Праздники в будни учебного года (настоящий календарь загрузим на этапе 3).</summary>
    private static IEnumerable<CalendarDay> CalendarDays() =>
        new[]
        {
            new DateOnly(2026, 11, 4),
            new DateOnly(2027, 1, 1), new DateOnly(2027, 1, 4), new DateOnly(2027, 1, 5), new DateOnly(2027, 1, 6),
            new DateOnly(2027, 1, 7), new DateOnly(2027, 1, 8),
            new DateOnly(2027, 2, 23), new DateOnly(2027, 3, 8),
        }.Select(d => new CalendarDay { Date = d, IsWorkingDay = false });

    /// <summary>Примерные каникулы 2026/27.</summary>
    private static IEnumerable<Vacation> Vacations() =>
    [
        new() { Name = "Осенние каникулы", StartDate = new(2026, 10, 26), EndDate = new(2026, 11, 3) },
        new() { Name = "Зимние каникулы", StartDate = new(2026, 12, 29), EndDate = new(2027, 1, 10) },
        new() { Name = "Дополнительные каникулы", StartDate = new(2027, 2, 15), EndDate = new(2027, 2, 21), GradeFrom = 1, GradeTo = 1 },
        new() { Name = "Весенние каникулы", StartDate = new(2027, 3, 22), EndDate = new(2027, 3, 31) },
    ];

    private static Lesson NewSingleLesson(Person teacher, Subject subject, Room room, DateOnly date, TimeOnly time, int minutes,
        string? topic, List<Person> students, DateTime now, Random random, bool cancelled = false)
    {
        var lesson = new Lesson
        {
            Teacher = teacher,
            Subject = subject,
            Room = room,
            StartsAt = SchoolTime.ToUtc(date, time),
            DurationMinutes = minutes,
            Capacity = Math.Max(1, students.Count),
            Topic = topic,
        };
        foreach (var student in students)
        {
            lesson.Enrollments.Add(new Enrollment { Student = student, Source = EnrollmentSource.Teacher, CreatedAt = now });
        }
        if (cancelled)
        {
            lesson.Status = LessonStatus.Cancelled;
        }
        else
        {
            MarkIfPast(lesson, now, random);
        }
        return lesson;
    }

    /// <summary>Прошедшие занятия: в основном проведены, иногда кто-то отсутствовал, изредка занятие не состоялось.</summary>
    private static void MarkIfPast(Lesson lesson, DateTime now, Random random)
    {
        if (lesson.StartsAt.AddMinutes(lesson.DurationMinutes) > now)
        {
            return;
        }
        if (random.Next(15) == 0)
        {
            lesson.Status = LessonStatus.NotHeld;
            return;
        }
        lesson.Status = LessonStatus.Held;
        foreach (var enrollment in lesson.Enrollments)
        {
            enrollment.Attendance = random.Next(6) == 0 ? Attendance.Absent : Attendance.Present;
        }
    }

    private static Person NewPerson(string last, string first, string? middle, string? phone, Role role) => new()
    {
        LastName = last,
        FirstName = first,
        MiddleName = middle,
        Phone = phone,
        Roles = [new PersonRole { Role = role }],
    };

    private static readonly string[] Surnames =
        ["Иванов", "Смирнов", "Кузнецов", "Попов", "Васильев", "Петров", "Новиков", "Фёдоров", "Волков", "Алексеев",
         "Лебедев", "Семёнов", "Егоров", "Павлов", "Козлов", "Степанов", "Николаев", "Макаров", "Андреев", "Ковалёв",
         "Ильин", "Гусев", "Титов", "Кузьмин", "Кудрявцев", "Баранов", "Куликов", "Ёлкин", "Абрамов", "Жуков"];

    private static readonly string[] MaleNames =
        ["Александр", "Максим", "Михаил", "Артём", "Даниил", "Иван", "Дмитрий", "Кирилл", "Андрей", "Егор", "Никита", "Матвей", "Тимофей"];

    private static readonly string[] FemaleNames =
        ["Анна", "Мария", "Софья", "Алиса", "Виктория", "Полина", "Елизавета", "Дарья", "Варвара", "Ксения", "Ева", "Вероника", "Ульяна"];

    private static readonly string[] MaleMiddleNames =
        ["Александрович", "Сергеевич", "Дмитриевич", "Андреевич", "Алексеевич", "Михайлович", "Игоревич", "Евгеньевич"];

    private static readonly string[] FemaleMiddleNames =
        ["Александровна", "Сергеевна", "Дмитриевна", "Андреевна", "Алексеевна", "Михайловна", "Игоревна", "Евгеньевна"];

    private static (string Last, string First, string Middle) RandomChildName(Random random)
    {
        var surname = RandomFrom(random, Surnames);
        return random.Next(2) == 0
            ? (surname, RandomFrom(random, MaleNames), RandomFrom(random, MaleMiddleNames))
            : (ToFemale(surname), RandomFrom(random, FemaleNames), RandomFrom(random, FemaleMiddleNames));
    }

    /// <summary>
    /// Иванов → Иванова, Ильин → Ильина; «Иванова» остаётся как есть
    /// (для фамилий из нашего списка этого правила достаточно).
    /// </summary>
    private static string ToFemale(string surname) => surname.EndsWith('а') ? surname : surname + "а";

    private static T RandomFrom<T>(Random random, T[] items) => items[random.Next(items.Length)];

    /// <summary>Заведомо несуществующие номера +70000000001, +70000000002...</summary>
    private sealed class PhoneSequence
    {
        private int _next = 1;
        public string Next() => $"+7000{_next++:D7}";
    }
}
