# Fast Translation — 账号体系 + 后端 + 上云 方案（评审用）

- 状态：**待决策**（本文只做方案，不含代码改动）
- 日期：2026-10-28（对应客户端 1.2.2 / git 头 `cfe2fc6`）
- 前置文档：`docs/superpowers/specs/2026-10-22-fast-translation-design.md`（v1.0 规格，本文与之冲突处已在 §7 列出）
- 一手价格来源：`https://help.aliyun.com/zh/model-studio/model-pricing`（2026-10-28 抓取）、`https://help.aliyun.com/zh/model-studio/machine-translation`（响应 usage 字段）

---

## 1. 结论摘要

用户诉求拆成 6 条：①客户端加账号 ②自建后端 ③买云服务器 ④能统计注册用户数与用户数 ⑤未来免费版/商用版双档 ⑥商用版用开发者的 Key，开发者与用户都要能看到消耗。

**一句话结论**：账号和后端必须做，但「商用版用你的 Key + 消耗可见」有两条互斥的实现路径，选哪条决定工作量是 3 周还是 6 周，也决定要不要备案、要不要阿里云书面确认。这两条路径之外的部分（账号、注册统计、设备绑定、用量展示）无争议，可以先做。

| 方案 | 计量准确度 | Key 是否暴露 | 合规门槛 | 工作量 | 建议 |
|---|---|---|---|---|---|
| **A. 客户端直连，Key 下发客户端** | 差（客户端自报，可篡改） | **会暴露** | 代付性质仍需阿里云确认 | 3 周 | 只适合过渡 |
| **B. 服务端代理网关（推荐）** | 准（网关读 `usage` 记账） | 不暴露 | 代付 + 需域名备案（可用香港免备案） | 6 周 | **推荐** |
| **C. 商用版仍 BYO（用户自己的 Key）** | 准（用户自己付费） | 不涉及 | 无 | 2 周 | 只想卖软件授权时选 |

推荐 B，理由与「什么会推翻它」见 §3。

---

## 2. 事实基线（已核实，不是估算）

### 2.1 Qwen-MT 官方单价（华北 2 / 北京，每百万 Token）

| 模型 ID | 输入 | 输出 | 免费额度 |
|---|---|---|---|
| `qwen-mt-flash`（客户端默认，`src/FastTranslation.Core/Settings.cs:9`） | 0.7 元 | 1.95 元 | 100 万 Token / 90 天 |
| `qwen-mt-plus` | 1.8 元 | 5.4 元 | 100 万 Token / 90 天 |
| `qwen-mt-lite` | 0.6 元 | 1.6 元 | 100 万 Token / 90 天 |

图片翻译（截图翻译链路 `qwen-mt-image-2.0`）：**0.004 元/张**，100 张免费；旧 `qwen-mt-image` 0.003 元/张。

### 2.2 计费口径（关键，直接决定成本模型）

- 按 **输入 Token + 输出 Token** 计费，响应体里回传 `usage`。官方文档实测样本：原文「我看到这个视频后没有笑」（17 字）→ `CompletionUsage(completion_tokens=9, prompt_tokens=56, total_tokens=65)`。
- ⇒ **每次请求有约 45 Token 的固定开销**（模型内部指令），与选中文本长度无关。划词多为短句，所以「按字符数估成本」会严重低估，必须按 Token 且有固定项。
- 当前客户端**完全不解析 `usage`**：`src/FastTranslation.Providers.QwenMt/QwenMtProvider.cs:131-147` 的 `ReadTranslationAsync` 只取 `choices[0].message.content`。这是「消耗可见」唯一的硬缺口。

### 2.3 现有可复用的机制（不要重造）

| 机制 | 位置 | 复用方式 |
|---|---|---|
| `OverrideBaseUrl` | `src/FastTranslation.Core/Settings.cs:20`、`src/FastTranslation.App/App.xaml.cs:88-90` | 指向自建网关，**provider 零改动** |
| OpenAI 兼容路径 | `QwenMtProvider.cs:107-111` `BuildUri()` = `BaseUrl.TrimEnd('/') + "/chat/completions"` | 网关实现同名路径即可透明替换 |
| Bearer 鉴权 | `QwenMtProvider.cs:63` `Authorization: Bearer {apiKey}` | 登录令牌塞进现有 `ISecretStore`，鉴权代码零改动 |
| 密钥存储 | `ISecretStore`（Core）/ DPAPI（Platform.Windows） | 存 refresh token |
| 自更新通道 | `src/FastTranslation.Core/UpdateChecker.cs:49-63` | 后端上线后可加「必须升级」门槛 |

