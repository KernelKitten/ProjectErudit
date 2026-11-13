// Небольшие помощники JavaScript, которые вызываются из C# через IJSRuntime.
window.erudit = {
    // Строка браузера: по ней сервер составляет «Windows, Chrome» для сообщения в боте.
    userAgent: () => navigator.userAgent,

    // Cookie для настроек вида (тема): не секрет, поэтому ставим из браузера, на год.
    setCookie: (name, value) => {
        document.cookie = `${name}=${encodeURIComponent(value)}; path=/; max-age=31536000; SameSite=Lax`;
    },
};
