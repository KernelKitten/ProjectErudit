#!/bin/bash
# Выполняется на сервере (его копирует и запускает deploy.ps1):
#   bash /tmp/activate-release.sh <версия>
# Распаковывает /tmp/erudit-publish.tar.gz в /opt/erudit/releases/<версия>, переключает ссылку
# /opt/erudit/app на новую папку, перезапускает службу и ждёт, пока сайт ответит.
set -euo pipefail
release="$1"
dir="/opt/erudit/releases/$release"

mkdir -p "$dir"
tar -xzf /tmp/erudit-publish.tar.gz -C "$dir"
rm /tmp/erudit-publish.tar.gz
chown -R root:erudit "$dir"
ln -sfn "$dir" /opt/erudit/app
systemctl restart erudit

# Оставляем 5 последних версий — на случай отката (переключить ссылку /opt/erudit/app и перезапустить службу).
cd /opt/erudit/releases && ls -1t | tail -n +6 | xargs -r rm -rf

for _ in $(seq 1 30); do
    if curl -fs -o /dev/null http://localhost:5000/login; then
        echo "Сайт отвечает, версия $release"
        exit 0
    fi
    sleep 2
done
echo "Сайт не ответил за минуту. Последние строки журнала:"
journalctl -u erudit -n 40 --no-pager
exit 1
