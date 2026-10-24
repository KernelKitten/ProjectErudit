using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Erudit.Infrastructure.Max;

/// <summary>
/// Проверка сертификата сервера MAX.
/// MAX API (platform-api2.max.ru) использует сертификат от Russian Trusted Root CA (Минцифры),
/// которого нет в стандартных списках доверенных центров Windows и Linux. Доверяем этому центру
/// <b>только</b> в запросах к MAX: систему не меняем, остальные программы и сайты ему по-прежнему не доверяют.
/// </summary>
/// <remarks>
/// Файл russian_trusted_root_ca.pem скачан 2026-10-24 с https://gu-st.ru/content/lending/russian_trusted_root_ca_pem.crt
/// и проверен: цепочка сертификата platform-api2.max.ru подписью сходится к нему. Действует до 27.02.2032.
/// </remarks>
public static class MaxCertificateTrust
{
    /// <summary>Отпечаток (SHA-1) корневого сертификата — чтобы тест заметил, если файл подменят.</summary>
    public const string RootThumbprint = "8FF915CCAB7BC16F8C5C8099D53E0E115B3AEC2F";

    public static X509Certificate2 RussianTrustedRoot { get; } = LoadRoot();

    /// <summary>
    /// Подходит для <see cref="SslClientAuthenticationOptions.RemoteCertificateValidationCallback"/>.
    /// </summary>
    public static bool Validate(X509Certificate2? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        // Системе сертификат и так подходит.
        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        // Другие ошибки (имя сервера не совпадает, сертификата нет) не прощаем —
        // наш корень помогает только с одной: «не знаю, кто выдал».
        if (errors != SslPolicyErrors.RemoteCertificateChainErrors || certificate is null)
        {
            return false;
        }

        // Строим цепочку заново, но доверяем только нашему корню.
        using var custom = new X509Chain();
        custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        custom.ChainPolicy.CustomTrustStore.Add(RussianTrustedRoot);
        // Как и обычная проверка HTTPS в .NET, списки отзыва не запрашиваем.
        custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        // Промежуточный сертификат (Russian Trusted Sub CA) сервер присылает сам — берём его из исходной цепочки.
        if (chain is not null)
        {
            foreach (var element in chain.ChainElements.Skip(1))
            {
                custom.ChainPolicy.ExtraStore.Add(element.Certificate);
            }
        }
        return custom.Build(certificate);
    }

    private static X509Certificate2 LoadRoot()
    {
        using var stream = typeof(MaxCertificateTrust).Assembly
            .GetManifestResourceStream("Erudit.Infrastructure.Max.russian_trusted_root_ca.pem")
            ?? throw new InvalidOperationException("Не найден встроенный файл russian_trusted_root_ca.pem");
        using var reader = new StreamReader(stream);
        return X509Certificate2.CreateFromPem(reader.ReadToEnd());
    }
}
