#!/usr/bin/env bash
# 备案号下来之后的一条命令上线。
#
# 做四件事,全部幂等,重复执行等于重新校验:
#   1. 把 /etc/caddy/Caddyfile 里的 __DOMAIN__ / __ACME_EMAIL__ 换成真实值
#   2. 把 /var/www/fasttranslation/index.html 页脚的 __ICP_NUMBER__ 换成备案号
#   3. caddy validate 后 enable --now caddy(第一次会向 Let's Encrypt 申请证书)
#   4. 五项断言:HTTP 跳 HTTPS、/health、证书主体与有效期、页面无占位符、页面含备案号
#
# 用法(服务器上,root):
#   bash go-live.sh --email you@example.com --icp '豫ICP备2026xxxxxx号-1'
#
# 可选:
#   --domain NAME     默认 ftranslation.cn
#   --expect-ip IP    断言域名解析到这台机器,建议填 ECS 公网 IP
#   --skip-icp        允许页脚暂时留占位符(境内站点必须展示,不建议)
#   --dry-run         只做只读诊断并打印计划,不改任何文件、不启 Caddy
#
# 备案没下来不要跑:域名指向大陆服务器且未备案会被拦,ACME 也签不下证书。
set -euo pipefail

DOMAIN=ftranslation.cn
ACME_EMAIL=
ICP_NUMBER=
EXPECT_IP=
DRY=0
SKIP_ICP=0

CADDYFILE=/etc/caddy/Caddyfile
WEB_DIR=/var/www/fasttranslation
SERVICE=fasttranslation

say() { printf '==> %s\n' "$*"; }
ok() { printf '    ok   %s\n' "$*"; }
warn() { printf '    !!   %s\n' "$*"; }
fail() { printf '    FAIL %s\n' "$*"; FAILED=$((FAILED + 1)); }
die() { printf '!! %s\n' "$*" >&2; exit 1; }
run() {
	if [ "$DRY" = 1 ]; then printf '    [dry-run] %s\n' "$*"; else "$@"; fi
}

FAILED=0

while [ $# -gt 0 ]; do
	case "$1" in
	--domain) DOMAIN=${2:?--domain needs a value}; shift 2 ;;
	--email) ACME_EMAIL=${2:?--email needs a value}; shift 2 ;;
	--icp) ICP_NUMBER=${2:?--icp needs a value}; shift 2 ;;
	--expect-ip) EXPECT_IP=${2:?--expect-ip needs a value}; shift 2 ;;
	--skip-icp) SKIP_ICP=1; shift ;;
	--dry-run) DRY=1; shift ;;
	-h | --help) sed -n '2,25p' "$0"; exit 0 ;;
	*) die "unknown argument: $1 (try --help)" ;;
	esac
done

[ "$(id -u)" = 0 ] || die "run as root: it rewrites /etc/caddy and restarts the unit"
case "$DOMAIN" in
	*.*) ;;
	*) die "--domain $DOMAIN does not look like a hostname" ;;
esac

say "configuration"
printf '    domain      %s\n    acme email  %s\n    icp number  %s\n    web dir     %s\n' \
	"$DOMAIN" "${ACME_EMAIL:-(not set)}" "${ICP_NUMBER:-(not set)}" "$WEB_DIR"

if [ ! -f "$CADDYFILE" ]; then
	die "$CADDYFILE is missing: run install.sh first"
fi
if [ ! -f "$WEB_DIR/index.html" ]; then
	die "$WEB_DIR/index.html is missing: run install.sh first"
fi

say "preflight: dns"
RESOLVED=$(getent ahostsv4 "$DOMAIN" 2>/dev/null | awk '{print $1}' | sort -u | tr '\n' ' ' || true)
if [ -z "${RESOLVED// /}" ]; then
	fail "$DOMAIN does not resolve yet — add the A record in 云解析 first (A @ -> ECS public IP)"
else
	ok "$DOMAIN -> $RESOLVED"
fi
if [ -n "$EXPECT_IP" ]; then
	case " $RESOLVED " in
		*" $EXPECT_IP "*) ok "resolves to the expected $EXPECT_IP" ;;
		*) fail "$DOMAIN resolves to '$RESOLVED', expected $EXPECT_IP — wrong record or stale cache" ;;
	esac
fi

say "preflight: ports"
if ss -ltnH '( sport = :443 or sport = :80 )' | grep -q .; then
	if systemctl is-active --quiet caddy; then
		ok "80/443 are held by the running caddy (its own socket)"
	else
		fail "80/443 already have a listener that is not caddy:"
		ss -ltnp '( sport = :443 or sport = :80 )' || true
	fi
else
	ok "80 and 443 are free"
fi

say "preflight: placeholders"
if grep -q '__DOMAIN__\|__ACME_EMAIL__' "$CADDYFILE"; then
	ok "$CADDYFILE still has placeholders, they will be replaced"
else
	ok "$CADDYFILE is already filled in"
	if ! grep -q -- "$DOMAIN" "$CADDYFILE"; then
		fail "$CADDYFILE is filled in, but not with $DOMAIN — fix it by hand"
	fi