### 2.4 云服务器价格（**二手来源，下单前必须现场核实**）

- 阿里云轻量应用服务器通用型档位：**35 / 40 / 45 / 70 / 220 元/月**（来源 `developer.aliyun.com/article/1716989`，非官方定价页；官方定价页是 JS 渲染，抓不到正文）。
- 促销价（2026）：2 核 2G 抢购价 38 元/年、2 核 4G 9.9 元/月 —— **促销价不可作为长期预算**。
- 本方案预算按 **35 元/月 ×12 = 420 元/年**（内地机房，2 核 2G）计；免备案走香港约 +30%。

---

## 3. 推荐架构（方案 B）

```
桌面客户端 1.2.x+                你的服务器（1 台轻量）              阿里云百炼
  │                                  │
  ├─ 登录 ──────────────► POST /api/auth/login ──► users 表
  │                        ◄── accessToken(1h) + refreshToken(30d)
  │
  ├─ 翻译 ──────────────► POST /v1/chat/completions ──► dashscope compatible-mode
  │   Bearer accessToken      │  校验配额 → 注入你的 Key → 转发
  │                        ◄── 流式透传 + 读 usage.prompt/completion_tokens
  │                        └─► usage_daily 累加（只记数字，不记文本）
  │
  └─ 看消耗 ────────────► GET /api/me/usage
```

**为什么网关能做到 provider 零改动**：网关对外就是 OpenAI 兼容的 `/chat/completions`。客户端只需要把 `OverrideBaseUrl` 设成 `https://api.<你的域名>/v1`、把 `ISecretStore` 里的 `qwenmt-api-key` 换成登录 accessToken + `refreshToken` 的刷新逻辑。`QwenMtProvider.cs` 一行不用改。

**为什么不用「短时令牌下发 Key」**：DashScope 只认真的 API Key，没有「短时令牌」这种东西。要客户端直连就只能把真 Key 下发，等于把 Key 交给每个用户，只能靠 Workspace 拆分 + 限额 + 事后吊销兜底 —— 这个代价通常不划算，所以推荐代理。

**推翻方案 B 的条件**（两条任一成立就要改）：
1. 你拿不到阿里云对「代理/转售/代付」的书面确认（`spec:743` 是项目自己的硬性前置条件）⇒ 退回方案 C，商用版改 BYO。
2. 你不想为域名做 ICP 备案 ⇒ 改用香港/新加坡轻量服务器（免备案），或退回方案 A。

---

## 4. 分三期落地

### 阶段 0：账号与统计（无争议，先做，约 1.5 周）

只做「谁在用」，不动 Key 归属，**零合规风险**。

- 后端：注册/登录/刷新/设备绑定/管理员看板（用户总数、日活、按设备数）。
- 客户端：登录窗、令牌持久化、「账号」设置分组。
- 翻译链路：**不变**，仍是用户自己的 Key 直连（`App.xaml.cs:88` 保持默认）。

### 阶段 1：计量可见（约 1 周，两个方案都需要）

- `TranslationTypes.cs:31-42` 的 `TranslationResult` 增加 `TokenUsage? Usage { get; init; }`（可空 init 属性，既有 6 处构造点全部仍合法）。
- `QwenMtProvider.cs:131-147` 解析 `usage.prompt_tokens / completion_tokens`。
- 客户端本地累计 + 设置页显示「本月 X 次 / Y Token ≈ Z 元」，按上表单价换算。
- **此时数据是客户端自报，只能给用户看，不能给你做账。**

### 阶段 2：服务端代理 + 双档（约 3 周，需 §7 合规前置条件）

- 网关 `/v1/chat/completions`：验 accessToken → 查配额 → 注入服务端 Key → 转发 → 读 `usage` 记账 → 扣配额。
- 图片链路同样代理：`QwenMtImageTranslator.cs:12` 的 BaseUrl(`/api/v1/services/aigc/image2image/image-synthesis`)需加一条网关路由，按 0.004 元/张记账。
- 免费版/商用版：`plan` 字段 + 月配额；免费版按月 token 上限硬截断（返回 429 语义，客户端已有 `RateLimited` 处理，`TranslationTypes.cs:21`）。
- 管理后台：注册用户数、日活、每用户用量与折算金额、套餐变更、封号。

