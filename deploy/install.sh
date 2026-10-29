#!/usr/bin/env bash
# Install the published binary, the systemd unit, the Caddyfile, the backup job and the
# static site. Run AFTER bootstrap.sh. Idempotent: re-running upgrades in place.
#
# Expects this directory layout (scp the whole deploy/ folder plus the published output):
#   /tmp/ftdeploy/bootstrap.sh
#   /tmp/ftdeploy/install.sh
#   /tmp/ftdeploy/fasttranslation.service
#   /tmp/ftdeploy/Caddyfile
#   /tmp/ftdeploy/backup.sh
#   /tmp/ftdeploy/fasttranslation-backup.cron
#   /tmp/ftdeploy/server/            <- publish/server-linux
#   /tmp/ftdeploy/site/              <- site/
set -euo pipefail

HERE=$(cd "$(dirname "$0")" && pwd)
APP_DIR=/opt/fasttranslation
WEB_DIR=/var/www/fasttranslation

echo "==> binary"
install -d -o root -g ftserver -m 0755 "$APP_DIR"
cp -r "$HERE/server/." "$APP_DIR/"
chown -R root:ftserver "$APP_DIR"
chmod 755 "$APP_DIR/FastTranslation.Server"

echo "==> configuration"
if [ ! -f "$APP_DIR/appsettings.Production.json" ]; then
	echo "    !! $APP_DIR/appsettings.Production.json is missing."
	echo "    !! Copy appsettings.Production.json.example there, fill in SigningKey,"
	echo "    !! AdminToken and Gateway:ApiKey, then re-run this script."
	echo "    (nothing else has been started yet)"
	exit 1
fi
chown root:ftserver "$APP_DIR/appsettings.Production.json"
chmod 640 "$APP_DIR/appsettings.Production.json"

echo "==> systemd unit"
install -m 0644 "$HERE/fasttranslation.service" /etc/systemd/system/fasttranslation.service
systemctl daemon-reload
systemctl enable fasttranslation

echo "==> static site"
install -d -m 0755 "$WEB_DIR"
if [ -d "$HERE/site" ]; then
	rm -rf "${WEB_DIR:?}"/*
	cp -r "$HERE/site/." "$WEB_DIR/"
	chown -R root:root "$WEB_DIR"
else
	echo "    (no site/ in $HERE — skipping)"
fi

if grep -q '__ICP_NUMBER__' "$WEB_DIR/index.html" 2>/dev/null; then
	echo "    !! The footer still reads __ICP_NUMBER__ instead of a real filing number."
	echo "    !! A mainland-hosted site must display it; fill it in once the管局 issues it."
fi

echo "==> backup job"
install -m 0755 "$HERE/backup.sh" /usr/local/bin/ft-backup.sh
install -m 0644 "$HERE/fasttranslation-backup.cron" /etc/cron.d/fasttranslation-backup

echo "==> Caddy"
install -m 0644 "$HERE/Caddyfile" /etc/caddy/Caddyfile
if grep -q '__DOMAIN__\|__ACME_EMAIL__' /etc/caddy/Caddyfile; then
	echo "    !! /etc/caddy/Caddyfile still has placeholders."
	echo "    !! Fill them in, then: systemctl reload caddy"
	echo "    !! Caddy is left DISABLED on purpose: serving the site before the ICP"
	echo "    !! filing is approved is not allowed, and ACME cannot issue for a"
	echo "    !! placeholder domain."
	systemctl disable --now caddy 2>/dev/null || true
else
	caddy validate --config /etc/caddy/Caddyfile
	systemctl enable caddy
	echo "    Caddyfile looks good. Start it with: systemctl reload-or-restart caddy"
fi

echo "==> server"
systemctl restart fasttranslation
sleep 2
systemctl --no-pager --lines=5 status fasttranslation || true
echo
echo "health check:"
curl -fsS http://127.0.0.1:5080/health && echo
