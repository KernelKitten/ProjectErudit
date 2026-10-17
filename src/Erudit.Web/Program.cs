using System.Globalization;
using Erudit.Infrastructure;
using Erudit.Infrastructure.Persistence;
using Erudit.Infrastructure.Persistence.Seed;
using Erudit.Web.Auth;
using Erudit.Web.Components;
using Microsoft.AspNetCore.Authentication.Cookies;
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

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<EruditDbContext>();

    // Заполняем пустую базу тестовыми данными (если включено в appsettings.Development.json).
    if (app.Configuration.GetValue<bool>("SeedDevData") && await DevDataSeeder.SeedAsync(db, DateTime.UtcNow))
    {
        app.Logger.LogInformation("База заполнена тестовыми данными");
    }

    // Номер разработчика из User Secrets получает тестовый администратор — чтобы войти через MAX как он.
    var myPhone = app.Configuration["Dev:MyPhone"];
    if (!string.IsNullOrWhiteSpace(myPhone))
    {
        var person = await DevDataSeeder.AssignDeveloperPhoneAsync(db, myPhone);
        app.Logger.LogInformation("Номер разработчика привязан к {Person}", person);
    }
}

// Configure the HTTP request pipeline.
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
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