---

## 5. 后端技术选型（建议）

| 项 | 选择 | 理由 |
|---|---|---|
| 语言/框架 | **ASP.NET Core Minimal API（.NET 10）** | 与客户端同一门语言、同一套 SDK，你维护成本最低；不引入 Node/Java |
| 数据库 | **SQLite → PostgreSQL** | 500 用户以内 SQLite 完全够（单文件备份即可）；上量后换 PG，EF Core 换 provider 即可 |
| 部署 | 单二进制 self-contained + systemd + Caddy（自动 HTTPS） | 与客户端已有的 publish 流程同构，无需 Docker 学习成本 |
| 管理后台 | Blazor Server 单页（复用同一后端） | 不额外开前端工程 |

### 5.1 数据表（最小集）

```
users        (id, email, password_hash, plan, created_at, status, invite_code_used)
devices      (id, user_id, device_id, first_seen, last_seen, name)     -- 一人多机
refresh_tokens (jti, user_id, expires_at, revoked_at)
invites      (code, max_uses, used_count, expires_at)
usage_daily  (user_id, day, model, prompt_tokens, completion_tokens, image_count, cost_micro_yuan)
```

**红线（继承 `docs/PLAN.md:62`「不记录选中文本」）**：`usage_daily` 只存 Token 数、张数与折算金额，**不落任何原文或译文**。网关日志同样不得写请求体。

### 5.2 接口清单

```
POST /api/auth/register      {email, password, inviteCode}
POST /api/auth/login         -> {accessToken(1h), refreshToken(30d), plan}
POST /api/auth/refresh
GET  /api/me                 -> {email, plan, quotaMonthly, usedThisMonth, deviceCount}
GET  /api/me/usage?from&to   -> 按日聚合
POST /v1/chat/completions    (OpenAI 兼容，Bearer accessToken)
POST /api/v1/services/aigc/image2image/image-synthesis   (图片，Bearer accessToken)
GET  /admin/users            管理看板（仅管理员令牌）
```

---

## 6. 成本模型（可复算）

### 6.1 API 成本（按 `qwen-mt-flash`，含 45 Token 固定开销）

单人每月免费额度定为 **5 万字符**（沿用 `spec:1009-1022` 的起点建议）：

- 假设平均每次划词 60 字 → 约 **833 次/月/人**
- 输入 Token = 833 × (60 + 45) ≈ **87,500**
- 输出 Token ≈ 833 × 70 ≈ **58,000**（中译英译文偏长，取 1.2×）
- 每人每月成本 = 87,500×0.7/1e6 + 58,000×1.95/1e6 = 0.061 + 0.113 ≈ **0.17 元**

| 免费用户数 | API 月成本 | 服务器月成本 | 合计 |
|---|---|---|---|
| 50 | ≈ 9 元 | 35 元 | **≈ 44 元/月** |
| 200 | ≈ 35 元 | 35 元 | **≈ 70 元/月** |
| 1,000 | ≈ 175 元 | 35~70 元 | **≈ 210~245 元/月** |
| 5,000 | ≈ 875 元 | 220 元 | **≈ 1,095 元/月** |

**关键洞察**：用户量 200 以下时，**服务器固定成本大于 API 成本**；API 便宜到不构成压力（1,000 个跑满额度的免费用户才 175 元/月）。所以定价压力主要来自服务器和你的时间，不是 Token。

### 6.2 商用版定价参考（框架，不是决定）

`月费 ≈ (平均 API 成本 × 3) + 服务器分摊`。按重度用户（每月 50 万字符 ≈ 1.7 元 API）算，**15~25 元/月**是可覆盖成本且有毛利的位置；免费版给 5 万字符，两档之间差 10 倍用量，用户升级动机清晰。

---

## 7. 必须正面回应的 3 条既有约束

### 7.1 隐私承诺要改口（方案 B 一定会打破）

- 现行 `spec:29`：「文本由客户端直接发往阿里云百炼，**不经过开发者服务器**」；`:141` 把这条列为 BYO 模式的卖点。
- 走代理后这句话就是假的，**必须改**：官网/README/隐私政策统一改成「文本经我们的网关转发（仅加鉴权，不落盘）」。这是对已有用户（你朋友）的告知义务，不能悄悄改。

### 7.2 代理需要阿里云书面确认（`spec:743` 是硬前置）

