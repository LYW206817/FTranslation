#!/usr/bin/env bash
# 给 SQLite 数据库做快照。服务端跑在 WAL 模式下，直接 cp 可能
# 拷到撕裂的页；".backup" 能在写入方继续工作的同时取一份一致的副本。
#
# 默认只在本机留 14 天。把 /etc/fasttranslation/backup.env 里的
# FT_BACKUP_OFFSITE 改成 1，就会再把 age 加密后的副本传到阿里云 OSS；
# 私钥不放在服务器上，OSS 上只有密文。
set -euo pipefail

DB=${FT_DB:-/var/lib/fasttranslation/fasttranslation.db}
DEST=${FT_BACKUP_DIR:-/var/backups/fasttranslation}
KEEP_DAYS=${FT_BACKUP_KEEP_DAYS:-14}

# 单独放一个 600 的 env 文件：OSS AccessKey 不能出现在命令行或仓库里。
ENV_FILE=${FT_BACKUP_ENV_FILE:-/etc/fasttranslation/backup.env}
if [ -f "$ENV_FILE" ]; then
	if [ "$(stat -c %a "$ENV_FILE" 2>/dev/null || true)" != "600" ]; then
		echo "warning: $ENV_FILE should be mode 600 (it holds OSS credentials)" >&2
	fi
	# shellcheck disable=SC1090
	. "$ENV_FILE"
fi
FT_BACKUP_OFFSITE=${FT_BACKUP_OFFSITE:-0}
FT_OSS_PREFIX=${FT_OSS_PREFIX:-fasttranslation}

# 备份里是全部账号与笔记，默认权限只给 root（目录 700、文件 600）；
# 不设 umask 时 sqlite3/gzip 会按 644 落盘，同机其他用户可读。
umask 077

require_offsite_config() {
	local missing=0 name
	for name in FT_BACKUP_AGE_RECIPIENT FT_OSS_BUCKET FT_OSS_ENDPOINT FT_OSS_ACCESS_KEY_ID FT_OSS_ACCESS_KEY_SECRET; do
		if [ -z "${!name:-}" ]; then
			echo "offsite backup is enabled but $name is empty in $ENV_FILE" >&2
			missing=1
		fi
	done
	[ "$missing" = 0 ] || return 1

	for tool in age openssl curl; do
		if ! command -v "$tool" >/dev/null 2>&1; then
			echo "offsite backup needs '$tool' (install age with: apt-get install -y age)" >&2
			return 1
		fi
	done

	case "$FT_BACKUP_AGE_RECIPIENT" in
		age1*) ;;
		*) echo "FT_BACKUP_AGE_RECIPIENT does not look like an age public key (age1...)" >&2; return 1 ;;
	esac
	case "$FT_OSS_BUCKET" in
		'' | *[!a-z0-9-]*) echo "FT_OSS_BUCKET must be a lowercase OSS bucket name" >&2; return 1 ;;
	esac
	case "$FT_OSS_ENDPOINT" in
		'' | *[!a-z0-9.-]*) echo "FT_OSS_ENDPOINT must look like oss-cn-hangzhou.aliyuncs.com" >&2; return 1 ;;
	esac
	case "$FT_OSS_PREFIX" in
		/* | *[!A-Za-z0-9/_-]*) echo "FT_OSS_PREFIX must be relative and contain only letters, digits, '/', '_' or '-'" >&2; return 1 ;;
	esac
}

# OSS Signature V1：PUT 空 Content-MD5、固定 Content-Type，StringToSign 的
# 资源段是 /<bucket>/<key>。只上传 age 密文；签名与密钥都不进日志。
offsite_upload() {
	local plain=$1 stamp=$2
	local encrypted="$plain.age"
	local key="${FT_OSS_PREFIX%/}/$stamp.db.gz.age"

	echo "offsite: encrypting with age"
	if ! age --encrypt --recipient "$FT_BACKUP_AGE_RECIPIENT" --output "$encrypted" "$plain"; then
		echo "age encryption failed" >&2
		rm -f "$encrypted"
		return 1
	fi

	if [ "${FT_BACKUP_OFFSITE_DRY_RUN:-0}" = "1" ]; then
		echo "[dry-run] would PUT https://$FT_OSS_BUCKET.$FT_OSS_ENDPOINT/$key ($(du -h "$encrypted" | cut -f1))"
		rm -f "$encrypted"
		return 0
	fi

	local date string signature
	date=$(LC_ALL=C date -u '+%a, %d %b %Y %H:%M:%S GMT')
	string=$(printf 'PUT\n\napplication/octet-stream\n%s\n/%s/%s' "$date" "$FT_OSS_BUCKET" "$key")
	signature=$(printf '%s' "$string" | openssl dgst -sha1 -hmac "$FT_OSS_ACCESS_KEY_SECRET" -binary | openssl base64 -A)

	echo "offsite: uploading $key"
	if ! curl -fsS --retry 3 --retry-delay 2 --max-time 120 -X PUT \
		-H "Date: $date" \
		-H "Content-Type: application/octet-stream" \
		-H "Authorization: OSS $FT_OSS_ACCESS_KEY_ID:$signature" \
		--upload-file "$encrypted" \
		"https://$FT_OSS_BUCKET.$FT_OSS_ENDPOINT/$key"; then
		echo "OSS upload failed: oss://$FT_OSS_BUCKET/$key" >&2
		rm -f "$encrypted"
		return 1
	fi

	rm -f "$encrypted"
	echo "offsite backup uploaded: oss://$FT_OSS_BUCKET/$key"
}

# sqlite3 对不存在的路径会新建一个空库再「成功」备份，等于静默丢掉真数据。
[ -f "$DB" ] || { echo "database not found: $DB" >&2; exit 1; }

if [ "$FT_BACKUP_OFFSITE" = "1" ]; then
	require_offsite_config || exit 1
fi

stamp=$(date -u +%Y%m%dT%H%M%SZ)
mkdir -p "$DEST"
chmod 700 "$DEST"
snapshot="$DEST/fasttranslation-$stamp.db"

sqlite3 "$DB" ".backup '$snapshot'"

# .backup 成功不等于副本可读：磁盘坏页或并发异常都可能留下半份文件。
# 压缩前先让 SQLite 自己走一遍完整性检查，坏了就删掉并以非零退出，让 cron/监控看见。
integrity=$(sqlite3 "$snapshot" 'PRAGMA integrity_check;')
if [ "$integrity" != "ok" ]; then
	echo "backup failed integrity_check: $integrity" >&2
	rm -f "$snapshot"
	exit 1
fi

gzip -9 "$snapshot"
archive="$DEST/fasttranslation-$stamp.db.gz"

offsite_ok=1
if [ "$FT_BACKUP_OFFSITE" = "1" ]; then
	if ! offsite_upload "$archive" "$stamp"; then
		offsite_ok=0
	fi
fi

find "$DEST" -name 'fasttranslation-*.db.gz' -type f -mtime +"$KEEP_DAYS" -delete

echo "backup written: $archive ($(du -h "$archive" | cut -f1))"
if [ "$offsite_ok" != "1" ]; then
	echo "local backup kept, but the offsite copy failed" >&2
	exit 1
fi