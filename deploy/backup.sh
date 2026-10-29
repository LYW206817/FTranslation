#!/usr/bin/env bash
# 给 SQLite 数据库做快照。服务端跑在 WAL 模式下，直接 cp 可能
# 拷到撕裂的页；".backup" 能在写入方继续工作的同时取一份一致的副本。
set -euo pipefail

DB=${FT_DB:-/var/lib/fasttranslation/fasttranslation.db}
DEST=${FT_BACKUP_DIR:-/var/backups/fasttranslation}
KEEP_DAYS=${FT_BACKUP_KEEP_DAYS:-14}

stamp=$(date -u +%Y%m%dT%H%M%SZ)
mkdir -p "$DEST"

sqlite3 "$DB" ".backup '$DEST/fasttranslation-$stamp.db'"
gzip -9 "$DEST/fasttranslation-$stamp.db"

find "$DEST" -name 'fasttranslation-*.db.gz' -type f -mtime +"$KEEP_DAYS" -delete

echo "backup written: $DEST/fasttranslation-$stamp.db.gz ($(du -h "$DEST/fasttranslation-$stamp.db.gz" | cut -f1))"
