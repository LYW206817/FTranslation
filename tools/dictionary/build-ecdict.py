#!/usr/bin/env python3
"""把 ECDICT 的 CSV 裁剪成客户端词库文件（FTD1）。

为什么不用 SQLite：桌面端只用一次单条查询，为它带一个原生 SQLite 依赖不值得。
FTD1 是一个只读、按词哈希索引的简单格式——启动时读一遍索引（约 5 万条），
查询时按偏移读几百字节，实测单次 0.3 ms 量级。

数据来源：https://github.com/skywind3000/ECDICT （MIT License，Copyright (c) 2025 Linwei）。
改词库时保留上游的 LICENSE 与署名，见 assets/dictionary/README.md。

用法：
    python tools/dictionary/build-ecdict.py --csv path/to/ecdict.csv \
        --out assets/dictionary/ecdict.ftd
"""

import argparse
import csv
import struct
import sys
from pathlib import Path

MAGIC = b"FTD1"
# 只保留考试大纲词与有语料库词频的词：覆盖四六级/考研/雅思托福/GRE 与日常高频，
# 全量 77 万条对划词场景没有意义，体积会从 7 MB 涨到几十 MB。
EXAM_TAGS = {"zk", "gk", "cet4", "cet6", "ky", "toefl", "ielts", "gre"}


def should_keep(row: dict) -> bool:
    if EXAM_TAGS & set((row.get("tag") or "").split()):
        return True
    for key in ("frq", "bnc"):
        try:
            if int(row.get(key) or 0) > 0:
                return True
        except ValueError:
            return False
    return False


def clean(value: str | None, limit: int) -> str:
    # 字段用制表符分隔，读到换行没问题（长度前缀已经给定了边界），
    # 但制表符会破坏分列，所以替换成空格；超长释义截断，避免单条撑爆卡片。
    # ECDICT 的 CSV 里用字面量 "\n" 分行，这里还原成真换行，读取侧才好按行拆词性。
    text = (value or "").replace("\t", " ").replace("\\n", "\n").strip()
    return text[:limit]


def build(csv_path: Path, out_path: Path) -> None:
    entries: dict[str, tuple[str, str, str, str, str]] = {}
    total = 0
    with csv_path.open(encoding="utf-8", newline="") as fh:
        for row in csv.DictReader(fh):
            total += 1
            if not should_keep(row):
                continue
            word = (row.get("word") or "").strip()
            if not word or len(word.encode("utf-8")) > 120:
                continue
            # 去重用小写键，但存原始拼写：China / iPhone 这类词条不能在卡片上变成小写。
            key = word.casefold()
            if key in entries:
                continue
            entries[key] = (
                word,
                clean(row.get("phonetic"), 80),
                clean(row.get("translation"), 1200),
                clean(row.get("exchange"), 200),
                clean(row.get("tag"), 80),
            )

    ordered = sorted(entries.items(), key=lambda item: item[0])
    out_path.parent.mkdir(parents=True, exist_ok=True)

    with out_path.open("wb") as out:
        out.write(MAGIC)
        out.write(struct.pack("<i", len(ordered)))

        # 索引区先占位，数据区从 header + 索引之后开始。
        index_size = 0
        for key, _ in ordered:
            word_bytes = key.encode("utf-8")
            index_size += 2 + len(word_bytes) + 4 + 4
        data_start = 4 + 4 + index_size

        offset = data_start
        for key, (word, phonetic, translation, exchange, tag) in ordered:
            word_bytes = key.encode("utf-8")
            payload = "\t".join((word, phonetic, translation, exchange, tag)).encode("utf-8")
            out.write(struct.pack("<h", len(word_bytes)))
            out.write(word_bytes)
            out.write(struct.pack("<ii", offset, len(payload)))
            offset += len(payload)

        for _, fields in ordered:
            payload = "\t".join(fields).encode("utf-8")
            out.write(payload)

    size_mb = out_path.stat().st_size / 1024 / 1024
    print(f"scanned={total} kept={len(ordered)} out={out_path} size={size_mb:.1f} MB")


def main() -> int:
    parser = argparse.ArgumentParser(description="Build the FTD1 dictionary asset from ECDICT csv")
    parser.add_argument("--csv", required=True, type=Path)
    parser.add_argument("--out", required=True, type=Path)
    args = parser.parse_args()
    if not args.csv.exists():
        print(f"missing csv: {args.csv}", file=sys.stderr)
        return 1
    build(args.csv, args.out)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
