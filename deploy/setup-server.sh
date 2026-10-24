#!/bin/bash
# Первичная настройка контейнера тестового стенда (Ubuntu 22.04). Запускать от root.
# Повторный запуск ничего не ломает.
#   ssh erudit-demo "bash -s" < deploy/setup-server.sh
# (erudit.service копируется на сервер отдельно — это делает deploy.ps1 -Setup)
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive

# .NET 10 есть только в репозитории Canonical dotnet/backports (в самой Ubuntu 22.04 — только .NET 8).
if [[ "$(apt-cache policy aspnetcore-runtime-10.0)" != *"Candidate: 10"* ]]; then
    apt-get install -y -qq software-properties-common
    add-apt-repository -y ppa:dotnet/backports
    apt-get update -qq
fi
# curl — deploy.ps1 проверяет им, что сайт запустился; psql — для ручных запросов к базе.
apt-get install -y -qq aspnetcore-runtime-10.0 postgresql-client curl

# Сайт работает от своего пользователя, не от root.
# В домашней папке ASP.NET хранит ключи Data Protection (ими подписаны cookie входа).
if ! id erudit >/dev/null 2>&1; then
    useradd --system --create-home --home-dir /var/lib/erudit --shell /usr/sbin/nologin erudit
fi

mkdir -p /opt/erudit/releases /etc/erudit
# Файл секретов: читать может только root и группа erudit.
touch /etc/erudit/erudit.env
chown root:erudit /etc/erudit/erudit.env
chmod 640 /etc/erudit/erudit.env

if [ -f /tmp/erudit.service ]; then
    mv /tmp/erudit.service /etc/systemd/system/erudit.service
    systemctl daemon-reload
    systemctl enable erudit >/dev/null 2>&1
fi
echo "Сервер готов"
