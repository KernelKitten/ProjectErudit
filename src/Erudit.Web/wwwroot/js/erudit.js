// Небольшие помощники JavaScript, которые вызываются из C# через IJSRuntime.
window.erudit = {
    // Строка браузера: по ней сервер составляет «Windows, Chrome» для сообщения в боте.
    userAgent: () => navigator.userAgent,
};
