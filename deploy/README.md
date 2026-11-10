# 服务器部署

一台阿里云 ECS(经济型 e,2 核 2G / 40G ESSD Entry / 3 Mbps 固定带宽 / Ubuntu 26.04)上同时跑三件东西:

| 组件 | 监听 | 说明 |
|---|---|---|
| FastTranslation.Server | `127.0.0.1:5080` | 账号、笔记、开发者网关。只监听回环,不对外 |
| Caddy | `0.0.0.0:80,443` | 自动申请并续期证书;静态站点 + 反代 `/api/*`、`/v1/*`、`/health`;运营后台 `/admin/` 用独立 root |
| SQLite | `/var/lib/fasttranslation/fasttranslation.db` | WAL 模式,由上面的 server 进程独占 |

对外只有 80/443。应用进程以 `ftserver` 用户运行,systemd 的 `ProtectSystem=strict` 让它除 `/var/lib/fasttranslation` 之外整机只读。

## 文件

| 文件 | 去向 |
|---|---|
| `build-server.sh` | 本机执行,产出 `publish/server-linux` |
| `bootstrap.sh` | 服务器上执行一次(幂等):swap 与 `vm.swappiness=10`、系统账号、目录、Caddy(装完即停用)、libicu、SSH 加固、ufw |
| `install.sh` | 服务器上执行:先校验生产配置,再停服务拷二进制(所以可以原地重复执行做升级),然后 systemd 单元、静态站点、运营后台、备份任务、Caddyfile,最后启动并打印健康检查。**已填好的 `/etc/caddy/Caddyfile` 不会被覆盖** |
| `fasttranslation.service` | → `/etc/systemd/system/fasttranslation.service` |
| `Caddyfile` | → `/etc/caddy/Caddyfile`(两处占位符) |
| `appsettings.Production.json.example` | → `/opt/fasttranslation/appsettings.Production.json` 后填空 |
| `backup.sh` | → `/usr/local/bin/ft-backup.sh` |
| `fasttranslation-backup.cron` | → `/etc/cron.d/fasttranslation-backup` |
| `ft-alert.sh` | → `/usr/local/lib/ft-alert.sh`（飞书发送与告警去重，被下面两个脚本 source） |
| `ft-probe.sh` | → `/usr/local/bin/ft-probe.sh`（合成翻译探测） |
| `ft-monitor.sh` | → `/usr/local/bin/ft-monitor.sh`（资源检查与每日摘要） |
| `provision-probe.sh` | → `/usr/local/bin/ft-provision-probe.sh`（一次性创建探针账号） |
| `fasttranslation-monitor.cron` | → `/etc/cron.d/fasttranslation-monitor` |
| `monitor.env.example` | 首次安装复制为 `/etc/fasttranslation/monitor.env`（600）；填探针密码与飞书 webhook |
| `go-live.sh` | 备案号下来后在服务器上执行一次:替换占位符、填备案号、启用 Caddy 并断言五项 |

## 首次部署

```bash
# 1) 本机:发布 linux-x64 自包含二进制
bash deploy/build-server.sh

# 2) 本机:把 deploy/ 与两个产物一起打成一份上传目录
rm -rf publish/ftdeploy
mkdir -p publish/ftdeploy
cp deploy/bootstrap.sh deploy/install.sh deploy/go-live.sh deploy/fasttranslation.service \
   deploy/Caddyfile deploy/backup.sh deploy/fasttranslation-backup.cron \
   deploy/ft-alert.sh deploy/ft-probe.sh deploy/ft-monitor.sh \
   deploy/provision-probe.sh deploy/fasttranslation-monitor.cron \
   deploy/monitor.env.example publish/ftdeploy/
cp deploy/appsettings.Production.json.example publish/ftdeploy/
cp -r publish/server-linux publish/ftdeploy/server
cp -r site publish/ftdeploy/site
cp -r admin publish/ftdeploy/admin

# 3) 上传(IP 换成你的)
scp -r publish/ftdeploy root@YOUR_SERVER_IP:/root/ftdeploy

# 4) 服务器:装系统依赖
ssh root@YOUR_SERVER_IP 'bash /root/ftdeploy/bootstrap.sh'

# 5) 服务器:填生产配置(见下节),再跑安装脚本
ssh root@YOUR_SERVER_IP 'cp /root/ftdeploy/appsettings.Production.json.example /opt/fasttranslation/appsettings.Production.json'
# ...编辑该文件填 SigningKey / AdminToken / Gateway:ApiKey...
ssh root@YOUR_SERVER_IP 'bash /root/ftdeploy/install.sh'
```

