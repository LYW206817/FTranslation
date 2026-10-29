# 服务器部署

一台阿里云 ECS(经济型 e,2 核 2G / 40G ESSD Entry / 3 Mbps 固定带宽 / Ubuntu 26.04)上同时跑三件东西:

| 组件 | 监听 | 说明 |
|---|---|---|
| FastTranslation.Server | `127.0.0.1:5080` | 账号、笔记、开发者网关。只监听回环,不对外 |
| Caddy | `0.0.0.0:80,443` | 自动申请并续期证书;静态站点 + 反代 `/api/*`、`/v1/*`、`/health` |
| SQLite | `/var/lib/fasttranslation/fasttranslation.db` | WAL 模式,由上面的 server 进程独占 |

对外只有 80/443。应用进程以 `ftserver` 用户运行,systemd 的 `ProtectSystem=strict` 让它除 `/var/lib/fasttranslation` 之外整机只读。

## 文件

| 文件 | 去向 |
|---|---|
| `build-server.sh` | 本机执行,产出 `publish/server-linux` |
| `bootstrap.sh` | 服务器上执行一次(幂等):swap、系统账号、目录、Caddy、libicu |
| `install.sh` | 服务器上执行:二进制、systemd 单元、静态站点、备份任务、Caddyfile |
| `fasttranslation.service` | → `/etc/systemd/system/fasttranslation.service` |
| `Caddyfile` | → `/etc/caddy/Caddyfile`(两处占位符) |
| `appsettings.Production.json.example` | → `/opt/fasttranslation/appsettings.Production.json` 后填空 |
| `backup.sh` | → `/usr/local/bin/ft-backup.sh` |
| `fasttranslation-backup.cron` | → `/etc/cron.d/fasttranslation-backup` |

## 首次部署

```bash
# 1) 本机:发布 linux-x64 自包含二进制
bash deploy/build-server.sh

# 2) 本机:把 deploy/ 与两个产物一起打成一份上传目录
rm -rf publish/ftdeploy
mkdir -p publish/ftdeploy
cp deploy/bootstrap.sh deploy/install.sh deploy/fasttranslation.service \
   deploy/Caddyfile deploy/backup.sh deploy/fasttranslation-backup.cron publish/ftdeploy/
cp deploy/appsettings.Production.json.example publish/ftdeploy/
cp -r publish/server-linux publish/ftdeploy/server
cp -r site publish/ftdeploy/site

# 3) 上传(IP 换成你的)
scp -r publish/ftdeploy root@YOUR_SERVER_IP:/tmp/ftdeploy

# 4) 服务器:装系统依赖
ssh root@YOUR_SERVER_IP 'bash /tmp/ftdeploy/bootstrap.sh'

# 5) 服务器:填生产配置(见下节),再跑安装脚本
ssh root@YOUR_SERVER_IP 'cp /tmp/ftdeploy/appsettings.Production.json.example /opt/fasttranslation/appsettings.Production.json'
# ...编辑该文件填 SigningKey / AdminToken / Gateway:ApiKey...
ssh root@YOUR_SERVER_IP 'bash /tmp/ftdeploy/install.sh'
```

第 5 步的 `install.sh` 会做幂等安装,最后打印健康检查结果。Caddy 只有在 `Caddyfile` 的占位符被替换后才会被启用 —— 这是刻意的,见下文。

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

## 安全组

| 端口 | 来源 | 用途 |
|---|---|---|
| 22 | 你的固定 IP `/32` | SSH |
| 80 | `0.0.0.0/0` | ACME HTTP 挑战 + 跳转 443 |
| 443 | `0.0.0.0/0` | 站点与 API |

5080 **不要**对外放行。

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

## 升级

```bash
bash deploy/build-server.sh
scp -r publish/server-linux root@YOUR_SERVER_IP:/tmp/ft-server
ssh root@YOUR_SERVER_IP 'systemctl stop fasttranslation && cp -r /tmp/ft-server/* /opt/fasttranslation/ && chown -R root:ftserver /opt/fasttranslation && systemctl start fasttranslation && curl -fsS http://127.0.0.1:5080/health'
```

数据库结构变更由服务端启动时的 `ServerDatabase.Initialize()` 负责。升级前先跑一次备份 —— SQLite 的 `ALTER TABLE` 一旦写坏没有回退路径。

## 日志

```bash
journalctl -u fasttranslation -f          # 应用
journalctl -u caddy -f                    # Caddy(证书申请失败看这里)
cat /var/log/ft-backup.log                # 备份
```