- 原文：「v1.1 若做免费额度代理，**必须先取得阿里云对『代理/转售/代付』的书面确认；未确认前不实现代理**」。用户付钱给你、你用你的 Key 调百炼，就是代付/转售性质。
- 动作：开工阶段 2 之前，先开工单问百炼。**回执是阶段 2 的开工条件，不是收尾事项。**

### 7.3 备案与个人信息合规

- 内地服务器 + 域名必须 ICP 备案（约 2~3 周，需要身份证/主体信息）；**免备案路径 = 香港/新加坡轻量服务器**，延迟增加 30~50ms，对划词翻译几乎无感。
- 账号体系上线即开始处理个人数据（邮箱、设备标识），需要一份隐私政策 + 注销/删除通道。

---

## 8. 客户端改动清单（文件级，供排期）

| 文件 | 改动 | 阶段 |
|---|---|---|
| `src/FastTranslation.Core/Settings.cs:5-25` | `AppSettings` 增 `ServerBaseUrl`、`AccountEmail`、`LastKnownPlan` | 0 |
| `src/FastTranslation.Core/TranslationTypes.cs:31-42` | `TranslationResult` 增 `TokenUsage? Usage { get; init; }` | 1 |
| `src/FastTranslation.Providers.QwenMt/QwenMtProvider.cs:131-147` | `ReadTranslationAsync` 解析 `usage` | 1 |
| 新增 `src/FastTranslation.Core/AccountService.cs` | 注册/登录/刷新/取用量（HttpClient） | 0 |
| 新增 `src/FastTranslation.App/AccountWindow.xaml(.cs)` | 登录/注册/用量页 | 0 |
| `src/FastTranslation.App/SettingsWindow.xaml` | 新增「账号与用量」分组（现有分组：翻译与外观 / 取词与写回 / 服务 / 系统） | 0 |
| `src/FastTranslation.App/App.xaml.cs:84-91` | 组合根：已登录 → BaseUrl 指向网关；未登录 → 保持直连 | 2 |
| `src/FastTranslation.App/App.xaml.cs:155` | 图片 provider 同样指向网关 | 2 |
| 新增 `server/FastTranslation.Server/` | 网关 + 账号 API + 管理页 | 0~2 |

---

## 9. 工作量估算

| 阶段 | 内容 | 人天 |
|---|---|---|
| 0 | 后端骨架 + 账号 API + 客户端登录与设置页 | 8~10 |
| 1 | usage 解析 + 本地计量 + 用量 UI | 4~5 |
| 2 | 代理网关 + 配额 + 双档 + 管理后台 | 12~15 |
| — | 备案（并行，不占开发） | 2~3 周墙钟 |
| — | 部署、HTTPS、备份、监控 | 2~3 |
| **合计** | 全量（方案 B） | **26~33 人天（约 6 周）** |

只要阶段 0+1（能统计用户、能看见消耗，但商用版仍 BYO）：**12~15 人天（约 3 周）**，且不触发 §7 的任何前置条件。

---

## 10. 待决策的 4 个点

1. **商用版的钱怎么收**：手动开通/激活码（省 2~3 周）｜外部卖卡密｜接微信支付宝（需营业执照+商户号，多 2~3 周）。
2. **翻译请求走哪条路**：服务端代理（推荐，但触发 §7.1/7.2）｜客户端直连 + Key 下发（Key 会暴露）｜商用版仍 BYO（零合规风险，但「用你的 Key」不成立）。
3. **服务器放哪**：内地机房（35 元/月，需 ICP 备案 2~3 周）｜香港（约 +30%，免备案，延迟 +30~50ms）。
4. **免费版额度定多少**：5 万字符/月（≈0.17 元/人/月，沿用既有 spec 建议）｜更低（1 万字符，≈0.035 元）｜更高。

---

## 11. 未核实项（写在这里，避免当成事实用）

- 云服务器具体配置与报价：官方定价页是 JS 渲染抓不到正文，本文档引的是二手文章数据，**下单前必须在控制台核价**。
- `qwen-mt-image` 图片接口是否也返回 `usage` 字段：官方文档只给了文本 chat 的样本，图片链路的 Token/张数计量方式需实测确认（若只按张计，则按 0.004 元/张 × 张数即可）。
- 香港/新加坡机房对百炼 API 的实测延迟：需部署后实测，本文档的「+30~50ms」是经验值。