第 5 步的 `install.sh` 会做幂等安装,最后打印健康检查结果。Caddy 只有在 `Caddyfile` 的占位符被替换后才会被启用 —— 这是刻意的,见下文。`bootstrap.sh` 里的 SSH 加固只在 `/root/.ssh/authorized_keys` 非空时才动手(否则它会把自己锁在门外);不想动 sshd 和防火墙就加 `FT_SKIP_HARDEN=1`。

## 真机验证记录(2026-10-29,华东1 杭州 / Ubuntu 26.04.1)

整套流程在 2 核 2G 的 ECS 上从零跑通,不是纸面推演:

| 检查项 | 实测 |
|---|---|
| `bootstrap.sh` | 退出 0;`ftserver` uid 999;`vm.swappiness=10` 生效 |
| `install.sh` 首装 | systemd `enabled + active`,`curl /health` = `{"status":"ok"}` |
| `install.sh` 重复执行(升级) | 停服务→拷二进制→起服务,数据不丢(2 用户 / 1 笔记) |
| 内存占用 | `MemoryCurrent` ≈ 84 MB(自包含 .NET 10,机器可用 1288 MB) |
| 端口暴露 | 只有 `0.0.0.0:22` 与 `127.0.0.1:5080`;Caddy disabled,80/443 无监听 |
| 网关真翻译 | `POST /v1/chat/completions` → 200 / 535ms,上游 usage `43/17` |
| 按字符记账 | 请求 71 字符 → `usedMonthly/usedDaily` 各 +71;`usage_events` 落 `cost_micros` |
| 免费日额度闸门 | 1975 字符后的下一次 → `429` + `X-FT-Quota-Reason: daily_quota_exhausted` |
| 跨设备隔离 | 同账号换指纹仍 200(日额度按设备,月额度按账号) |
| 订阅闸门 | 免费账号 `POST /api/v1/notes/` → `403 {"error":"subscription_required"}`;升级 light 后 → 200 |
| 只读不受闸门影响 | 免费账号 `GET /api/v1/notes/` → 200 |
| 备份 | `ft-backup.sh` 产出 4909 字节 `.db.gz`,cron 04:17 UTC |

撞出来并当场修掉的两个真缺陷:阿里云镜像自带 `/swapfile`,旧脚本只在「新建 swapfile」分支里写 swappiness,导致调优静默失效;旧 `install.sh` 边跑边拷二进制,升级时在 apphost 上撞 `cp: ... Text file busy`(Linux 不允许覆写运行中的可执行文件)。

## 生产配置

`Server:SigningKey` 与 `Server:AdminToken` 是硬门槛:非 Development 环境下 SigningKey 缺失或不足 32 字节会直接启动失败(`server/FastTranslation.Server/Program.cs:96-100`)。

```bash
# 32 字节签名密钥(base64 形式,44 字符)
openssl rand -base64 32

# 管理令牌
openssl rand -hex 32
```

`Gateway:ApiKey` 填你自己的百炼 Key。填入后 `Gateway:Enabled` 为 true 即开始为免费用户提供带额度的翻译。

配置文件的权限是 `root:ftserver 640` —— server 进程只读,root 可改。

## Caddyfile 的两处占位

域名已定为 **ftranslation.cn**。Caddyfile 里 `__DOMAIN__` 出现两次（主域与 `www.`），一条 sed 同时替换：

