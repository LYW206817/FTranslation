#!/usr/bin/env bash
# 一次性创建合成探测账号：注册 → 升 standard 10 年 → 验证登录。
# 读 /etc/fasttranslation/monitor.env 里的探针邮箱/密码，AdminToken 从生产配置读。
# 幂等：账号已存在时注册返回 409，脚本继续升档并验证登录。
set -euo pipefail

ENV_FILE=${FT_MONITOR_ENV:-/etc/fasttranslation/monitor.env}
[ -f "$ENV_FILE" ] || { echo "missing $ENV_FILE" >&2; exit 1; }
set -a
# shellcheck disable=SC1090
. "$ENV_FILE"
set +a

FT_BASE_URL=${FT_BASE_URL:-http://127.0.0.1:5080}
FT_BASE_URL=${FT_BASE_URL%/}
FT_CONFIG=${FT_CONFIG:-/opt/fasttranslation/appsettings.Production.json}
FT_PROBE_EMAIL=${FT_PROBE_EMAIL:-probe@example.com}
FT_PROBE_PASSWORD=${FT_PROBE_PASSWORD:-}

command -v curl >/dev/null 2>&1 || { echo "curl is required" >&2; exit 1; }
command -v jq >/dev/null 2>&1 || { echo "jq is required" >&2; exit 1; }
[ -n "$FT_PROBE_PASSWORD" ] || { echo "FT_PROBE_PASSWORD is empty in $ENV_FILE" >&2; exit 1; }

tmp=$(mktemp)
trap 'rm -f "$tmp"' EXIT

register_body=$(jq -n --arg e "$FT_PROBE_EMAIL" --arg p "$FT_PROBE_PASSWORD" \
	'{email:$e,password:$p,deviceFingerprint:"ft-synthetic-probe",deviceName:"synthetic-probe"}')
http_code=$(curl -sS --max-time 15 -o "$tmp" -w '%{http_code}' \
	-X POST "$FT_BASE_URL/api/v1/auth/register" \
	-H 'Content-Type: application/json' -d "$register_body") || http_code=000
case "$http_code" in
	200) echo "probe account registered: $FT_PROBE_EMAIL" ;;
	409) echo "probe account already exists; checking the plan and password" ;;
	*) echo "register failed (http $http_code): $(head -c 200 "$tmp")" >&2; exit 1 ;;
esac

admin=$(jq -r '.Server.AdminToken // empty' "$FT_CONFIG")
[ -n "$admin" ] || { echo "Server:AdminToken is missing in $FT_CONFIG" >&2; exit 1; }
http_code=$(curl -sS --max-time 15 -o "$tmp" -w '%{http_code}' \
	-X POST "$FT_BASE_URL/api/v1/admin/accounts/$FT_PROBE_EMAIL/plan" \
	-H "X-Admin-Token: $admin" -H 'Content-Type: application/json' \
	-d '{"plan":"standard","days":3650}') || http_code=000
if [ "$http_code" != "200" ]; then
	echo "plan update failed (http $http_code): $(head -c 200 "$tmp")" >&2
	exit 1
fi
echo "probe account is on standard for 3650 days"

# 最后验证密码真的能用；账号已存在但密码不对时，这里会明确报出来。
login_body=$(jq -n --arg e "$FT_PROBE_EMAIL" --arg p "$FT_PROBE_PASSWORD" \
	'{email:$e,password:$p,deviceFingerprint:"ft-synthetic-probe",deviceName:"synthetic-probe"}')
http_code=$(curl -sS --max-time 15 -o "$tmp" -w '%{http_code}' \
	-X POST "$FT_BASE_URL/api/v1/auth/login" \
	-H 'Content-Type: application/json' -d "$login_body") || http_code=000
if [ "$http_code" != "200" ]; then
	echo "probe login failed (http $http_code): $(head -c 200 "$tmp")" >&2
	echo "check FT_PROBE_PASSWORD, or remove the probe account and rerun this script" >&2
	exit 1
fi
echo "probe login ok"
