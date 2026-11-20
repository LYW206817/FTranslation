#!/usr/bin/env bash
# 资源检查与每日摘要。SQLite 以只读方式打开，WAL 下不会阻塞服务端写入。
# 用法：ft-monitor.sh check（每小时） / ft-monitor.sh digest（每天 09:00 CST）。
set -euo pipefail

MODE=${1:-check}
ENV_FILE=${FT_MONITOR_ENV:-/etc/fasttranslation/monitor.env}
if [ -f "$ENV_FILE" ]; then
	set -a
	# shellcheck disable=SC1090
	. "$ENV_FILE"
	set +a
fi

FT_DB=${FT_DB:-/var/lib/fasttranslation/fasttranslation.db}
FT_CONFIG=${FT_CONFIG:-/opt/fasttranslation/appsettings.Production.json}
FT_BACKUP_DIR=${FT_BACKUP_DIR:-/var/backups/fasttranslation}
FT_BACKUP_MAX_AGE_HOURS=${FT_BACKUP_MAX_AGE_HOURS:-36}
FT_DISK_MAX_PERCENT=${FT_DISK_MAX_PERCENT:-85}
FT_BUDGET_ALERT_PERCENT=${FT_BUDGET_ALERT_PERCENT:-80}
FT_STATE_DIR=${FT_STATE_DIR:-/var/lib/fasttranslation-monitor}
# 只从 backup.env 读两个非敏感项；不要把整份含 OSS 密钥的文件 source 进监控进程。
FT_BACKUP_ENV_FILE=${FT_BACKUP_ENV_FILE:-/etc/fasttranslation/backup.env}

read_env_value() {
	local key=$1 file=$2 value
	[ -f "$file" ] || return 1
	value=$(sed -n "s/^[[:space:]]*${key}=//p" "$file" | tail -1)
	value=${value%$'\r'}
	case "$value" in
		\"*\"|\'*\') value=${value:1:${#value}-2} ;;
	esac
	printf '%s' "$value"
}

offsite_enabled=${FT_BACKUP_OFFSITE:-}
[ -n "$offsite_enabled" ] || offsite_enabled=$(read_env_value FT_BACKUP_OFFSITE "$FT_BACKUP_ENV_FILE" || true)
offsite_enabled=${offsite_enabled:-0}
offsite_marker=${FT_BACKUP_OFFSITE_MARKER:-}
[ -n "$offsite_marker" ] || offsite_marker=$(read_env_value FT_BACKUP_OFFSITE_MARKER "$FT_BACKUP_ENV_FILE" || true)
offsite_marker=${offsite_marker:-$FT_BACKUP_DIR/.offsite-ok}

command -v curl >/dev/null 2>&1 || { echo "curl is required" >&2; exit 1; }
command -v jq >/dev/null 2>&1 || { echo "jq is required" >&2; exit 1; }
command -v sqlite3 >/dev/null 2>&1 || { echo "sqlite3 is required" >&2; exit 1; }

HERE=$(cd "$(dirname "$0")" && pwd)
. "$HERE/ft-alert.sh" 2>/dev/null || . /usr/local/lib/ft-alert.sh

log() { printf '%s ft-monitor[%s]: %s\n' "$(date -u +%FT%TZ)" "$MODE" "$*"; }
sql() { sqlite3 -readonly "$FT_DB" "PRAGMA query_only=ON; $1"; }
yuan() { awk -v m="${1:-0}" 'BEGIN { printf "%.2f", m / 1000000 }'; }

# 读一个预算键；键不存在或值非法都返回 missing——不能当成 0（0 是「刻意不设上限」），
# 否则生产配置漏了键时监控会静默跳过这一桶，而网关那边仍在按默认值熔断。
config_micros() {
	local value
	value=$(jq -r "(${1}) // \"missing\"" "$FT_CONFIG" 2>/dev/null || echo missing)
	case "$value" in
		''|*[!0-9]*) echo missing ;;
		*) echo "$value" ;;
	esac
}

