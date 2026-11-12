# Fast Translation 实施日志(IMPL)· 索引

> 每任务:变更文件 / 命令 / 构建与测试输出 / 未验证项。按 PLAN.md 执行。

实现记录按变更序号 `vN` 累加(与产品版本号 1.x.x 是两套编号)。单文件到 2026-11-03 已经涨到
2,847 行,读一次就吃掉大量上下文,所以**按每 20 个版本一册**拆到 `docs/impl/`,本文件只留索引和写入规则。

| 分册 | 覆盖 | 行数 |
|---|---|---|
| [impl/IMPL-v1-20.md](impl/IMPL-v1-20.md) | 编号之前的施工记录(T1–T16、真实 Key 联调、环境变更、UX 改造)+ v1–v20 | 302 |
| [impl/IMPL-v21-40.md](impl/IMPL-v21-40.md) | v21–v40 | 320 |
| [impl/IMPL-v41-60.md](impl/IMPL-v41-60.md) | v41–v60 | 772 |
| [impl/IMPL-v61-80.md](impl/IMPL-v61-80.md) | v61–v80 | 851 |
| [impl/IMPL-v81-100.md](impl/IMPL-v81-100.md) | v81–v100 | 782 |
| [impl/IMPL-v101-120.md](impl/IMPL-v101-120.md) | v101–v120 | 821 |
| [impl/IMPL-v121-140.md](impl/IMPL-v121-140.md) | v121–v140（当前最新一册） | 507 |

## 怎么找东西

- 看「最近改了什么」:打开最新一册的文末,现在是 [impl/IMPL-v121-140.md](impl/IMPL-v121-140.md)。
- 按版本号定位:`grep -rn '^## v78' docs/impl/`。
- 按关键词找坑:`grep -rn '剪贴板\|UIA\|DPI' docs/impl/`,命中后再读那一节。

## 新条目写到哪一册

1. 落在编号区间包含新 `vN` 的那一册——现在 v121 写进 `impl/IMPL-v121-140.md`；v141 起新开 `impl/IMPL-v141-160.md`。
2. 一册写满 20 个版本就新开下一册(下一个是 `IMPL-v141-160.md`),并在上面的表里加一行。
3. 追加中文的踩坑照旧:若 bash heredoc 带中文报错,先用写文件工具落到 `publish/*.md`,再 `cat >> docs/impl/IMPL-v121-140.md`。
