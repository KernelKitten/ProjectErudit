namespace Erudit.Web.Theme;

public enum ThemeMode
{
    System, // как в системе (Windows, телефон)
    Light,
    Dark,
}

/// <summary>
/// Выбранная тема на время работы со страницей (одна на вкладку: scoped). Выбор хранится в cookie браузера:
/// <see cref="ModeCookie"/> — что выбрал человек, <see cref="SystemCookie"/> — тёмная ли тема в системе
/// (запоминаем, чтобы уже первая отрисовка на сервере была в нужных цветах и страница не мелькала белым).
/// Cookie читает App.razor и передаёт в Routes → <see cref="Init"/>.
/// </summary>
public sealed class ThemeState
{
    public const string ModeCookie = "erudit.theme";
    public const string SystemCookie = "erudit.theme.sys";

    public ThemeMode Mode { get; private set; }
    public bool SystemDark { get; private set; }

    public bool IsDark => Mode == ThemeMode.Dark || (Mode == ThemeMode.System && SystemDark);

    /// <summary>Тема поменялась — макет перерисовывается.</summary>
    public event Action? Changed;

    public void Init(ThemeMode mode, bool systemDark) => (Mode, SystemDark) = (mode, systemDark);

    public void SetMode(ThemeMode mode)
    {
        Mode = mode;
        Changed?.Invoke();
    }

    public void SetSystemDark(bool dark)
    {
        SystemDark = dark;
        Changed?.Invoke();
    }

    /// <summary>По кругу: как в системе → светлая → тёмная → как в системе.</summary>
    public static ThemeMode Next(ThemeMode mode) => mode switch
    {
        ThemeMode.System => ThemeMode.Light,
        ThemeMode.Light => ThemeMode.Dark,
        _ => ThemeMode.System,
    };

    public static ThemeMode Parse(string? value) => Enum.TryParse<ThemeMode>(value, ignoreCase: true, out var mode) ? mode : ThemeMode.System;
}
