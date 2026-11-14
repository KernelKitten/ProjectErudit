// Небольшие помощники JavaScript, которые вызываются из C# через IJSRuntime.
window.erudit = {
    // Строка браузера: по ней сервер составляет «Windows, Chrome» для сообщения в боте.
    userAgent: () => navigator.userAgent,

    // Cookie для настроек вида (тема): не секрет, поэтому ставим из браузера, на год.
    setCookie: (name, value) => {
        document.cookie = `${name}=${encodeURIComponent(value)}; path=/; max-age=31536000; SameSite=Lax`;
    },

    // Скачать файл, который сервер передал потоком (DotNetStreamReference): собираем Blob и «нажимаем» ссылку.
    downloadFile: async (fileName, contentType, streamRef) => {
        const blob = new Blob([await streamRef.arrayBuffer()], { type: contentType });
        const url = URL.createObjectURL(blob);
        const link = document.createElement('a');
        link.href = url;
        link.download = fileName;
        link.click();
        URL.revokeObjectURL(url);
    },
};
