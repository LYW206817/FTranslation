#!/usr/bin/env bash
# One-shot preparation of a bare Ubuntu box. Safe to run twice.
set -euo pipefail

APP_DIR=/opt/fasttranslation
DATA_DIR=/var/lib/fasttranslation
WEB_DIR=/var/www/fasttranslation
BACKUP_DIR=/var/backups/fasttranslation

echo "==> swap (2 GiB), so a burst of traffic cannot OOM-kill the server"
if [ ! -f /swapfile ]; then
	fallocate -l 2G /swapfile
	chmod 600 /swapfile
	mkswap /swapfile
	swapon /swapfile
	grep -q '^/swapfile' /etc/fstab || echo '/swapfile none swap sw 0 0' >> /etc/fstab
	# Prefer RAM; only fall back to swap under real pressure.
	echo 'vm.swappiness=10' > /etc/sysctl.d/99-ft.conf
	/usr/sbin/sysctl -q -p /etc/sysctl.d/99-ft.conf
else
	echo "    /swapfile already present"
fi

echo "==> service account"
if ! id ftserver >/dev/null 2>&1; then
	useradd --system --home-dir "$DATA_DIR" --shell /usr/sbin/nologin ftserver
fi

echo "==> directories"
install -d -o root -g ftserver -m 0755 "$APP_DIR"
install -d -o ftserver -g ftserver -m 0700 "$DATA_DIR"
install -d -o root -g root -m 0755 "$WEB_DIR"
install -d -o root -g root -m 0755 "$BACKUP_DIR"

echo "==> packages"
export DEBIAN_FRONTEND=noninteractive
apt-get update
# sqlite3 is needed by the backup script; the rest are Caddy prerequisites.
apt-get install -y --no-install-recommends sqlite3 ca-certificates curl debian-keyring debian-archive-keyring apt-transport-https

if ! command -v caddy >/dev/null 2>&1; then
	curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' \
		| gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
	curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' \
		| tee /etc/apt/sources.list.d/caddy-stable.list >/dev/null
	apt-get update
	apt-get install -y caddy
fi

echo "==> ICU check (the server is built with InvariantGlobalization=false)"
if ! ldconfig -p | grep -q 'libicu'; then
	echo "    libicu missing - installing"
	apt-get install -y --no-install-recommends libicu-dev || apt-get install -y --no-install-recommends libicu74
fi

echo "==> done. Next: copy the published binary, then 'bash bootstrap.sh deploy' steps in deploy/README.md"