```bash
sed -i 's/__DOMAIN__/ftranslation.cn/g; s/__ACME_EMAIL__/你的邮箱/g' /etc/caddy/Caddyfile
caddy validate --config /etc/caddy/Caddyfile
systemctl reload-or-restart caddy
```

## 页脚的备案号

`site/index.html` 页脚留了 `__ICP_NUMBER__` 占位。管局下发备案号后替换成真实号码（形如 `浙ICP备2026xxxxxx号-1`），链到 https://beian.miit.gov.cn/ ，境内网站底部必须展示。`install.sh` 检测到占位还在时会打印一行警告。

**备案通过之前不要启用 Caddy。** 域名解析过去就是对外开放,而且 ACME 也签不下证书。这段时间调试用 SSH 隧道:

```bash
ssh -L 5080:127.0.0.1:5080 root@YOUR_SERVER_IP
# 然后本机浏览器访问 http://127.0.0.1:5080/health
```

## 备案下来后:一条命令上线

`go-live.sh` 把「换 Caddyfile 占位 → 填页脚备案号 → `caddy validate` → 启用 Caddy → 逐项断言」收成一条命令,幂等,重复执行等于重新体检:

```bash
bash /root/ftdeploy/go-live.sh --email 你的邮箱 --icp '豫ICP备2026xxxxxx号-1' --expect-ip YOUR_SERVER_IP
```

先看它怎么说,再让它动手 —— `--dry-run` 只做只读诊断(解析、端口、占位符),不改文件、不启 Caddy,发现问题时退出码非 0:

```bash
bash /root/ftdeploy/go-live.sh --email 你的邮箱 --icp '豫ICP备2026xxxxxx号-1' --expect-ip YOUR_SERVER_IP --dry-run
```

它断言五项:80 回 301/308、`https://<域名>/health` 回 `{"status":"ok"}`、证书主体与到期日、页面不含 `__ICP_NUMBER__` 且链到 beian.miit.gov.cn、页面确实带备案号。任一项不过就打印回滚命令(`Caddyfile` 与 `index.html` 都留了 `.bak`)。

脚本会先替你验两个前置:**云解析** `A @ -> 公网 IP` 与 `CNAME www -> 裸域` 已生效、**安全组**放行 80/443。缺一个就直接退出,不碰文件。

> 备案号到手后,别忘了同时把它写进仓库的 `site/index.html`(替换 `__ICP_NUMBER__`)并提交 —— 否则下次 `install.sh` 会把页脚覆盖回占位符。`install.sh` 已经不会覆盖已配好的 `/etc/caddy/Caddyfile`,但站点文件是整目录同步的。

## 安全组

入方向只留这四条（2026-10-29 已按此配好，实测从公网只有 22/80/443 能到达机器）：

| 端口 | 来源 | 优先级 | 用途 |
|---|---|---|---|
| 22 | 开发机出口 IP `/32`（当前 `YOUR_DEV_IP/32`） | 100 | SSH，只认密钥 |
| 22 | `100.104.0.0/16` | 100 | Workbench「文件管理」——它走真实 sshd + 实例密码 |
| 80 | `0.0.0.0/0` | 1 | ACME HTTP 挑战 + 跳转 443 |
| 443 | `0.0.0.0/0` | 1 | 站点与 API |

- 5080 **不要**对外放行：应用只监听 `127.0.0.1:5080`，调试走 `ssh -L`。
- 镜像自带的 `RDP(3389)`，以及 `3000 / 8888 / 23404` 这类「任何位置 + 优先级 1」的放行一律删掉。阿里云优先级数字越小越优先，留着它们等于以后哪个软件一绑定端口就立刻对公网开门。
- 开发机 IP 是动态的，变了就连不上 22。两条兜底都不经过 22：控制台 VNC/管理终端（用实例密码）、Workbench 免密连接（走云助手 agent）。
- sshd 侧的按来源放行（公网只密钥、内网段可密码）见 `docs/IMPL.md` v53。

