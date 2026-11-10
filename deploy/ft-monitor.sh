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

command -v curl >/dev/null 2>&1 || { echo "curl is required" >&2; exit 1; }
command -v jq >/dev/null 2>&1 || { echo "jq is required" >&2; exit 1; }
command -v sqlite3 >/dev/null 2>&1 || { echo "sqlite3 is required" >&2; exit 1; }

HERE=$(cd "$(dirname "$0")" && pwd)
. "$HERE/ft-alert.sh" 2>/dev/null || . /usr/local/lib/ft-alert.sh

log() { printf '%s ft-monitor[%s]: %s\n' "$(date -u +%FT%TZ)" "$MODE" "$*"; }
sql() { sqlite3 -readonly "$FT_DB" "PRAGMA query_only=ON; $1"; }
yuan() { awk -v m="${1:-0}" 'BEGIN { printf "%.2f", m / 1000000 }'; }

budget_micros() {
	local value
	value=$(jq -r '.Server.Gateway.DailyBudgetMicros // 0' "$FT_CONFIG" 2>/dev/null || echo 0)
	case "$value" in
		''|*[!0-9]*) echo 0 ;;
		*) echo "$value" ;;
	esac
}

[ -f "$FT_DB" ] || { log "database not found: $FT_DB"; exit 1; }

check_resources() {
	local now latest age disk budget used percent
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

	disk=$(df -P /var/lib/fasttranslation 2>/dev/null | awk 'NR==2 { gsub("%","",$5); print $5 }')
	if [ -n "$disk" ] && [ "$disk" -gt "$FT_DISK_MAX_PERCENT" ]; then
		ft_alert_once disk fail "[FastTranslation] 磁盘检查：/var/lib/fasttranslation 已用 ${disk}%（阈值 ${FT_DISK_MAX_PERCENT}%）" || true
	else
		ft_alert_once disk ok "[FastTranslation] 磁盘恢复：已用 ${disk:-?}%" || true
	fi

	budget=$(budget_micros)
	if [ "$budget" -gt 0 ]; then
		used=$(sql "SELECT COALESCE(SUM(cost_micros),0) FROM usage_events WHERE created_at >= strftime('%s','now','start of day');")
		percent=$(( used * 100 / budget ))
		if [ "$percent" -ge "$FT_BUDGET_ALERT_PERCENT" ]; then
			ft_alert_once budget fail "[FastTranslation] 预算检查：今日已用 $(yuan "$used") 元（${percent}%），预算 $(yuan "$budget") 元" || true
		else
			ft_alert_once budget ok "[FastTranslation] 预算恢复：今日已用 $(yuan "$used") 元（${percent}%）" || true
		fi
	fi
}

digest() {
	local midnight yesterday new_users active rows chars cost today_cost budget oldest by_source text
	midnight=$(sql "SELECT strftime('%s','now','start of day');")
	yesterday=$(sql "SELECT strftime('%s','now','start of day','-1 day');")
	new_users=$(sql "SELECT COUNT(*) FROM users WHERE created_at >= $yesterday AND created_at < $midnight;")
	active=$(sql "SELECT COUNT(DISTINCT user_id) FROM usage_events WHERE created_at >= strftime('%s','now','-30 days');")
	rows=$(sql "SELECT COUNT(*) FROM usage_events WHERE created_at >= $yesterday AND created_at < $midnight;")
	chars=$(sql "SELECT COALESCE(SUM(chars),0) FROM usage_events WHERE created_at >= $yesterday AND created_at < $midnight;")
	cost=$(sql "SELECT COALESCE(SUM(cost_micros),0) FROM usage_events WHERE created_at >= $yesterday AND created_at < $midnight;")
	today_cost=$(sql "SELECT COALESCE(SUM(cost_micros),0) FROM usage_events WHERE created_at >= $midnight;")
	budget=$(budget_micros)
	oldest=$(sql "SELECT COALESCE(strftime('%Y-%m', MIN(created_at), 'unixepoch'), '-') FROM usage_events;" | head -1)
	by_source=$(sql "SELECT source || ': ' || COUNT(*) || ' 次 / ' || COALESCE(SUM(chars),0) || ' 字符 / ' || printf('%.2f', COALESCE(SUM(cost_micros),0)/1000000.0) || ' 元' FROM usage_events WHERE created_at >= $yesterday AND created_at < $midnight GROUP BY source;")

	text=$(printf '[FastTranslation] 每日摘要 %s (UTC)\n新增注册: %s\n30 天活跃: %s\n昨日请求: %s\n昨日字符: %s\n昨日成本: %s 元\n今日成本: %s 元 / 预算 %s 元\n最早 usage_events 月份: %s\n%s' \
		"$(date -u +%F)" "$new_users" "$active" "$rows" "$chars" \
		"$(yuan "$cost")" "$(yuan "$today_cost")" "$(yuan "$budget")" "$oldest" "${by_source:-（昨日无请求）}")
	ft_send_feishu "$text"
	log "digest sent"
}

case "$MODE" in
	check) check_resources ;;
	digest) digest ;;
	*) echo "usage: $0 check|digest" >&2; exit 2 ;;
esac
