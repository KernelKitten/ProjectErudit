using System.Globalization;
using Erudit.Infrastructure;
using Erudit.Infrastructure.Persistence;
using Erudit.Infrastructure.Persistence.Seed;
using Erudit.Web.Auth;
using Erudit.Web.Components;
using Hangfire;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;

// Русские названия дней недели и формат дат («ср 21.10») для всего приложения.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("ru-RU");

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);

// Вход по cookie: после входа браузер получает зашифрованный «пропуск» и прикладывает его к каждому запросу.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "erudit.auth";
        options.Cookie.HttpOnly = true; // JavaScript страницы не может прочитать cookie
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.LoginPath = "/login";
        options.LogoutPath = "/auth/logout";
        options.AccessDeniedPath = "/access-denied";
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true; // срок продлевается, пока человек пользуется сайтом
    });
builder.Services.AddAuthorization();
// Даёт компонентам Blazor доступ к текущему пользователю (<AuthorizeView>, [CascadingParameter] Task<AuthenticationState>).
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<SignInService>();

// На стенде сайт стоит за nginx: nginx принимает HTTPS и передаёт запрос нам по обычному HTTP.
// Из заголовков X-Forwarded-For / X-Forwarded-Proto узнаём настоящий адрес клиента и то, что снаружи был https
// (иначе UseHttpsRedirection будет бесконечно переадресовывать на https).
// Доверяем любому прокси: порт сайта на стенде открыт только в домашней сети.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

// Demo — тестовый стенд для показа (erudit.23mx.ru): тестовые данные как при разработке,
// но без входа разработчика /dev/login (он есть только в Development).
var isDemo = app.Environment.IsEnvironment("Demo");

if (app.Environment.IsDevelopment() || isDemo)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<EruditDbContext>();

    // На стенде схема базы обновляется сама при каждом запуске (на компьютере — dotnet ef database update).
    if (app.Configuration.GetValue<bool>("MigrateOnStartup"))
    {
        await db.Database.MigrateAsync();
    }

    // Заполняем пустую базу тестовыми данными (если включено в appsettings.Development.json).
    if (app.Configuration.GetValue<bool>("SeedDevData") && await DevDataSeeder.SeedAsync(db, DateTime.UtcNow))
    {
        app.Logger.LogInformation("База заполнена тестовыми данными");
    }

    // Настоящие номера из User Secrets получают тестовые люди — чтобы войти через MAX под ними:
    //   Dev:MyPhone              → тестовый администратор Зайцев А.П.;
    //   Dev:Phones:Фамилия Имя   → любой человек из тестовых данных.
    var phones = app.Configuration.GetSection("Dev:Phones").GetChildren()
        .Select(s => (Person: s.Key, Phone: s.Value))
        .Append((Person: DevDataSeeder.DefaultDeveloperPerson, Phone: app.Configuration["Dev:MyPhone"]))
        .Where(x => !string.IsNullOrWhiteSpace(x.Phone));
    foreach (var (personName, phone) in phones)
    {
        var result = await DevDataSeeder.AssignDeveloperPhoneAsync(db, personName, phone!);
        app.Logger.LogInformation("Номер разработчика: {Result}", result);
    }
}

// Configure the HTTP request pipeline.
app.UseForwardedHeaders(); // первым — чтобы всё остальное уже видело настоящие адрес и https
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapAuthEndpoints();
app.Services.ScheduleRecurringJobs();
app.MapHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = [new HangfireAdminFilter()],
    DashboardTitle = "Эрудит · фоновые задачи",
    AppPath = "/admin", // куда ведёт ссылка «назад на сайт»
});
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
