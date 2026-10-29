#!/usr/bin/env bash
# Snapshot the SQLite database. The server runs in WAL mode, so a plain cp can
# capture a torn page; ".backup" takes a consistent copy while writers keep going.
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
