# 运维接口与轻量管理后台 — 设计规格

> 状态：草案（2026-11-10）
> 关联：内部账号与计费设计（含定价与成本，不入公开仓）§5 阶段 2、§9 后端选型。
> 本文只写可公开的接口契约与行为。预算金额、成本、告警阈值与防刷细则不在这里，见内部实现计划与生产配置。

## 1. 目标与非目标

目标：单操作员用 `X-Admin-Token` 和同域静态页面，完成账号查询、套餐变更、封禁/启用、激活码管理，并看到注册、活跃、用量与网关健康数据。

非目标：

- 多操作员、角色权限、审计表（先用结构化日志）；
- 单独调整某个账号的额度（额度按套餐档位计算，改额度需要新的 schema 决策）；
- 实时大屏、图表库、Prometheus/Grafana/APM、移动 App；
- 桌面客户端不调用管理接口。

## 2. 认证与暴露面

- 所有 `/api/v1/admin/*` 端点要求请求头 `X-Admin-Token`。服务端用常量时间比较；`Server:AdminToken` 为空时管理面整体关闭（返回 403 `admin_forbidden`）。
- 管理端点按客户端 IP 限流，超限返回 429；限额可配置，默认值见服务端配置。
- 管理页面与 API 同源。页面本身不包含 token；操作员在页面里粘贴 token 后存 `sessionStorage`，关闭标签即清除。token 不进 URL、不写 `localStorage`。
- 页面路径 `/admin` 不链接进公开站点，`robots.txt` 禁止抓取，响应带 `noindex`。
- Basic Auth 为可选项，默认关闭；开启时只保护 `/admin*` 页面路径，不保护 `/api/v1/admin/*`，所以 `curl` 调 API 仍只需要 `X-Admin-Token`。
- 管理日志不记录 token、密码、生成的激活码；不记录用户翻译文本。

## 3. 端点

### 3.1 已有（保持不变）

| 方法 | 路径 | 说明 |
|---|---|---|
| `GET` | `/api/v1/admin/metrics` | 账号总数、按套餐分布、30 天活跃数 |
| `POST` | `/api/v1/admin/accounts/{email}/plan` | 改套餐；`days` 为 null 保留到期时间、0 表示永久、正数表示相对天数 |
| `POST` | `/api/v1/admin/codes` | 批量生成激活码，一次最多 200 张；响应是这批码的唯一一次批量返回，未兑换的码可在 `GET /codes` 里找回 |
| `GET` | `/api/v1/admin/codes?status=&limit=&offset=` | 激活码分页；`status` 为 `open`/`used`/`revoked`/`all`，默认 `open` |
| `POST` | `/api/v1/admin/codes/{code}/revoke` | 作废未兑换的码；幂等 |

响应字段沿用现有 `AdminMetricsResponse`、`MessageResponse`、`CreateCodesResponse`、`RedeemCodePageResponse`。

补差价升级复用 `POST /admin/accounts/{email}/plan` 并传 `days: null`：只切换套餐、保留到期时间；`usage_monthly` / `usage_daily` 不动，所以本月已用字符继续从新套餐额度里扣，不重置。差价由运营在站外收取，服务端不计算金额。后台页面只对生效中的轻享账号显示「升级标准」入口。

### 3.2 新增

#### `GET /api/v1/admin/usage?days=`

`days` 默认 7，范围 1–30。按 UTC 日统计。

```json
{
  "days": 7,
  "fromUnix": 1762790400,
  "toUnix": 1763395200,
  "total": { "requests": 0, "chars": 0, "tokensIn": 0, "tokensOut": 0, "costMicros": 0 },
  "bySource": [
    { "source": "gateway", "requests": 0, "chars": 0, "costMicros": 0 }
  ],
  "byDay": [
    { "day": "2026-11-10", "requests": 0, "chars": 0, "costMicros": 0 }
  ],
  "today": { "costMicros": 0, "budgetMicros": 0 }
}
```

- `costMicros` 与 `budgetMicros` 以微元为单位（`1e-6` 元），保持整数账本。
- 只统计 `usage_events` 里已有的记账；不读用户翻译文本。
- 数据窗口超过归档保留期时，只返回仍在 `usage_events` 里的部分。

#### `GET /api/v1/admin/accounts?query=&plan=&limit=&offset=`

- `query` 为邮箱前缀匹配，不做通配符注入；`limit` 默认 50、上限 200；`offset` 用于翻页。
- `plan` 可选，按存储套餐过滤（`free`/`light`/`standard`）。

```json
{
  "total": 0,
  "items": [
    {
      "email": "user@example.com",
      "plan": "free",
      "effectivePlan": "free",
      "planExpiresAt": null,
      "disabled": false,
      "createdAt": 0,
      "lastSeenAt": 0,
      "monthlyChars": 0
    }
  ]
}
```