# 预算按桶比对（免费 / 会员 / 语音，见网关 GatewayBuckets）：把全站成本塞进免费桶的
# 上限，会在会员或语音花钱时提前告警，而真正烧穿的那一桶反而看不出来。
# 预算 0 表示该桶不熔断；此时补一条 ok，清掉之前遗留的失败告警。
check_budget() {
	local key=$1 label=$2 source=$3 config_path=$4
	local budget used percent
	budget=$(config_micros "$config_path")
	if [ "$budget" = "missing" ]; then
		ft_alert_once "$key" fail "[FastTranslation] 预算检查（${label}）：配置缺少 ${config_path#.Server.Gateway.}，这一桶无法比对" || true
		return 0
	fi
	if [ "$budget" -le 0 ]; then
		ft_alert_once "$key" ok "[FastTranslation] 预算检查（${label}）：未设上限" || true
		return 0
	fi
	used=$(sql "SELECT COALESCE(SUM(cost_micros),0) FROM usage_events WHERE source = '$source' AND created_at >= strftime('%s','now','start of day');")
	percent=$(( used * 100 / budget ))
	if [ "$percent" -ge "$FT_BUDGET_ALERT_PERCENT" ]; then
		ft_alert_once "$key" fail "[FastTranslation] 预算检查（${label}）：今日已用 $(yuan "$used") 元（${percent}%），预算 $(yuan "$budget") 元" || true
	else
		ft_alert_once "$key" ok "[FastTranslation] 预算恢复（${label}）：今日已用 $(yuan "$used") 元（${percent}%）" || true
	fi
}

# 每日摘要用的一行「今日成本 / 上限」。
bucket_today() {
	local source=$1 config_path=$2 used budget
	used=$(sql "SELECT COALESCE(SUM(cost_micros),0) FROM usage_events WHERE source = '$source' AND created_at >= strftime('%s','now','start of day');")
	budget=$(config_micros "$config_path")
	case "$budget" in
		missing) printf '%s 元（未配置上限）' "$(yuan "$used")" ;;
		0) printf '%s 元（不限）' "$(yuan "$used")" ;;
		*) printf '%s / %s 元' "$(yuan "$used")" "$(yuan "$budget")" ;;
	esac
}

[ -f "$FT_DB" ] || { log "database not found: $FT_DB"; exit 1; }

check_resources() {
	local now latest age disk
	now=$(date -u +%s)

	if [ -d "$FT_BACKUP_DIR" ]; then
		latest=$(find "$FT_BACKUP_DIR" -maxdepth 1 -name 'fasttranslation-*.db.gz' -type f \
			-printf '%T@\n' 2>/dev/null | sort -nr | head -1)
		if [ -z "$latest" ]; then
			ft_alert_once backup fail "[FastTranslation] 备份检查：$FT_BACKUP_DIR 里没有 .db.gz" || true
		else
			latest=${latest%.*}
			age=$(( (now - latest) / 3600 ))
			if [ "$age" -gt "$FT_BACKUP_MAX_AGE_HOURS" ]; then
				ft_alert_once backup fail "[FastTranslation] 备份检查：最新备份已 ${age}h（阈值 ${FT_BACKUP_MAX_AGE_HOURS}h）" || true
			else
				ft_alert_once backup ok "[FastTranslation] 备份恢复：最新备份 ${age}h" || true
			fi
		fi
	else
		ft_alert_once backup fail "[FastTranslation] 备份检查：目录 $FT_BACKUP_DIR 不存在" || true
	fi

	if [ "$offsite_enabled" != "1" ]; then
		# 从启用改回关闭时也写一次 ok，清掉历史上遗留的异地失败告警。
		ft_alert_once offsite ok "[FastTranslation] 异地备份未启用" || true
	elif [ -f "$offsite_marker" ]; then
		latest=$(stat -c %Y "$offsite_marker" 2>/dev/null || true)
		latest=${latest:-0}
		age=$(( (now - latest) / 3600 ))
		if [ "$age" -gt "$FT_BACKUP_MAX_AGE_HOURS" ]; then
			ft_alert_once offsite fail "[FastTranslation] 异地备份检查：最近成功上传已 ${age}h（阈值 ${FT_BACKUP_MAX_AGE_HOURS}h）" || true
		else
			ft_alert_once offsite ok "[FastTranslation] 异地备份恢复：最近成功上传 ${age}h" || true
		fi
	else
		ft_alert_once offsite fail "[FastTranslation] 异地备份检查：从未成功上传（缺少 $offsite_marker）" || true
	fi

	disk=$(df -P /var/lib/fasttranslation 2>/dev/null | awk 'NR==2 { gsub("%","",$5); print $5 }')
	if [ -n "$disk" ] && [ "$disk" -gt "$FT_DISK_MAX_PERCENT" ]; then
		ft_alert_once disk fail "[FastTranslation] 磁盘检查：/var/lib/fasttranslation 已用 ${disk}%（阈值 ${FT_DISK_MAX_PERCENT}%）" || true
	else
		ft_alert_once disk ok "[FastTranslation] 磁盘恢复：已用 ${disk:-?}%" || true
	fi

	# 免费桶沿用旧 key（budget），部署前的失败状态不会因为改名被丢掉。
	check_budget budget 免费 gateway '.Server.Gateway.DailyBudgetMicros'
	check_budget budget_paid 会员 gateway_paid '.Server.Gateway.PaidDailyBudgetMicros'
	check_budget budget_speech 语音 speech '.Server.Gateway.Speech.DailyBudgetMicros'
}

