using Hangfire.Dashboard;

namespace Erudit.Web.Auth;

/// <summary>
/// Панель Hangfire (/hangfire) видна только администратору в активной роли.
/// Без этого фильтра Hangfire пускает в панель только запросы с самого сервера (localhost).
/// </summary>
public sealed class HangfireAdminFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context) =>
        context.GetHttpContext().User.IsInRole(AppRoles.Admin);
}