- `effectivePlan` 已把过期付费折算回 `free`；`plan` 保留数据库里的原始值。
- `lastSeenAt` 取该账号设备的最大 `last_seen_at`；`monthlyChars` 取当前 UTC 月的 `usage_monthly`。
- 按 `created_at DESC` 排序。

#### `POST /api/v1/admin/accounts/{email}/disable` / `enable`

- 幂等；成功返回 200 `{"message":"account_disabled"}` 或 `{"message":"account_enabled"}`。
- 未知邮箱返回 404 `{"error":"account_missing"}`。
- 语义见 §4。

#### `GET /api/v1/admin/diagnostics`

```json
{
  "startedAtUnix": 0,
  "uptimeSeconds": 0,
  "gateway": {
    "requests": 0,
    "failures": 0,
    "byReason": { "rate_limited": 0 },
    "lastFailureAtUnix": null,
    "lastFailureReason": null
  },
  "speech": {
    "requests": 0,
    "failures": 0,
    "byReason": {},
    "lastFailureAtUnix": null,
    "lastFailureReason": null
  },
  "database": { "sizeBytes": 0, "walBytes": 0 }
}
```

- 计数器是进程内状态，重启清零；不持久化，也不写用户文本。
- `byReason` 使用现有网关错误 token（如 `rate_limited`、`budget_exhausted`、`quota_exhausted`）。

## 4. 禁用语义

- `users.disabled = 1` 后：
  - 所有 Bearer 端点返回 401；认证处理器验签后每次查库按 `disabled` 拒绝，所以不用等 access token 过期。
  - 所有 refresh token 立即吊销；启用后用户必须重新登录。
- 网关原有的 `403 {"error":"account_disabled"}` 保留为二道防线。
- 禁用/启用本身是幂等的管理动作；重复调用不会报错。

## 5. 页面

- 页面是纯静态文件：`admin/index.html`、`admin.css`、`admin.js`，无构建步骤、无第三方脚本。
- 由 Caddy 从独立目录提供；与 `/api/v1/admin/*` 同源。
- 功能：
  - token 登录与退出（`sessionStorage`）；
  - 账号卡片与用量卡片；
  - 最近 7/30 天按日用量；
  - 账号搜索、分页、套餐变更、禁用/启用；
  - 激活码列表、生成、作废。
- 安全头：`Cache-Control: no-store`、`X-Frame-Options: DENY`、`X-Content-Type-Options: nosniff`、`Referrer-Policy: no-referrer`、`Content-Security-Policy: default-src 'self'`。
- 数据渲染用 `textContent`，不用 `innerHTML`；不引第三方字体/脚本。

## 6. 客户端 IP 与限流

- 服务端启用 `ForwardedHeaders`：只处理 `X-Forwarded-For` 与 `X-Forwarded-Proto`，`ForwardLimit = 1`。
- 只有直接对端是 loopback（生产中的 Caddy）时才信任这些头；其他对端直接忽略，避免伪造限流键与日志里的 IP。
- `ForwardLimit = 1` 表示取最后一跳；客户端自带的 `X-Forwarded-For` 会被 Caddy 追加在真实 IP 之前，不会成为限流键。
- 认证与管理限流都按还原后的客户端 IP 分区；额度仍按账号/设备计，不按 IP。

## 7. 错误码

| 错误码 | 场景 |
|---|---|
| `admin_forbidden` | 未配置 `AdminToken` 或令牌错误 |
| `plan_invalid` | 套餐字段不是 `free`/`light`/`standard` |
| `days_invalid` | 天数越界 |
| `count_invalid` | 批量生成数量越界 |
| `status_invalid` | 激活码查询状态未知 |
| `account_missing` | 邮箱不存在 |
| `redeem_code_invalid` | 激活码规范化后为空 |
| `code_missing` | 激活码不存在 |
| `code_already_redeemed` | 已兑换的码不能作废 |

限流拒绝沿用框架的 429，不额外返回 JSON 错误体。

## 8. 兼容性与部署

- 新端点全部是增量；桌面客户端不调用管理接口，不受影响。
- 禁用语义只影响被禁用的账号；正常账号的 token 生命周期不变。
- 管理页面和 `admin/` 目录只在部署时复制；不进入公开站点的 `index.html`。
- Caddy 增加 `/admin*` 路由后才对外提供页面；未部署时页面不存在，API 仍按 token 保护。
- `ForwardedHeaders` 的修改对直连本地开发无影响；生产只信任 Caddy 的 loopback 连接。

## 9. 隐私与公开仓

- 管理接口只返回账号元数据、用量聚合与错误计数，不返回翻译原文/译文。
- 截图与示例数据必须编造，不使用真实账号、邮箱、订单或 Key 片段。
- 告警脚本与页面不包含服务端密钥；凭据只放服务器上的 root-only 环境文件。

## 10. 未决

- 单独调整账号额度：需要 schema 与 Entitlement 决策，暂不做。
- 审计表：单操作员先用结构化日志；多操作员时再建表。
- Basic Auth：部署时决定是否开启；本规格只约定「开启时只保护页面路径」。