digest() {
	local midnight yesterday new_users active rows chars cost today_free today_paid today_speech oldest by_source text
	midnight=$(sql "SELECT strftime('%s','now','start of day');")
	yesterday=$(sql "SELECT strftime('%s','now','start of day','-1 day');")
	new_users=$(sql "SELECT COUNT(*) FROM users WHERE created_at >= $yesterday AND created_at < $midnight;")
	active=$(sql "SELECT COUNT(DISTINCT user_id) FROM usage_events WHERE created_at >= strftime('%s','now','-30 days');")
	rows=$(sql "SELECT COUNT(*) FROM usage_events WHERE created_at >= $yesterday AND created_at < $midnight;")
	chars=$(sql "SELECT COALESCE(SUM(chars),0) FROM usage_events WHERE created_at >= $yesterday AND created_at < $midnight;")
	cost=$(sql "SELECT COALESCE(SUM(cost_micros),0) FROM usage_events WHERE created_at >= $yesterday AND created_at < $midnight;")
	today_free=$(bucket_today gateway '.Server.Gateway.DailyBudgetMicros')
	today_paid=$(bucket_today gateway_paid '.Server.Gateway.PaidDailyBudgetMicros')
	today_speech=$(bucket_today speech '.Server.Gateway.Speech.DailyBudgetMicros')
	oldest=$(sql "SELECT COALESCE(strftime('%Y-%m', MIN(created_at), 'unixepoch'), '-') FROM usage_events;" | head -1)
	by_source=$(sql "SELECT source || ': ' || COUNT(*) || ' 次 / ' || COALESCE(SUM(chars),0) || ' 字符 / ' || printf('%.2f', COALESCE(SUM(cost_micros),0)/1000000.0) || ' 元' FROM usage_events WHERE created_at >= $yesterday AND created_at < $midnight GROUP BY source;")

	text=$(printf '[FastTranslation] 每日摘要 %s (UTC)\n新增注册: %s\n30 天活跃: %s\n昨日请求: %s\n昨日字符: %s\n昨日成本: %s 元\n今日成本: 免费 %s · 会员 %s · 语音 %s\n最早 usage_events 月份: %s\n%s' \
		"$(date -u +%F)" "$new_users" "$active" "$rows" "$chars" \
		"$(yuan "$cost")" "$today_free" "$today_paid" "$today_speech" "$oldest" "${by_source:-（昨日无请求）}")
	ft_send_feishu "$text"
	log "digest sent"
}

case "$MODE" in
	check) check_resources ;;
	digest) digest ;;
	*) echo "usage: $0 check|digest" >&2; exit 2 ;;
esac
