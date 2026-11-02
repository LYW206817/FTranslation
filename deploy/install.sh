#!/usr/bin/env bash
# 安装发布好的二进制、systemd 单元、Caddyfile、备份任务和
# 静态站点。在 bootstrap.sh 之后执行。幂等：重复执行等于原地升级。
#
# 期望的目录布局（scp 整个 deploy/ 目录加上发布产物）：
#   /tmp/ftdeploy/bootstrap.sh          引导脚本
#   /tmp/ftdeploy/install.sh            安装脚本
#   /tmp/ftdeploy/fasttranslation.service  systemd 单元
#   /tmp/ftdeploy/Caddyfile             反向代理配置
#   /tmp/ftdeploy/backup.sh             数据库备份脚本
#   /tmp/ftdeploy/fasttranslation-backup.cron  备份定时任务
#   /tmp/ftdeploy/server/               发布产物（即 publish/server-linux）
#   /tmp/ftdeploy/site/                 静态站点（即 site/）
set -euo pipefail

HERE=$(cd "$(dirname "$0")" && pwd)
APP_DIR=/opt/fasttranslation
WEB_DIR=/var/www/fasttranslation

echo "==> configuration"
if [ ! -f "$APP_DIR/appsettings.Production.json" ]; then
	echo "    !! $APP_DIR/appsettings.Production.json is missing."
	echo "    !! Copy appsettings.Production.json.example there, fill in SigningKey,"
	echo "    !! AdminToken and Gateway:ApiKey, then re-run this script."
	echo "    (nothing has been copied or stopped yet)"
	exit 1
fi
chown root:ftserver "$APP_DIR/appsettings.Production.json"
chmod 640 "$APP_DIR/appsettings.Production.json"

echo "==> stop the service"
# 在拷贝之前检查，而不是之后：Linux 拒绝覆盖正在运行的
# 可执行文件（ETXTBSY），第一次真升级就死在这里，报错 "cp: cannot create regular file
# '/opt/fasttranslation/./FastTranslation.Server': Text file busy"，即内核拒绝覆盖正在运行的程序。
# 最后一节会重新启动服务。
if systemctl is-active --quiet fasttranslation; then
	systemctl stop fasttranslation
	echo "    stopped for the duration of the copy"
else
	echo "    not running"
fi

echo "==> binary"
install -d -o root -g ftserver -m 0755 "$APP_DIR"
cp -r "$HERE/server/." "$APP_DIR/"
chown -R root:ftserver "$APP_DIR"
chmod 755 "$APP_DIR/FastTranslation.Server"

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
# 已经配好（没有占位符）的 Caddyfile 不能被升级覆盖：拷贝过来会带回占位符，
# 下面那段检测就会把 caddy 停掉，网站跟着下线。要重新铺模板用
# FT_FORCE_CADDYFILE=1 bash install.sh。
if [ -f /etc/caddy/Caddyfile ] && ! grep -q '__DOMAIN__\|__ACME_EMAIL__' /etc/caddy/Caddyfile \
	&& [ "${FT_FORCE_CADDYFILE:-0}" != 1 ]; then
	echo "    keeping the configured /etc/caddy/Caddyfile (FT_FORCE_CADDYFILE=1 to overwrite)"
else
	install -m 0644 "$HERE/Caddyfile" /etc/caddy/Caddyfile
fi
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
