#!/usr/bin/env bash
# 飞书群机器人发送与告警去重。由 ft-probe.sh / ft-monitor.sh source，
# 不单独执行。依赖 curl 与 jq；真实 webhook 只放在
# /etc/fasttranslation/monitor.env（600），不进仓库。
set -euo pipefail

FT_ALERT_KEYWORD=${FT_ALERT_KEYWORD:-FastTranslation}
FT_STATE_DIR=${FT_STATE_DIR:-/var/lib/fasttranslation-monitor}
FT_REPEAT_SECONDS=${FT_REPEAT_SECONDS:-21600}

ft_send_feishu() {
	local text=$1
	# 飞书自定义机器人若开了「自定义关键词」校验，消息正文必须包含该词。
	case "$text" in
		*"$FT_ALERT_KEYWORD"*) ;;
		*) text="$FT_ALERT_KEYWORD $text" ;;
	esac

	if [ "${FT_DRY_RUN:-0}" = "1" ]; then
		printf '[dry-run] %s\n' "$text"
		return 0
	fi

	command -v curl >/dev/null 2>&1 || { echo "curl is required" >&2; return 1; }
	command -v jq >/dev/null 2>&1 || { echo "jq is required" >&2; return 1; }
	[ -n "${FT_FEISHU_WEBHOOK:-}" ] || { echo "FT_FEISHU_WEBHOOK is not set" >&2; return 1; }

	local body_file http_code
	body_file=$(mktemp)
	# 飞书关键词校验失败时仍返回 HTTP 200，所以必须读 body 里的 code。
	http_code=$(curl -sS --max-time 10 -o "$body_file" -w '%{http_code}' \
		-X POST "$FT_FEISHU_WEBHOOK" \
		-H 'Content-Type: application/json' \
		-d "$(jq -n --arg t "$text" '{msg_type:"text",content:{text:$t}}')") || http_code=000
	if [ "$http_code" != "200" ] || ! jq -e '.code == 0' "$body_file" >/dev/null 2>&1; then
		echo "feishu send failed: http=$http_code body=$(head -c 200 "$body_file" 2>/dev/null)" >&2
		rm -f "$body_file"
		return 1
	fi
	rm -f "$body_file"
}

# 同一 key 持续失败时只在进入失败态和每 FT_REPEAT_SECONDS 提醒；
# 恢复时补一条。状态文件用 tmp+mv，避免 cron 重叠时读到半行。
ft_alert_once() {
	local key=$1 state=$2 text=$3
	mkdir -p "$FT_STATE_DIR"
	local state_file="$FT_STATE_DIR/$key.state"
	local last_state="" last_alert=0 now
	now=$(date -u +%s)
	if [ -f "$state_file" ]; then
		IFS='|' read -r last_state last_alert < "$state_file" || true
	fi
	last_alert=${last_alert:-0}
	case "$last_alert" in
		''|*[!0-9]*) last_alert=0 ;;
	esac

	if [ "$state" = "ok" ]; then
		# 没有历史状态时第一次成功不发「恢复」，避免每次部署都来一条噪音。
		if [ "$last_state" = "fail" ]; then
			ft_send_feishu "$text" || return 1
		fi
		printf 'ok|%s\n' "$now" > "$state_file.tmp"
		mv "$state_file.tmp" "$state_file"
		return 0
	fi

	if [ "$last_state" = "fail" ] && [ $((now - last_alert)) -lt "$FT_REPEAT_SECONDS" ]; then
		return 0
	fi
	# 发送失败时不写状态，下次 cron 会重试；否则一个坏 webhook 会静默 6 小时。
	ft_send_feishu "$text" || return 1
	printf 'fail|%s\n' "$now" > "$state_file.tmp"
	mv "$state_file.tmp" "$state_file"
}
