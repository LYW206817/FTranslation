# 本地词典

`ecdict.ftd` 是随客户端分发的只读词库，用于单英文词的词典卡。它不是手写数据，
由 `tools/dictionary/build-ecdict.py` 从 ECDICT 裁剪生成。

- 来源：https://github.com/skywind3000/ECDICT
- 许可：MIT（Copyright (c) 2025 Linwei），完整声明见仓库根目录 `THIRD_PARTY_NOTICES.md`
- 内容：约 5.8 万词，只保留考试大纲词（中考/高考/四六级/考研/托福/雅思/GRE）
  与有 BNC / 当代语料库词频的词条
- 字段：词头、音标、按词性分行的中文释义、词形变化、考试标签
- 体积：约 5.3 MB

## 重新生成

```bash
curl -L -o /tmp/ecdict.csv https://raw.githubusercontent.com/skywind3000/ECDICT/master/ecdict.csv
python tools/dictionary/build-ecdict.py --csv /tmp/ecdict.csv --out assets/dictionary/ecdict.ftd
```

原始 CSV（约 66 MB）不提交进仓库。换新版词库后需要重跑 `dotnet test`，
并重新拍一张单词卡截图核对排版。

## 文件格式（FTD1）

```
magic  4 bytes  "FTD1"
count  int32    词条数
index  count 条: int16 词头字节数 + 词头 UTF-8 + int32 数据偏移 + int32 数据长度
data   每条: word \t phonetic \t translation \t exchange \t tag   （UTF-8）
```

索引在启动时全部读进内存，查询按偏移读一条。改格式时同步改 `EcdictLexicon`
与 `tools/dictionary/build-ecdict.py`，并保留 `EcdictLexiconTests` 里的最小词库写入器。

## 已知边界

- 只覆盖英→中；中→英单词不会命中，继续走在线翻译。
- 没有例句；例句如需显示，应走独立的在线生成，不要混进词典数据。
- 音标来自 ECDICT 的老格式，读取侧会把 `ә` / `ǝ` 归一化成 `ə`。