验证方法：临时在机器上起监听，再从本机探测，能分清「门口被丢」和「机器自己拒绝」：

```bash
ssh root@YOUR_SERVER_IP 'nohup timeout 40 python3 -m http.server 443 --bind 0.0.0.0 >/dev/null 2>&1 &'
# 本机对 22/80/443/3000/3389/8888/23404 各发一次 TCP 连接：
#   OPEN     = 安全组放行，且机器有人监听
#   REFUSED  = 安全组放行，但机器无监听（秒回 RST）
#   超时     = 包在阿里云门口就被丢掉
```

## 备份与恢复

```bash
/usr/local/bin/ft-backup.sh        # 手动备份
ls -lh /var/backups/fasttranslation
```

恢复:

```bash
systemctl stop fasttranslation
gunzip -c /var/backups/fasttranslation/fasttranslation-<stamp>.db.gz \
	> /var/lib/fasttranslation/fasttranslation.db
chown ftserver:ftserver /var/lib/fasttranslation/fasttranslation.db
systemctl start fasttranslation
```

## 监控与告警

`install.sh` 安装三组任务（`/etc/cron.d/fasttranslation-monitor`）：

| 任务 | 频率 | 检查 |
|---|---|---|
| `ft-probe.sh` | 每 15 分钟 | 专用账号登录 → 真实 `/v1/chat/completions` → 200 且译文非空；抓百炼 Key 失效、网关/记账坏掉 |
| `ft-monitor.sh check` | 每小时 | 最新备份 >36h、`/var/lib/fasttranslation` 磁盘 >85%、今日网关花费 ≥ 预算 80% |
| `ft-monitor.sh digest` | 每天 09:00 CST | 新增注册、30 天活跃、昨日请求/字符/成本、今日花费/预算、`usage_events` 最早月份 |

告警发到飞书群机器人：进入失败态发一次，持续失败每 6 小时提醒，恢复时补一条。状态文件在 `/var/lib/fasttranslation-monitor/`。

### 首次配置

1. 飞书群 → 群设置 → 群机器人 → 添加「自定义机器人」；安全设置选「自定义关键词」，关键词填 `FastTranslation`。复制 webhook。
2. 填 `/etc/fasttranslation/monitor.env`（`install.sh` 首次安装会从 `deploy/monitor.env.example` 复制，权限 600）：
   - `FT_PROBE_PASSWORD`：用 `openssl rand -hex 16` 生成；
   - `FT_FEISHU_WEBHOOK`：上一步复制的地址。
3. 创建探针账号（注册 → standard 10 年 → 验证登录）：
   ```bash
   bash /usr/local/bin/ft-provision-probe.sh
   ```
4. 手工跑一次，确认飞书群收到：
   ```bash
   /usr/local/bin/ft-probe.sh
   /usr/local/bin/ft-monitor.sh digest
   ```
   调试时可设 `FT_DRY_RUN=1`，只打印不发送。
5. 云监控（可选，但整机挂了只有它能在服务器之外报警）：
   - 报警联系人：手机号 + 邮箱，建联系组；
   - 站点监控：`https://fttranslation.cn/health`，5 分钟，状态码非 200/超时告警；**域名备案与 Caddy 启用后再加**，不要用 `https://IP`（证书不匹配）；
   - ECS 主机监控：CPU >80%、磁盘 >85%；内存/磁盘可能需要云监控插件，没有插件就靠 `ft-monitor.sh` 的 `df` 检查。

### 手工操作

```bash
/usr/local/bin/ft-probe.sh
/usr/local/bin/ft-monitor.sh check
/usr/local/bin/ft-monitor.sh digest
tail -f /var/log/ft-monitor.log
```

