using Erudit.Infrastructure;
using Erudit.Infrastructure.Persistence;
using Erudit.Infrastructure.Persistence.Seed;
using Erudit.Web.Components;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();

// В режиме разработки заполняем пустую базу тестовыми данными (если включено в appsettings.Development.json).
if (app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("SeedDevData"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<EruditDbContext>();
    if (await DevDataSeeder.SeedAsync(db, DateTime.UtcNow))
    {
        app.Logger.LogInformation("База заполнена тестовыми данными");
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

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