fi
if grep -q '__ICP_NUMBER__' "$WEB_DIR/index.html"; then
	if [ -z "$ICP_NUMBER" ] && [ "$SKIP_ICP" = 0 ]; then
		fail "the footer still reads __ICP_NUMBER__; pass --icp '<备案号>' (or --skip-icp to leave it)"
	else
		ok "the footer will get $ICP_NUMBER"
	fi
else
	ok "the footer already carries a filing number"
fi

if [ "$FAILED" -gt 0 ]; then
	printf '\n!! %s preflight check(s) failed; nothing was changed.\n' "$FAILED"
	exit 1
fi

say "rewrite $CADDYFILE"
if grep -q '__DOMAIN__\|__ACME_EMAIL__' "$CADDYFILE"; then
	[ -n "$ACME_EMAIL" ] || die "--email is required while __ACME_EMAIL__ is still a placeholder"
	run cp -a "$CADDYFILE" "$CADDYFILE.bak"
	run sed -i "s/__DOMAIN__/$DOMAIN/g; s/__ACME_EMAIL__/$ACME_EMAIL/g" "$CADDYFILE"
else
	ok "already rewritten, nothing to do"
fi

say "rewrite the footer in $WEB_DIR/index.html"
if grep -q '__ICP_NUMBER__' "$WEB_DIR/index.html" && [ -n "$ICP_NUMBER" ]; then
	run cp -a "$WEB_DIR/index.html" "$WEB_DIR/index.html.bak"
	run sed -i "s/__ICP_NUMBER__/$ICP_NUMBER/g" "$WEB_DIR/index.html"
else
	ok "nothing to do"
fi

say "validate"
run caddy validate --config "$CADDYFILE"

say "start caddy"
if systemctl is-active --quiet caddy; then
	run systemctl reload-or-restart caddy
else
	run systemctl enable --now caddy
fi

if [ "$DRY" = 1 ]; then
	printf '\n==> dry run finished; re-run the same command without --dry-run to apply.\n'
	exit 0
fi

say "wait for the certificate (ACME HTTP-01, up to 90s)"
TRIES=0
while [ "$TRIES" -lt 45 ]; do
	if curl -fsS -o /dev/null --max-time 5 "https://$DOMAIN/health"; then
		ok "https is serving"
		break
	fi
	TRIES=$((TRIES + 1))
	sleep 2
done
if [ "$TRIES" -ge 45 ]; then
	warn "https did not answer within 90s; inspect: journalctl -u caddy -n 60 --no-pager"
fi

say "assertions"
CODE=$(curl -sS -o /dev/null -w '%{http_code}' --max-time 10 "http://$DOMAIN/" || true)
case "$CODE" in
	301 | 308) ok "http redirects with $CODE" ;;
	*) fail "http://$DOMAIN/ returned $CODE, expected 301/308" ;;
esac

HEALTH=$(curl -sS --max-time 10 "https://$DOMAIN/health" || true)
case "$HEALTH" in
	*'"ok"'*) ok "https://$DOMAIN/health -> $HEALTH" ;;
	*) fail "health endpoint returned '$HEALTH'" ;;
esac

if command -v openssl >/dev/null 2>&1; then
	SUBJECT=$(echo | openssl s_client -connect "$DOMAIN:443" -servername "$DOMAIN" 2>/dev/null \
		| openssl x509 -noout -subject -enddate 2>/dev/null | tr '\n' ' ' || true)
	if [ -n "$SUBJECT" ]; then ok "certificate: $SUBJECT"; else fail "could not read the certificate"; fi
fi

PAGE=$(curl -sS --max-time 10 "https://$DOMAIN/" || true)
case "$PAGE" in
	*'__ICP_NUMBER__'*) fail "the served page still shows __ICP_NUMBER__ (stale file or wrong web dir)" ;;
	*'beian.miit.gov.cn'*) ok "the served page links to beian.miit.gov.cn" ;;
	*) fail "the served page has no filing link" ;;
esac
if [ -n "$ICP_NUMBER" ]; then
	case "$PAGE" in
		*"$ICP_NUMBER"*) ok "the served page shows $ICP_NUMBER" ;;
		*) fail "the served page does not contain $ICP_NUMBER" ;;
	esac
fi

say "state"
systemctl is-enabled caddy || true
systemctl --no-pager --lines=3 status caddy | sed -n '1,3p' || true

if [ "$FAILED" -gt 0 ]; then
	printf '\n!! %s assertion(s) failed.\n' "$FAILED"
	printf '   rollback: cp -a %s.bak %s && cp -a %s/index.html.bak %s/index.html && systemctl restart caddy\n' \
		"$CADDYFILE" "$CADDYFILE" "$WEB_DIR" "$WEB_DIR"
	exit 1
fi
printf '\n==> live at https://%s\n' "$DOMAIN"
printf '    note: also replace __ICP_NUMBER__ in the repo copy (site/index.html) and commit it,\n'
printf '          otherwise the next install.sh restores the placeholder in the footer.\n'