`ft-probe.sh` 和 `ft-monitor.sh` 都从 `/etc/fasttranslation/monitor.env` 读配置；真实 webhook 和探针密码只放那里，不进仓库。`ft-monitor.sh` 用 `sqlite3 -readonly` 读生产库（WAL 下不阻塞写入），并用 `jq` 读 `appsettings.Production.json` 的 `Server:Gateway:DailyBudgetMicros`；`bootstrap.sh` 会安装 `jq`。

## 运营后台

`admin/` 是一个没有任何构建步骤的静态页：登录后看概览、7/30 天用量、账号（改套餐 / 禁用）、激活码（生成 / 作废）和网关健康。它由 `install.sh` 复制到 `/var/www/fasttranslation-admin`，Caddy 用独立的 `/admin/*` root 提供，和官网的整目录同步互不影响。

访问 `https://<域名>/admin/`，令牌是 `/opt/fasttranslation/appsettings.Production.json` 里的 `Server:AdminToken`。页面只把令牌放在当前标签页的 `sessionStorage`，关闭标签即清除；请求走同域的 `/api/v1/admin/*`，服务端按 IP 限制每分钟 20 次。令牌是共享密钥：轮换时改配置并重启服务，所有已打开的后台会立即失效。

Caddy 对后台额外加了 `Cache-Control: no-store`、`Content-Security-Policy`（只允许同源脚本/样式/请求）、`X-Frame-Options: DENY`、`Referrer-Policy: no-referrer` 和 `X-Robots-Tag: noindex`；`site/robots.txt` 也写了 `Disallow: /admin`。想再加一层遮挡，可按 `deploy/Caddyfile` 里的注释启用 Basic Auth —— 它不能替代后台令牌。

**已填好的 `/etc/caddy/Caddyfile` 不会被 `install.sh` 覆盖。** 第一次升级到带后台的版本时，`install.sh` 会提示 `/admin` 路由缺失；把 `deploy/Caddyfile` 里的 `redir /admin` 与 `handle_path /admin/*` 两段合并进现有配置，或者用 `FT_FORCE_CADDYFILE=1 bash install.sh` 重新铺模板（会覆盖手改）。

## 升级

升级就是再跑一遍 `install.sh`:它会自己停服务、拷完再启动(不要手写 stop/cp/start —— 边跑边拷会撞上面那条 Text file busy)。上传目录的铺法与首次部署相同,产物用最新的 `publish/server-linux`:

```bash
bash deploy/build-server.sh
rm -rf /root/ftup && mkdir -p /root/ftup
cp deploy/bootstrap.sh deploy/install.sh deploy/go-live.sh deploy/fasttranslation.service \
   deploy/Caddyfile deploy/backup.sh deploy/fasttranslation-backup.cron \
   deploy/ft-alert.sh deploy/ft-probe.sh deploy/ft-monitor.sh \
   deploy/provision-probe.sh deploy/fasttranslation-monitor.cron \
   deploy/monitor.env.example /root/ftup/
cp -r publish/server-linux /root/ftup/server
cp -r site /root/ftup/site
cp -r admin /root/ftup/admin
tar czf /root/ftup.tgz -C /root/ftup .
scp /root/ftup.tgz root@YOUR_SERVER_IP:/tmp/
ssh root@YOUR_SERVER_IP 'rm -rf /root/ftdeploy && mkdir -p /root/ftdeploy && tar xzf /root/ftup.tgz -C /root/ftdeploy && bash /root/ftdeploy/install.sh'
```

`/opt/fasttranslation/appsettings.Production.json` 不在上传目录里,升级不会覆盖服务器上的生产配置。

数据库结构变更由服务端启动时的 `ServerDatabase.Initialize()` 负责。升级前先跑一次备份 —— SQLite 的 `ALTER TABLE` 一旦写坏没有回退路径。

## 日志

```bash
journalctl -u fasttranslation -f          # 应用
journalctl -u caddy -f                    # Caddy(证书申请失败看这里)
cat /var/log/ft-backup.log                # 备份
tail -f /var/log/ft-monitor.log           # 合成探测、资源检查与每日摘要
```
