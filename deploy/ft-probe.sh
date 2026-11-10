#!/usr/bin/env bash
# 合成翻译探测：登录专用账号 → 走真实的 /v1/chat/completions → 断言 200 与译文。
# 与 /health 的区别：这个能抓到百炼 Key 失效、网关或记账坏掉，/health 只能证明
# 进程活着。探测走 loopback，不依赖域名、备案或 HTTPS 是否就绪。
set -euo pipefail

ENV_FILE=${FT_MONITOR_ENV:-/etc/fasttranslation/monitor.env}
if [ -f "$ENV_FILE" ]; then
	set -a
	# shellcheck disable=SC1090
	. "$ENV_FILE"
	set +a
fi

FT_BASE_URL=${FT_BASE_URL:-http://127.0.0.1:5080}
FT_BASE_URL=${FT_BASE_URL%/}
FT_PROBE_EMAIL=${FT_PROBE_EMAIL:-probe@example.com}
FT_PROBE_PASSWORD=${FT_PROBE_PASSWORD:-}
FT_STATE_DIR=${FT_STATE_DIR:-/var/lib/fasttranslation-monitor}

command -v curl >/dev/null 2>&1 || { echo "curl is required" >&2; exit 1; }
command -v jq >/dev/null 2>&1 || { echo "jq is required" >&2; exit 1; }
[ -n "$FT_PROBE_PASSWORD" ] || { echo "FT_PROBE_PASSWORD is empty; install $ENV_FILE" >&2; exit 1; }

HERE=$(cd "$(dirname "$0")" && pwd)
# 仓库里直接跑时用同目录的库；安装后从 /usr/local/lib 读。
. "$HERE/ft-alert.sh" 2>/dev/null || . /usr/local/lib/ft-alert.sh

log() { printf '%s ft-probe: %s\n' "$(date -u +%FT%TZ)" "$*"; }

tmp=$(mktemp)
trap 'rm -f "$tmp"' EXIT

fail() {
	local reason=$1
	log "$reason"
	ft_alert_once probe fail "[FastTranslation] 合成探测失败：$reason" || true
	exit 1
}

# 登录拿 access token。固定指纹保证每次复用同一个设备行，不触发设备上限。
login_body=$(jq -n --arg e "$FT_PROBE_EMAIL" --arg p "$FT_PROBE_PASSWORD" \
	'{email:$e,password:$p,deviceFingerprint:"ft-synthetic-probe",deviceName:"synthetic-probe"}')
http_code=$(curl -sS --max-time 15 -o "$tmp" -w '%{http_code}' \
	-X POST "$FT_BASE_URL/api/v1/auth/login" \
	-H 'Content-Type: application/json' -d "$login_body") || http_code=000
if [ "$http_code" != "200" ]; then
	fail "login failed (http $http_code): $(head -c 200 "$tmp" | tr '\n' ' ')"
fi

token=$(jq -r '.accessToken // empty' "$tmp")
[ -n "$token" ] || fail "login response has no accessToken"

# 固定一句短英文，成本可忽略；检查 choices[0].message.content 非空。
chat_body=$(jq -n '{model:"qwen-mt-flash",messages:[{role:"user",content:"The quick brown fox jumps over the lazy dog."}]}')
http_code=$(curl -sS --max-time 60 -o "$tmp" -w '%{http_code}' \
	-X POST "$FT_BASE_URL/v1/chat/completions" \
	-H "Authorization: Bearer $token" \
	-H 'X-FT-Device: ft-synthetic-probe' \
	-H 'Content-Type: application/json' -d "$chat_body") || http_code=000
if [ "$http_code" != "200" ]; then
	fail "translation failed (http $http_code): $(head -c 200 "$tmp" | tr '\n' ' ')"
fi

if ! jq -e '.choices[0].message.content | type == "string" and length > 0' "$tmp" >/dev/null 2>&1; then
	fail "translation response has no content: $(head -c 200 "$tmp" | tr '\n' ' ')"
fi

log "ok ($(jq -r '.choices[0].message.content' "$tmp" | head -c 80))"
ft_alert_once probe ok "[FastTranslation] 合成探测恢复：翻译链路正常" || true
