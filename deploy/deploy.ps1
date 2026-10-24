<#
.SYNOPSIS
    Публикует сайт на тестовый стенд (https://erudit.23mx.ru).

.DESCRIPTION
    1. Собирает Erudit.Web под Linux (dotnet publish -r linux-x64).
    2. Упаковывает в архив и копирует на сервер по SSH.
    3. Распаковывает в новую папку /opt/erudit/releases/<дата-время>, переключает на неё ссылку
       /opt/erudit/app и перезапускает службу. Старые версии остаются (5 последних) —
       откатиться можно, переключив ссылку обратно.

    Сервер — имя erudit-demo из ~/.ssh/config (адрес и ключ — там, не в git).

.PARAMETER Setup
    Сначала выполнить первичную настройку сервера (setup-server.sh) — нужно один раз.

.EXAMPLE
    .\deploy\deploy.ps1
#>
param(
    [switch]$Setup,
    [string]$Server = "erudit-demo"
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
# Windows PowerShell 5.1 по умолчанию передаёт текст в программы (ssh) в ASCII — русские буквы станут «?».
# (А сам этот файл должен быть сохранён в UTF-8 с BOM — иначе PowerShell 5.1 прочитает его в ANSI.)
$OutputEncoding = New-Object System.Text.UTF8Encoding $false
[Console]::OutputEncoding = $OutputEncoding

function Invoke-Native([string]$what, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what — ошибка (код $LASTEXITCODE)" }
}

if ($Setup) {
    Write-Host "== Настройка сервера" -ForegroundColor Cyan
    # Скрипты не передаём через конвейер PowerShell (он добавит BOM и CRLF, и bash их не поймёт) — копируем файлами.
    Invoke-Native "Копирование файлов настройки" {
        scp -q "$PSScriptRoot\erudit.service" "$PSScriptRoot\setup-server.sh" "${Server}:/tmp/"
    }
    Invoke-Native "setup-server.sh" { ssh $Server "bash /tmp/setup-server.sh && rm /tmp/setup-server.sh" }
}

$publishDir = Join-Path $env:TEMP "erudit-publish"
$archive = Join-Path $env:TEMP "erudit-publish.tar.gz"
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }

Write-Host "== Сборка" -ForegroundColor Cyan
Invoke-Native "dotnet publish" {
    dotnet publish "$root\src\Erudit.Web" -c Release -r linux-x64 --self-contained false -o $publishDir -nologo -v q
}

Write-Host "== Копирование на сервер" -ForegroundColor Cyan
Invoke-Native "Упаковка" { tar -czf $archive -C $publishDir . }
Invoke-Native "Копирование архива" {
    scp -q $archive "$PSScriptRoot\activate-release.sh" "${Server}:/tmp/"
}

Write-Host "== Переключение версии и перезапуск" -ForegroundColor Cyan
$release = Get-Date -Format "yyyyMMdd-HHmmss"
Invoke-Native "Публикация" { ssh $Server "bash /tmp/activate-release.sh $release" }

Remove-Item -Force $archive
Write-Host "Готово: https://erudit.23mx.ru" -ForegroundColor Green
