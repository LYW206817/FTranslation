#!/usr/bin/env bash
# 给 SQLite 数据库做快照。服务端跑在 WAL 模式下，直接 cp 可能
# 拷到撕裂的页；".backup" 能在写入方继续工作的同时取一份一致的副本。
set -euo pipefail

DB=${FT_DB:-/var/lib/fasttranslation/fasttranslation.db}
DEST=${FT_BACKUP_DIR:-/var/backups/fasttranslation}
KEEP_DAYS=${FT_BACKUP_KEEP_DAYS:-14}

# 备份里是全部账号与笔记，默认权限只给 root（目录 700、文件 600）；
# 不设 umask 时 sqlite3/gzip 会按 644 落盘，同机其他用户可读。
umask 077

# sqlite3 对不存在的路径会新建一个空库再「成功」备份，等于静默丢掉真数据。
[ -f "$DB" ] || { echo "database not found: $DB" >&2; exit 1; }

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

find "$DEST" -name 'fasttranslation-*.db.gz' -type f -mtime +"$KEEP_DAYS" -delete

echo "backup written: $DEST/fasttranslation-$stamp.db.gz ($(du -h "$DEST/fasttranslation-$stamp.db.gz" | cut -f1))"
