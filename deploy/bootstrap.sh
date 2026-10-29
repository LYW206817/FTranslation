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
else
	echo "    /swapfile already present"
fi
# Prefer RAM over swap. This must run in both branches: the Aliyun Ubuntu image
# ships a /swapfile already, so the create branch never fires and the tuning used
# to be skipped without anyone noticing (found on the real box: vm.swappiness=20).
echo 'vm.swappiness=10' > /etc/sysctl.d/99-ft.conf
sysctl -q -p /etc/sysctl.d/99-ft.conf

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
	# The Caddy package enables and starts its own service, which would publish the
	# default page on :80 before the ICP filing exists. install.sh re-enables it once
	# the Caddyfile names a real, filed domain.
	systemctl disable --now caddy >/dev/null 2>&1 || true
	echo "    Caddy installed but kept disabled (no filing number yet)"
fi

echo "==> ICU check (the server is built with InvariantGlobalization=false)"
if ! ldconfig -p | grep -q 'libicu'; then
	echo "    libicu missing - installing"
	apt-get install -y --no-install-recommends libicu-dev || apt-get install -y --no-install-recommends libicu74
fi

if [ "${FT_SKIP_HARDEN:-0}" = "1" ]; then
	echo "==> hardening skipped because FT_SKIP_HARDEN=1"
else
	echo "==> ssh hardening"
	# Never touch sshd unless a key is actually installed, or this script would be
	# the last thing that could still log us out.
	if [ -s /root/.ssh/authorized_keys ]; then
		chmod 700 /root/.ssh
		chmod 600 /root/.ssh/authorized_keys
		if [ ! -f /etc/ssh/sshd_config.d/99-ft.conf ]; then
			cat > /etc/ssh/sshd_config.d/99-ft.conf <<'FTSSH'
# Written by deploy/bootstrap.sh.
# From the public internet: keys only, and root never takes a password.
PermitRootLogin prohibit-password
PasswordAuthentication no
KbdInteractiveAuthentication no
MaxAuthTries 4

# The console file manager (Workbench) logs in with the instance password over a real
# sshd session, so password auth has to exist for it - but only from Aliyun's internal
# service range. Per-address behaviour is proved with:
#   sshd -T -C user=root,host=<hostname>,addr=<ip>
Match Address 100.104.0.0/16
    PasswordAuthentication yes
    PermitRootLogin yes
FTSSH
			if sshd -t; then
				systemctl reload ssh >/dev/null 2>&1 || systemctl reload sshd
				echo "    password logins disabled, root accepts keys only"
			else
				echo "    !! sshd config test failed, leaving sshd untouched"
			fi
		else
			echo "    /etc/ssh/sshd_config.d/99-ft.conf already exists"
		fi
	else
		echo "    !! no /root/.ssh/authorized_keys - skipping ssh hardening on purpose."
		echo "    !! Add your public key first (Alibaba Cloud Workbench works without a password)."
	fi

	echo "==> firewall"
	if command -v ufw >/dev/null 2>&1; then
		# Allow first, enable second: enabling without a rule for 22 would cut this session.
		ufw allow OpenSSH >/dev/null 2>&1 || ufw allow 22/tcp >/dev/null
		ufw allow 80/tcp >/dev/null
		ufw allow 443/tcp >/dev/null
		ufw --force enable >/dev/null
		ufw status | head -1
	else
		echo "    ufw not installed - the Alibaba security group is the only firewall;"
		echo "    make sure it opens 22, 80, 443 and nothing else."
	fi
fi

echo "==> done. Next: copy the published binary, then 'bash install.sh' from deploy/README.md"
