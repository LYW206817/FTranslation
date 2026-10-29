#!/usr/bin/env bash
# 一次性准备一台空白的 Ubuntu 机器。重复执行也安全。
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
# 尽量用内存、少用 swap。这段必须在 if/else 两个分支之外执行：阿里云 Ubuntu 镜像
# 自带 /swapfile，创建分支永远不触发，调优曾经
# 因此被悄悄跳过（真机上发现 vm.swappiness=20）。
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
# 备份脚本需要 sqlite3，其余是 Caddy 的依赖。
apt-get install -y --no-install-recommends sqlite3 ca-certificates curl debian-keyring debian-archive-keyring apt-transport-https

if ! command -v caddy >/dev/null 2>&1; then
	curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' \
		| gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
	curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' \
		| tee /etc/apt/sources.list.d/caddy-stable.list >/dev/null
	apt-get update
	apt-get install -y caddy
	# Caddy 包会启用并启动它自己的服务，那会在
	# 备案号还没下来时就把默认页发布到 ：80。等
	# Caddyfile 里换成真实已备案域名后，install.sh 会重新启用它。
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
	# 没真正装好公钥之前绝不动 sshd，否则这个脚本就成了
	# 最后一件还能把我们锁在门外的事。
	if [ -s /root/.ssh/authorized_keys ]; then
		chmod 700 /root/.ssh
		chmod 600 /root/.ssh/authorized_keys
		if [ ! -f /etc/ssh/sshd_config.d/99-ft.conf ]; then
			cat > /etc/ssh/sshd_config.d/99-ft.conf <<'FTSSH'
# 由 deploy/bootstrap.sh 生成。
# 公网侧只认密钥，root 永不接受密码。
PermitRootLogin prohibit-password
PasswordAuthentication no
KbdInteractiveAuthentication no
MaxAuthTries 4

# 控制台文件管理器（Workbench）会用实例密码走真实的 sshd 会话
# 登录，所以必须为它保留密码认证——但只对阿里云内部
# 服务网段开放。每个地址的行为用下面的命令验证：
#   验证命令：sshd -T -C user=root,host=<hostname>,addr=<ip>
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
		# 先放行再启用：启用时若还没有 22 的规则，会直接切断当前会话。
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
