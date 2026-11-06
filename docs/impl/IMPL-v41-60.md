# Fast Translation 实施日志 · v41-60

> 从 v41 起。全文索引与「新条目该写进哪一册」的规则见 [../IMPL.md](../IMPL.md)。

## v41 自动更新:自定义安装目录不再被挪走,也不再被误判成便携版

用户 m01641 原话:「这个自动更新不允许我们选择安装路径,是全自动的,自动没关系,不能更改原先安装路径」。

**先测事实,再看代码。** 本机把 1.2.0 装进 `D:\FT-Custom-Test`,再用更新器原样的参数(`/SILENT /SUPPRESSMSGBOXES /NORESTART`,不带 `/DIR`)跑 1.2.1:

- `publish/custom-upgrade.log` 里 `Dest filename: D:\FT-Custom-Test\FastTranslation.exe`,`[Run]` 启动的也是这个目录;
- 升级后 ARP 变成 `v1.2.1 | D:\FT-Custom-Test\`,默认目录始终没有出现。

⇒ `UsePreviousAppDir=yes` 确实沿用了用户选的目录,**静默安装本身不会挪位置**。真正的缺陷在应用侧的判定:

- `src/FastTranslation.App/UpdateCoordinator.cs` 的 `IsInstalledCopy()` 把「默认目录 `%LOCALAPPDATA%\Programs\FastTranslation`」写死,别的目录一律视为便携版;
- `src/FastTranslation.App/UpdateWindow.xaml.cs` 于是在点「立即更新」时弹「安装位置会变 / 会把它装到默认位置」的确认 —— 这个说法与上面的实测相反,而且把自定义目录的用户直接推到下载页。

**修法**

- 新增 `src/FastTranslation.Platform.Windows/InstallLayout.cs`:`UninstallKey = "{8B1F2C34-7A6D-4E58-9C21-5F3D8E7A1B60}_is1"`、`ReadRegisteredDirectory()`(HKCU / HKLM × 64/32 视图读 `InstallLocation`)、`CanSelfUpdate(exeDir, registeredDir, protectedRoots)`、纯函数 `IsSameDirectory` / `IsUnder`。判据从「是不是默认目录」改成「是不是安装记录里的那个目录」;便携包没有安装记录 ⇒ 仍然降级到下载页。
- `ProgramFiles` / `ProgramFilesX86` / `System` / `Windows` 四个根视为不可自我替换 —— `PrivilegesRequired=lowest` 没有提权通道,在那里静默安装会在应用已经退出之后失败。
- `UpdateInstaller.LaunchInstaller(setupPath, pid, installDirectory, out error)` 增加 `/DIR`:静默模式看不到文件夹页,显式重复当前目录才是「不改路径」的硬保证;`BuildInstallerArguments` 独立成公开方法以便测试,含空格的路径会加引号。
- `UpdateWindow` 删掉那段与事实不符的确认;便携版的降级文案仍由 `_selfInstallable` 负责。`NoticeWindow.Confirm` 保留,只由 UI 探针分支使用(`FT_UITEST=notice-confirm` 的文案改为中性说法)。

**验证**

- 单元测试 `tests/FastTranslation.Platform.Windows.Tests/InstallLayoutTests.cs`(9 例:去尾分隔符/大小写、reject 不同目录、便携无记录、Program Files 及其兄弟目录的边界)与 `InstallerArgumentsTests.cs`(3 例);`dotnet test` 75 + **83** + 40 = **198 全绿**,`dotnet build` 0 警告 0 错误。
- 真机探针 `publish/InstallProbe`(net10.0-windows 控制台,ProjectReference 到 Platform.Windows)对着真实注册表输出:`registered = D:\FT-Custom-Test\`、`canSelfUpdate(D:\FT-Custom-Test) = True`、`canSelfUpdate(默认目录) = False`、`canSelfUpdate(publish\ft-folder) = False`、`canSelfUpdate(C:\Program Files\FastTranslation) = False`。
- 安装级验证 `publish/dir-args-test.ps1`:T1 在 `D:\FT-Custom-Test` 已装 1.2.1 的基础上用 `/DIR=D:\FT-Custom-Test` 静默升级 → 目录内变成 `1.2.2+cfe2fc6`,默认目录仍 absent;T2 全新静默安装 `/DIR="D:\FT Test Dir"`(带空格、带引号)→ 落在该目录。测试目录随后卸载删除,本机恢复为默认目录的 1.2.2。
- 顺带发现:静默安装的 `[Run]` 会把应用拉起来,脚本里 `Start-Process -Wait` 会一直等到应用退出 —— 判完成要轮询 `ProductVersion`,不能用 `-Wait`。

**顺带修掉的**`tools/qa/make-portable.ps1` 缺 BOM(`$readme` 变 null)已在 1.2.1 那次提交里补上。

## v42 发布记录:1.2.2

- 主仓库 Release `426263015`,tag `v1.2.2` → commit `8601dddbd56b718fdb3bab44a9d709194dd8e3e3`;资产 `FastTranslation-1.2.2-Setup.exe` id **683817104**(46766839 字节 / `sha256:fc29736278a635e71945e29d8766a4a40c95e9f2a97803c521f68d8e90540d3e`)、`FastTranslation-1.2.2-portable.zip` id **683817237**(61757746 字节 / `sha256:279df77f0931311d71d0deb416188ce421e8044034000f21a8d445a2b6317f7b`)。
- 更新通道 `LYW206817/FTranslation-Updates` Release `426263182`,资产 id **683817635**(同一份 Setup.exe),三个资产均为 `state: uploaded` 且 digest 与本地一致。
- `version.json` 由 blob `bf2f85dea1aab1ea1e6164665c006f1d9b9f6ffb` 更新为 commit `6ef0b2f7e8a92c519772cdb612e44b29a9e3aedf`(version 1.2.2 / size 46766839 / sha256 fc297362… / url 指向通道的 v1.2.2 资产)。
- 清单四顺位实测:gh-proxy 与 ghfast.top 的 raw 立即 1.2.2;GitHub Pages 在 600 秒缓存过期后于 12:33 变 1.2.2;**fastly.jsdelivr 到 12:40 仍是 1.2.1**("@main" 的 CDN 缓存明显更久)。因为 `UpdateChannel.ManifestUrls` 里 Pages 排第一、这两个 raw 代理排在后两位,不影响正常检查;只有「Pages 挂掉且 jsdelivr 还在缓存旧清单」这种叠加情况下才会短暂读不到新版本,属于已知限窗,遇到再考虑改成「多源取最高版本」。
- 通道资产可用性:直连 `github.com` 返回 302 后不可达;`https://gh-proxy.com/https://github.com/…/v1.2.2/FastTranslation-1.2.2-Setup.exe` 返回 206、约 138 KB/s ⇒ 下载仍然只能走代理。

## v43 账号基座:自建后端 + 客户端登录链路(阶段0)✅

**为什么这样改**

v1.0 的设计(spec:12 / spec:29 / spec:45)刻意把「账号体系、代理后端、计费」排除在外,只做 BYO Key。用户已经推给朋友用之后要「知道有多少注册用户、免费版与商用版分档、开发者与用户都能看到消耗」,这套前提就作废了 —— 决策与取舍记在 `docs/superpowers/specs/2026-10-28-account-and-billing-design.md`,其中 D5 明确把 spec:743 那条「必须先取得阿里云对代理/转售/代付的书面确认」作废(该行已加删除线)。本版只做**阶段 0 账号基座**:能注册、能登录、能看到自己的额度与用量;翻译仍走用户自己的 Key,**没有任何文本经过本机以外的服务器**,所以这一版没有合规风险,也不影响现有用户。

**后端 `server/FastTranslation.Server`(.NET 10 + SQLite,零第三方业务框架)**

- 只有两个直接依赖:`Microsoft.NET.Sdk.Web` 框架本身 + `Microsoft.Data.Sqlite` 10.0.0。不引 EF、不引 ORM、不引 Identity、不引日志框架 —— 与客户端「最小实现 + 单测」的约定保持一致。
- `Domain/Plan.cs` 是配额的单一事实源:`Free = 10_000`、`Light = 50_000`、`Standard = 200_000` 字符/月,免费档额外 `FreeDailyChars = 2_000` 字符/天,单账号 `MaxDevicesPerAccount = 3`。`Domain/QuotaPolicy.Evaluate()` 是纯函数(先查月再查日,负数直接抛),所以额度语义可以脱离数据库测边界 —— 这是后面阶段 2 网关复用的同一份判定。
- 认证不引 JWT 库:`Auth/TokenService.cs` 手写 HS256(iss/aud/sub/email/plan/iat/exp/jti),签名与 base64url 解码**全部包在 try/catch(FormatException)** 里 —— 早期版本漏了这层,畸形 token 会变成 500 而不是 401。刷新令牌是 32 字节随机值的 SHA256,**一次性消费**,重放直接 401。
- 密码 `Auth/PasswordHasher.cs` 用 PBKDF2-SHA256 / 210,000 轮,格式 `pbkdf2-sha256$iterations$salt$hash`,校验用 `CryptographicOperations.FixedTimeEquals`。
- 注册可要求邮箱验证码(`Auth/VerificationCodeService.cs`:6 位数字、`VerificationCodeMinutes = 10`、`VerificationResendSeconds = 60`、`VerificationMaxAttempts = 5`)。开发环境 `RequireEmailVerification = false`,于是 `POST /api/v1/auth/code` 回 `verification_disabled`,客户端据此把「验证码」输入行整行隐藏 —— 不要把它当成错误。
- 登录失败一律 `401 credentials_invalid`,不区分「邮箱不存在」与「密码错」,避免账号枚举。
- 限流只作用在 `/api/v1/auth` 组上且按 **RemoteIpAddress**(`Api/RateLimiting.cs`)。**配额绝不能按 IP 限** —— 校园网与公司网 NAT 会把核心用户整片误杀(设计 §7.4)。
- `Storage/AccountStore.cs` 集中全部 SQL;三张用量表:`usage_monthly(user_id, month)`、`usage_daily(device_id, day)`、`usage_events`(逐条明细,供后续账单核对)。SQLite 开 WAL + `busy_timeout=5000` + `foreign_keys=ON`。
- `Program.cs` 里 `ServerOptions` 必须用 `AddSingleton(sp => sp.GetRequiredService<IConfiguration>().GetSection(...).Get<ServerOptions>())` 取,**不能**在 `Build()` 之前从 `builder.Configuration` 直接读 —— 否则 `WebApplicationFactory` 的内存配置覆盖不到,集成测试会连到真实数据库。
- 生产环境 `Server:SigningKey` 少于 32 字节直接抛异常拒绝启动(而不是悄悄用弱 key);Development 下才回退成随机 32 字节。

**客户端**

- 全部网络代码在 `src/FastTranslation.Core/Account/`(无 WPF 依赖,可单测):`AccountApiClient` / `AccountSessionStore` / `DeviceIdentity` / `AccountTypes`。`AccountApiClient` 承诺**永不抛异常**,所有失败都映射成 `AccountErrorCode`;传输层异常(DNS/拒绝/TLS/超时)那一层是靠 `SendAsync` 返回 `null` 实现的,不是靠调用方 catch。
- **wire 必须是 camelCase**:服务器 DTO 是 `record RegisterRequest(string Email, ...)`,System.Text.Json 服务端默认**大小写敏感**,所以客户端的 `JsonOptions` 必须带 `PropertyNamingPolicy = JsonNamingPolicy.CamelCase`。漏了这一条的后果是注册/登录稳定 400,而单元测试如果只断言路径不查请求体是发现不了的 —— 所以测试里显式断言了 `"code":"123456"`(注意这个字段的 wire 名就是 `code`,不是 `verificationCode`)。
- 会话(access + refresh)存 `ISecretStore`(`AccountSessionStore.SecretName = "account-session"`,Windows 下走 DPAPI),**不进 settings.json**:refresh 令牌是 90 天凭证,不该以明文躺在设置文件里。access 令牌提前 60 秒视为过期,避免边界上刚好失效。
- `AccountCoordinator` 用 `SemaphoreSlim` 保证并发请求只会刷新一次令牌;只有 `Unauthorized`/`InvalidResponse` 才清会话登出,网络类错误保留会话等下次启动重试 —— 否则用户坐一趟地铁就被登出。
- 入口:托盘菜单「账号」(id 1006,`TrayIcon.AccountRequested`)与主窗 footer 的「账号」按钮,都打开同一个非模态 `AccountWindow`(已开则前置复用)。设置页「服务」卡片新增**账号服务地址**输入框,指向自建或测试服务器;留空走内置默认。
- 会话恢复在启动时后台跑(`RestoreAccountAsync`),失败只 `Trace` 不弹窗。

**验证**

- `dotnet build FastTranslation.slnx -c Release` → **0 警告 0 错误**;**`dotnet test` → 112 + 83 + 92 + 40 = 327 全绿**(阶段 0 之前基线 290,新增 37 个)。
- 新增测试项目 `tests/FastTranslation.Server.Tests`(92 例):`QuotaPolicyTests`(月尽/日尽/两者皆尽报月/付费无日限/刚好用尽/负数抛)、`PasswordHasherTests`(畸形串 5 种)、`TokenServiceTests`(篡改 payload / 他钥签名 / 过期 / `alg=none` / 畸形输入 6 种)、`AccountStoreTests`(真 SQLite 临时库,含设备复用、refresh 一次性与过期、验证码计数、`ReadAccountMetrics`)、`AuthEndpointTests`(17 个 HTTP 端到端,用 `WebApplicationFactory` 覆盖注册/重复邮箱/统一登录失败/伪造令牌/refresh 轮换与重放/设备上限/admin 越权/升级套餐后 20 万字符且无日限)。
- 新增 `tests/FastTranslation.Core.Tests/AccountClientTests.cs`(34 例):`StubHandler : HttpMessageHandler` 断言请求 URI、方法、body 与 `Authorization: Bearer`,并覆盖 429/401/5xx 兜底、`HttpRequestException`→Network、`<html>gateway</html>`→InvalidResponse、死服务器下的 logout 不抛。
- 真服务器 E2E(Production 环境、独立 SQLite 文件、curl 与一个临时控制台探针):`/health` ok → 注册 `plan=free quota=10000 daily=2000` → me / usage → 重复注册 `EmailTaken` → 错密码 `InvalidCredentials` → refresh 轮换成功且新 access 不同 → **重放 Unauthorized** → 垃圾令牌 Unauthorized → logout → 死主机 `Network`。探针与临时库已删除。
- UI 证据(`assets/qa/account-main.png` 980x945、`assets/qa/account-dialog.png` 770x702 / 2366 种颜色,该目录不入库):UIA 控件树含邮箱/密码输入框、去注册/登录/关闭按钮,设置页 `账号服务地址` 输入框存在。

**残留风险**

- `AccountApiClient.DefaultBaseUrl` 目前是 `http://127.0.0.1:5199`(开发默认)。**正式对外前必须换成备案后的域名并走 HTTPS**,否则用户装上去默认连不上账号服务器(翻译不受影响,仍用本地 Key)。
- 本机没有任何人登录过的情况下,托盘「账号」能打开但提交会连不上 —— 属于预期行为,文案已说明是「自定义服务地址」。
- 邮件验证码的 SMTP 通道未接(未配置时只把验证码打进日志),所以**目前不能开 `RequireEmailVerification`**;阶段 2 做免费额度前必须先接上,否则刷号无门槛(设计 §7.4 三道闸的第一道)。
- 服务器尚无 TLS 终止、无反向代理、无备份策略;数据库是单文件 WAL,适合百人级,千人级要重新评估。
- 分档与网关(阶段 2)未做:现在所有人都是免费档,额度只被**展示**,没有任何地方真正扣减 —— `usage_events` 表与 `RecordUsage` 已就位但暂时没有调用方。

## v44 学习功能:保存结果 + 单词本三视图(阶段1)✅

**为什么这样改**

阶段 0 只解决了「能注册、能看到额度」。真正让付费有理由的是「保存」:免费版给 1 万字符网关额度但**完全不能保存**(决策 D8,连本地也不存),要保存就升级 —— 保存是这一版唯一的付费闸门,所以它必须先在客户端成立,订阅校验留到阶段 2 一次加。产品也据此从「划词翻译工具」变成「英语教学工具」:划词出译文顺手存进单词本/短语本/语句本,以后再复习。设计见 `docs/superpowers/specs/2026-10-28-account-and-billing-design.md`(阶段 1 一节)。

**服务端 `/api/v1/notes`**

- 数据模型是**一张 `notes` 表 + `kind` 字段**,不是三张表:`id / user_id / kind / source_text / target_text / source_lang / target_lang / model / origin_app / note / created_at / updated_at / deleted_at`。三个「本」只是同一张表按 `kind` 过滤;索引 `ix_notes_user_kind(user_id, kind, created_at DESC)` 直接服务列表页,`ix_notes_user_live(user_id, deleted_at)` 服务计数与去重。`deleted_at` 是软删除,留给阶段 3 的跨设备同步用 —— 硬删除会让另一台设备永远不知道这条被删了。
- `Domain/NoteClassifier.cs` 的类型判定**顺序有讲究**:先判句末标点(`. ! ?`)/长度 > 80 → 语句;再判「单个 CJK 字」或「不含空白且长度 ≤ 24」→ 单词;再判词数 2~6 → 短语;其余 → 语句。最初的写法是先判单词再判语句,`Really?`(无空白、11 字符)会被存进单词本,`她安静地离开了房间。` 同理 —— 这两条已经写进单元测试当回归锚点。
- `POST /api/v1/notes/` 是**幂等**的:`FindLive(userId, kind, sourceText)` 命中就直接回既有行,不新建。滑动选词反复点保存是常态,前端不该出现重复行。单账号上限 `MaxNotesPerAccount = 20_000`,超了回 409 `notes_limit_reached`(而不是静默失败)。
- `GET` 的 `q` 走 LIKE,且**必须转义**:把 `\` → `\\`、`%` → `\%`、`_` → `\_` 再配 `ESCAPE '\\'`;否则用户搜「100%」会把整库拉出来(下划线同理,单词里很常见)。
- 所有文本入库前统一截断(源/目标 5000、备注 2000、model 64、originApp 128、语言 16),避免一条异常长文本把整行撑爆。
- 端点文件 `Api/NotesEndpoints.cs` 挂在 `RequireAuthorization()` 组下;DTO 在 `Api/Contracts.cs`(`SaveNoteRequest` / `UpdateNoteRequest` / `NoteDto` / `NotePageResponse`)。

**客户端**

- `src/FastTranslation.Core/Notes/` 与 UI 无关、可单测:`NoteTypes.cs`(`NoteKind` / `Note` / `NotePage` / `NoteDeleted`)、`NoteClassifier.cs`、`NotesApiClient.cs`。类型判定在客户端**再实现一遍**是有意的 —— 卡片点保存要立刻显示「已保存到单词本」,不能等服务器回话;服务器仍是权威(不带 `kind` 时由它分类)。
- `Note` 的备注属性叫 **`Annotation`** 而不是 `Note`:叫 `Note` 会撞 C# 的 CS0542「成员名不能与其封闭类型同名」。
- `NoteDeleted` 这个只有 `bool Deleted` 的单字段 record 是被泛型逼出来的:`WithSessionAsync<T>` 有 `where T : class` 约束,`AccountApiResult<bool>` 编译不过。
- `AccountCoordinator` 新增四个 notes 包装方法,全部走既有的 `WithSessionAsync`:access 令牌过期时**自动刷新并重试一次**,词本里点删除不会因为半小时没动就报「未登录」。构造参数加了 `string? deviceFingerprint = null, NotesApiClient? notes = null`,不传 notes 时四个方法统一回 `NotSignedIn`,不给调用方留 null 分支。
- 结果卡(`src/FastTranslation.Platform.Windows/OverlayWindows.cs`)加第 4 个按钮「保存」,排在「插入译文」之前。它**始终可见**(不像替换/插入要看选区是否可写),因为「不能保存」本身就是要说给免费用户听的话:未登录点它 → 卡片切一行提示「先在账号里登录,保存的单词才能跨设备同步」并打开账号窗;已登录 → `SaveNoteAsync` 成功后 `MarkSaved()` 把按钮改成「已保存」并禁用(防连点),同时 `ShowNotice("已保存到语句本")`。
- `origin_app` 取 `ForegroundInfo.ProcessName()`(进程名,不记窗口标题) —— 窗口标题常常就是文档名甚至聊天内容,记下来等于偷偷采集用户数据。
- `NotesWindow`(新建)是 820x560 的非模态窗:顶部搜索框(320ms 防抖)+刷新,左侧三列「单词/短语/语句」书架,中间列表,右侧详情(原文/译文/改类型/备注/保存备注/删除/来源行)。改类型会换架,所以改完清空选中再重新拉列表。
- 入口两处:托盘菜单「单词本」(id 1007,`TrayIcon.NotesRequested`)与主窗 footer 的「单词本」按钮,已开则前置复用。

**验证**

- `dotnet build FastTranslation.slnx -c Release` → **0 警告 0 错误**;`dotnet test` → **133 + 83 + 40 + 109 = 365 全绿**(阶段 0 基线 327,新增 38 个)。新增:`NoteClassifierTests`(14)、`NotesApiClientTests`(7,用 `StubHandler` 断言 method/URI/body)、`NotesEndpointTests`(17,含跨账号隔离与幂等)。
- 真服务器 E2E(`ASPNETCORE_ENVIRONMENT=Development`、`http://127.0.0.1:5199`、账号 `e2e-stage1@example.com`):三条文本分别被自动判定为 word(`meticulous`)、sentence(`She left the room quietly.`)、phrase(`in the long run`);`GET /api/v1/notes/?kind=word` 只回 word 且 `total:3`;重复保存返回**同一个 id**,幂等成立。
- UI 证据(`assets/qa/`,不入库):`notes-card-result.png` 735x266 显示结果卡按钮为「保存 / 复制」;`notes-card-saved.png` 735x159 显示点击后卡片变成 **「已保存到语句本」**,证明「卡片 → OnSaveRequested → 服务器 → MarkSaved」整条链路通;`notes-window-word/-phrase/-sentence.png` 1435x980 三视图 + `notes-window-detail.png` 详情面板(含类型下拉、备注框、删除);`notes-main-with-button.png` 主窗 footer 出现「单词本」。

**残留风险**

- **免费版现在也能保存** —— 阶段 1 明确不做订阅闸门,所有登录用户都能存。阶段 2 加闸门时必须把已有数据算成「存量」(老数据只读或给宽限),否则会一夜之间让已存了几百条的用户点不开自己的本子。
- 列表一次拉 200 条、无分页游标;`total` 是账号总条数,翻页要等阶段 3 做同步时一起补。
- 单词本没有离线缓存,断网就是空列表;用户会以为是「数据丢了」。要等阶段 3 的本地镜像。
- 保存失败只提示「保存没成功,稍后再试一次」,没有区分网络与服务器错误 —— 现在够用,但用户量上来后需要能自查的文案。
- `origin_app` 依赖前台进程名,`ForegroundInfo.ProcessName()` 失败时为空串,数据里会出现空来源。
## v45 账号变现:开发者网关 + 额度闸门 + 订阅闸门(阶段2)✅

把「免费额度」从每个用户自带 Key 改成服务端用开发者自己的 Key 转发并记账,同时让「保存到单词本」成为第一个付费闸门。用户原话:*「给的免费额度直接走我的 API key 吧,给他们额度限制就行」* —— 不办执照、不接支付、不做手机号,付费靠激活码手动开通。

**为什么这样改**

- 以前免费用户想用就得自己申请百炼 Key,这是最大的流失点;开发者 Key 只有几十块成本,直接把门槛换成额度更划算。
- 记账必须落在服务端,客户端不可能诚实上报用量。所以网关是唯一入口:`POST /v1/chat/completions` 与 `POST /v1/image`。
- 额度耗尽时**不能报错卡住用户**:客户端要静默回落到用户自己的 Key,并在结果卡上说明一句。所以服务端返回 **429(不是 403)**,响应体 `{"error":"monthly_quota_exhausted"}` 加响应头 `X-FT-Quota-Reason` —— 403 会被客户端读成「账号被停用」。
- 闸门放在保存接口的**最开头**:若放到幂等查询之后,免费账号就能通过「这条是否已存在」的返回值探知自己的数据,闸门等于没关。

**服务端**

- `GatewayOptions.cs`(项目根,不在 `Gateway/` 下)绑定 `Server:Gateway`:`Enabled`、`ApiKey`、`BaseUrl`、`ImageBaseUrl`、`Model`、`TimeoutSeconds=60`、`DailyBudgetMicros=5_000_000`(全站日预算 5 元)、`RequestsPerMinute=40`、`MaxRequestChars=5_000`、`ImageQuotaChars=1_000`;配置项全部在 `appsettings.json` 补了默认值。
- `Gateway/GatewayEndpoints.cs`:`/v1` 组 `RequireAuthorization()`。聊天链路:503 `gateway_disabled` → 400 `text_required` → 413 `text_too_long` → 四道闸 → 转发 → 记账 → 原样返回上游体。**上游非 2xx 原样转发状态码且不记账**。四道闸顺序是 `rate_limited`(429) → `budget_exhausted`(429) → `account_disabled`(403) → 额度(429 + `X-FT-Quota-Reason`)。
- `Gateway/DashScopeGateway.cs`:把客户端令牌**换成**开发者 Key 再上行,客户端令牌永远不出现在上游请求里(有测试断言这一点)。
- token 记账:上游报了 `usage` 就用真实值,没报就按 `chars + 45` 估算输入(45 是 Qwen-MT 每次的固定前言),输出按 0。图片接口不报 usage,按 `ImageQuotaChars` 折成等效字符额度记账。
- `Domain/Pricing.cs` 把 token 换成微元成本存进 `usage_events.cost_micros`,全站日预算是对这些成本求和。
- `Domain/Entitlement.cs`:`plan_expires_at IS NULL` = 永久授权(手工开通不会因为没填到期时间就被锁死)。`EffectiveTier` 让**过期只降额度、不断服务**。
- 订阅闸门:`NotesEndpoints.Save` 一开始就判 `Entitlement.AllowsNotebook`,拒绝时 403 `subscription_required`。读接口永远不加闸。
- 管理面:激活码还没做,当前用 `POST /api/v1/admin/accounts/{email}/plan` 手动开通(`days` 是相对天数,0 表示永久)。

**客户端**

- `Core/TranslationTypes.cs` 新增 `TranslationErrorCode.QuotaExhausted`(刻意与 `RateLimited` 分开,组合 provider 靠它决定要不要回落)、`TokenUsage`、`TranslationResult.Usage` 与 `Notice`。
- `Providers.QwenMt/QwenMtProvider.cs`:`QwenMtOptions.ExtraHeaders` 让同一个类同时服务 DashScope 直连与网关(网关要 `X-FT-Device`,DashScope 忽略未知头);失败分支先读 body 再看 `QuotaRefusal.Describe`,命中即 `QuotaExhausted` 且**不重试**(额度用完重试没意义,还会白白多一次请求)。
- `GatewayTranslationProvider` / `GatewayImageTranslationProvider`:先走网关,成功即返回;网关返回 null(未登录/开关关)就只用自有 Key;网关拒绝时回落自有 Key,并在结果上写一句 `Notice` —— 用户看到的是「本月额度已用完,这次用了你自己的 Key」,而不是一条报错。
- `AccountCoordinator` 暴露 `DeviceFingerprint` 与 `GetAccessTokenAsync`(距过期 1 分钟内先刷新),网关请求因此永远带新鲜令牌与**和登录时同一个**设备指纹。
- 结果卡状态行(`OverlayWindows.StatusLine`)从「模型 · 耗时」扩成「模型 · 耗时 · 缓存 · N tokens · 一句说明」,并给 `_statusBlock` 加 `TextWrapping`,否则 420 宽度会把说明截掉。
- 保存被闸门拒绝时,卡片先说明原因再打开账号窗,不再只弹一行文案。

**验证**

- `dotnet build FastTranslation.slnx -c Release` → **0 警告 0 错误**;`dotnet test` → **138 + 87 + 58 + 125 = 408 全绿**(阶段 1 基线 365,本阶段新增 43)。
- 真服务器 E2E(`ASPNETCORE_ENVIRONMENT=Development`、`http://127.0.0.1:5199`、上游用本地桩,7 项全通过):
  1. 免费账号经网关翻译成功,且**上游收到的是开发者 Key 而不是客户端令牌**;
  2. 用量按 100 字符记到月/日两张表;
  3. 免费账号保存 → 403 `subscription_required`;
  4. 管理接口开 30 天轻享后,同一条保存 → 200 且能 round-trip;
  5. 升级后账号的 `quotaDaily` 变 null(不再有日限),继续翻译正常;
  6. 另一个免费账号花完 2,000 日额度 → **429 `daily_quota_exhausted` + `X-FT-Quota-Reason`**;
  7. `usage_events` 保留上游报的 60/8 token,成本 **58 微元/次 ≈ 0.0097 元/人/月**(设计预估 0.035,实测更低)。
- E2E 抓到一个真实缺陷并已修:网关把日额度记在**设备**上,但 `GET /api/v1/account/usage` 只认 `deviceId`,客户端只有指纹 → 账号窗「今日用量」恒为 0,而用户其实已被限流。现在该接口也接受 `fingerprint`(解析成 deviceId),`AccountApiClient.GetUsageAsync` 带上指纹,补了两侧测试。
- UI 证据(`assets/qa/`,不入库):`card-fallback.png` 783x276,UIA 文本为 `qwen-mt-flash · 486ms · 65 tokens · 本月额度已用完,这次用了你自己的 Key`,证明用量与回落说明都能在 420 宽度下换行显示;取图脚本 `tools/qa/shot-card-fallback.ps1`(新增 `FT_UITEST=overlays-fallback` 钩子)。

**残留风险**

- **存量免费用户现在能保存,明天就不能了**:阶段 1 没有闸门,已经存了数据的账号升级后会看到「需要付费」。上线前必须决定老数据是只读还是给宽限,否则是数据层面的破坏性变更。
- 全站日预算 5 元是**止损开关,不是防刷**:被刷爆的后果是所有人一起回落到自带 Key(还算体面),但当天免费体验就没了。没有 IP/设备级限速,只有每账号 40 请求/分钟。**语音功能评估时发现的连带问题**:语音也要花钱且音频按秒连续计,5 元/日全站熔断只够 5.8 小时语音,且熔断会连文本翻译一起停 → 语音必须拆独立预算桶 `Gateway:Speech:DailyBudgetMicros`(见 `docs/superpowers/specs/2026-10-31-语音与文档翻译-方案评估.md` §3.5、§6.2)。
- 激活码还没做,`plan_expires_at` 只能靠管理接口手填;真开卖前必须补 `POST /api/v1/notes` 之外的兑换链路,否则每来一个用户都要手工改库。
- 图片翻译按 `ImageQuotaChars=1000` 折字符记账,与真实成本(约 0.004 元/张)只是粗略对应;截图翻译量大时会低估消耗。
- 网关让**所有**登录用户的翻译都经过服务器,~~香港国际型线路对内地用户有延迟与丢包(设计 §2.4 已记)~~ **该顾虑已作废**:实际部署在**阿里云华东1 杭州**(境内),实测 `ping YOUR_SERVER_IP` 24ms / 0% 丢包。真正的容量约束改成**出向 3 Mbps 带宽**(入向不限,`docs/IMPL.md:1125` 实测),纯文本翻译无压力,但流式中继(语音)约 11 路并发就到顶(见 `docs/superpowers/specs/2026-10-31-语音与文档翻译-方案评估.md` §3.5);单机 ASP.NET 没有水平扩展方案,用户量上来要先解决这个。
- `GatewayOptions` 在项目根而不是 `Gateway/` 目录下,是历史遗留位置;找它时别只在子目录里搜。
- 设计 §5 原本要求「保存按钮灰掉并给订阅入口」,实际做的是**不置灰**:订阅/激活码页面还不存在,置灰只会让免费用户点了没反应又不知道为什么。等激活码链路做完再改成置灰 + 引导按钮。
## v46 官网:产品介绍静态单页(`site/`)✅

给 Fast Translation 做一个能对外说明产品的网页。用户原话:*「对了,我们肯定是要做个网站介绍产品的」*。选静态单页而不是前端工程:全站只有一个页面、没有任何数据交互,引入构建链只会增加发布步骤。

**为什么这样写**

- 首屏必须直接展示产品本体,不是讲道理。所以头图不是截图,而是用 CSS 1:1 重建的「浮动工具条 + 结果卡」:选中段落里一段蓝底文字、下面压着三个按钮、再下面是结果卡。好处是不依赖截图分辨率、深色模式自动跟着翻,也不需要维护两套图。
- 全站只有一个强调色(选区蓝 #2562FF),底色用纸白 #FDFCF8、正文用衬线,标题用无衬线 —— 因为产品做的事就是「在别人的文字上叠一层译文」,页面本身就该像一份双语评注本。
- 套餐只在页面上写价格与额度,不写「点击购买」。现在没有支付通道,也没有兑换入口,写上去就是假承诺。三张卡的按钮一律指向「下载」。

**页面结构**

- `site/index.html`(286 行)→ `site/assets/site.css`(14 KB)→ `site/assets/img/`(6 张 PNG,含 `og.png` 分享图)。章节:首屏 / 01 交互三按钮 / 02 界面四张截图 / 03 实测三个数字 / 04 能做与不做 / 05 套餐 / 06 下载 / 页脚。
- 02 的截图全部是真实运行截图(`assets/qa/` 里的同源文件),不是设计稿。
- 03 的三个数字来自 v45 的真机实测:531 毫秒、65 token、3 台设备上限。

**踩到的坑**

- **Headless Chrome 有最小窗口宽度**:`--window-size=390` 实际渲染出 clientWidth=518,所有「移动端」截图其实是被裁切的桌面视图。要测真实窄宽,得写一个外层页面用 `<iframe style="width:390px">` 包住站点再用 `--window-size=420,…` 截图。临时文件用完即删。
- **深色模式下卡片白底**:早先把卡片背景硬编码成 `#fff`,深色变量只翻文字颜色,于是浅字压白底几乎看不清。修法是引入 `--surface`(浅色 #FFFFFF / 深色 #15181D),所有卡片背景改用它;蓝底按钮保持白字。
- 结果卡按钮在 390px 下被逐字折行(「保存」被拆成两行),给按钮与状态行加 `white-space: nowrap`、给按钮行加 `flex-wrap: wrap`,窄宽下状态行独占一行。
- 套餐卡要「按钮压到卡底对齐」,得让 `.plan` 成为纵向 flex 并把 `ul` 设 `flex: 1 1 auto`;否则三张卡按钮高度错位。顺带 `.plan .tag` 必须 `align-self: flex-start`,不然「推荐」标签会被拉成整条色带。

**验证**

- 浅色 / 深色 / 1440 宽 / 390 宽四套截图逐段看过,无溢出、无错位:首屏演示、01 三卡、02 四图、03 数字、04 双栏、05 三卡 + 按钮、06 两卡 + 页脚。
- 深色预览的做法:复制到 `publish/sitedark/` 把 `@media (prefers-color-scheme: dark)` 换成 `@media all` 再截图(不污染源文件)。
- 标签平衡核对:`<div>` 55/55、`<section>` 6/6、`<a>` 12/12;五个锚点 `#how/#look/#numbers/#plans/#download` 都存在。
- 页面高度 1440 宽下 9184 px,深色与浅色一致。

**残留风险**

- **站点在备案通过前不能对外提供访问**,现在只能本地开 `index.html` 预览。要上线得先有域名、内地服务器、备案号,再把备案号写进页脚(现在页脚没有备案号占位)。
- `og:image` 用的是相对路径 `assets/img/og.png`;微信、Twitter 等抓取器普遍要求绝对 URL,拿到域名后要改成 `https://<域名>/assets/img/og.png`。
- 下载按钮直接指向 GitHub Releases 的 `latest`。国内访问 GitHub 不稳定,真要推广时得换成自己服务器上的文件或对象存储。
- 页面上的安装包名写死成 `FastTranslation-1.2.2-Setup.exe`,发版时必须跟着改(两处:安装卡与便携卡)。

## v47 部署:一台 ECS 上的 systemd + Caddy + SQLite ✅

**为什么这样改**

阶段 2 的网关和账号服务已经能跑,但它只在 `dotnet run` 里活过。要真上线还缺三件事:进程怎么常驻、HTTPS 证书从哪来、数据库文件放哪。这三件事写成一个可重复执行的 `deploy/` 目录,而不是一份手抄命令清单 —— 因为部署一定会做第二遍(换机器、换域名、升级)。

选型上有几个刻意的决定:

- **不引 Docker**。2G 内存的机器上再养一层容器运行时,runtime 镜像本身就要 200MB+,而且 SQLite 文件挂载出去比直接放 `/var/lib` 更容易出权限问题。systemd 单元 + 一个自包含二进制已经够了。
- **Kestrel 只监听 `127.0.0.1:5080`**。证书与 80/443 全部交给 Caddy,应用进程不碰端口,也就不需要 `CAP_NET_BIND_SERVICE`。
- **Caddy 而不是 nginx**。Caddy 自己申请并续期 Let's Encrypt 证书,nginx 还要配 certbot 的 timer 和 reload hook。项目里已经有一个「能少一个组件就少一个」的取向。
- **占位符没替换就不启用 Caddy**。`__DOMAIN__` 还在的时候强行 `systemctl start caddy` 只会每 30 秒重试一次 ACME 然后失败,日志里全是噪音。所以 `install.sh` 用 `grep` 检查占位符,还在就 `systemctl disable --now caddy` 并打印提示。

**服务端**

没有改服务端代码。部署文件依赖的既有事实:

- `Server:SigningKey` 在非 Development 环境下缺失或不足 32 字节会直接启动失败(`server/FastTranslation.Server/Program.cs:89-100`)。这是好事 —— 一个没配密钥的实例不应该安静地起来。
- `/health` 返回 `{"status":"ok"}`(`Program.cs:57`),`install.sh` 装完就用它做健康检查。
- 数据库路径支持绝对路径(`Program.cs:29-31`),所以单元里直接用 `Server__DatabasePath=/var/lib/fasttranslation/fasttranslation.db` 覆盖,不用改 `appsettings.json`。

`deploy/` 七个文件:

| 文件 | 作用 |
| --- | --- |
| `build-server.sh` | 本机执行,`dotnet publish -r linux-x64 --self-contained` 到 `publish/server-linux` |
| `bootstrap.sh` | 服务器上跑一次:2G swap、`ftserver` 系统账号、四个目录、apt 依赖、Caddy 官方源、libicu 检查 |
| `install.sh` | 幂等主安装:二进制、systemd 单元、静态站点、备份任务、Caddyfile |
| `fasttranslation.service` | `ProtectSystem=strict` + `ReadWritePaths=/var/lib/fasttranslation`,除数据库目录外整机只读 |
| `Caddyfile` | 静态站点 + `/api/*`、`/v1/*`、`/health` 反代,两处占位符 |
| `appsettings.Production.json.example` | 只列必须改的三项(SigningKey / AdminToken / Gateway:ApiKey) |
| `backup.sh` + `fasttranslation-backup.cron` | 每天 04:17 `sqlite3 .backup` + gzip,保留 14 天 |

备份脚本用 `sqlite3 $"$DB" ".backup ..."` 而不是 `cp`:数据库开着 WAL,直接拷主文件会漏掉 `-wal` 里还没 checkpoint 的事务,恢复出来是旧的。

**验证**

- `bash deploy/build-server.sh` → `publish/server-linux/` 341 个文件,二进制 78192 字节。产物里**没有** `appsettings.Production.json` —— 这是刻意的,生产配置不进发布产物。
- `bash -n` 四个脚本全部通过;`grep -c '\r'` 为 0,纯 LF(在 Windows 上写 shell 脚本最容易出的问题就是 CRLF,`bash` 会报 `$'\r': command not found`)。
- `caddy fmt --overwrite` 之后 `caddy adapt` 退出码 0,展开出的 JSON 确认了四件事:`admin` 已禁用、`/assets/*` 拿到一年期 immutable、`/api/*` 与 `/v1/*` 与 `/health` 三条都落到 `127.0.0.1:5080`、兜底是 `file_server` + `try_files {path} /index.html`。
- 服务器侧尚未实测:公网 IP `YOUR_SERVER_IP` 的 22 端口已通,80/443 仍关闭(安全组未开、Caddy 未装),与「备案通过前不对外」的预期一致。

**残留风险**

- **整套流程没有在真机上跑过一遍**。`bootstrap.sh` 里的 cloudsmith 源、`libicu` 包名、`systemd` 的加固指令都只做过语法检查;第一次跑大概率要修一两处。
- 3 Mbps 固定带宽意味着单个客户端下载安装包最快约 375 KB/s。安装包 60MB 左右,即 2~3 分钟。真要放安装包得另外接对象存储。
- 没有配置日志轮转。`journalctl` 会自己滚,但 `/var/log/ft-backup.log` 会一直长(一天一行,可忽略)。
- 备份只有本机一份。云盘挂了备份跟着挂,真要防灾得 `scp` 到别处。
- `install.sh` 的静态站点同步是 `rm -rf` 再拷 —— 如果 `WEB_DIR` 被误设成 `/var/www`,会连别的站点一起删。脚本里用了 ${WEB_DIR:?},变量为空时直接退出,但这个保护只在变量未设置时生效。
## v48 仓库瘦身:清掉过程文件与构建缓存(2026-10-29)✅

**为什么这样改**

仓库从 v1 长到 v47,攒下两类东西:每个历史版本的发布产物、几百个一次性探针脚本。它们都不在版本控制里(`.gitignore` 早就覆盖了 `publish/`、`assets/qa/`、`[Bb]in/`、`[Oo]bj/`),但会持续放大磁盘占用与备份体积,也让全仓 grep 变慢。这次做一次性盘点,顺便把「哪些东西其实没人引用」这件事查实。

三个刻意的决定:

- **只删在远端有备份的历史包**。9 个 `portable.zip` 与 7 个 `Setup.exe` 在 GitHub Release 里都有同名资产,本地副本零信息增量。反过来,1.2.2 的两个包必须留 —— 它们是对外分发的最新版本。
- **探针工程从仓库里删,而不是继续留在库里**。`tools/Probe`、`ProbeBare`、`ProbeBare8`、`ProbeWf`、`E2EKeySmoke` 自 v22 基线入库后再未改动、不在 `FastTranslation.slnx` 里、`docs/IMPL.md` 零引用。留着只会让后来者以为它们还在用。
- **`assets/qa` 只留被 IMPL.md 引用的 10 张**。整个目录被忽略,不设边界的话「截图即证据」这个说法就没有落点。删前逐个比对 `docs/IMPL.md` 里出现的文件名,保留的 10 个在 IMPL.md 中各命中 1 次。

**删了什么**

| 范围 | 删除前 | 删除后 |
| --- | --- | --- |
| `publish/` 全部 | 4157 文件 / 2591 MB | 992 文件 / 240.32 MiB |
| ├ `publish/dist` 历史包 | 30 文件 / 947.4 MB(9 个 portable.zip、7 个 Setup.exe、2 个旧 MSI + wixpdb、9 个安装日志) | 3 文件 / 103.5 MB(仅 1.2.2 的 Setup.exe + portable.zip + README.txt) |
| ├ 旧便携目录 | 8 个 × 263 文件,各约 134 MB | 0(只留 `ft-folder`,262 文件) |
| ├ 一次性探针产物 | `probe/` 41 文件 91.4 MB、`caddy/` 79.1 MB、`server-linux/` 106.4 MB、`tmp/`、`probeout/`、`inno7.exe` 13.6 MB、`innosetup.exe`、`speed.bin` 3.8 MB | 全删 |
| ├ 散装脚本与日志 | 根目录 257 个文件 19.85 MB(78 个 .ps1 探针、60 个 .py、56 个 .iss、20 个 .log、19 个发布载荷 json、9 个已并入 IMPL 的 md 分片、7 张 qa png) | 17 个文件(9 个按名保留的脚本 + `chm6.chm` + 7 张 `qa-notice-*.png`) |
| ├ `img/` + `art/` | 37 文件 / 1,357,737 B(MDN 抓取素材、诊断脚本、badge png) | 全删(全仓 0 引用) |
| 全部 `bin/obj` | 22 个目录 / 519,622,530 B(495.55 MiB) | 0 / 0 |
| `assets/qa` | 190 png / 26,752,819 B(25.51 MiB) | 10 png / 2,166,795 B(2.07 MiB) |
| `tools/` 探针工程 | 45 个被跟踪文件 | 0(`tools/` 下只剩 `qa/`) |
| 陈旧 worktree `D:/ft-rel-113` | 537 MB(detached HEAD `a8dcf5b`) | 已 `git worktree remove --force` |
| `.git` 对象库 | 404 文件 / 10.2 MB,`refs/original/` 残留 filter-branch 备份 | 34 文件 / 8.82 MiB,`size-pack: 8.75 MiB` |

合计:仓库根 **约 3.13 GB / 约 7970 文件 → 262.60 MiB / 1317 文件**,释放约 2.87 GB;另有仓库外的 537 MB 陈旧 worktree 一并移除。

**保留清单(逐项 EXISTS 校验)**

`publish/` 下按名被引用的都留住了:`chmread.py` + `chm6/`(AGENTS.md 指定用它读 Inno 帮助)、`poll-manifest.sh` + `poll-manifest.ps1`、`shot-pages4.ps1`、`e2e-shot.ps1`、`inno-install.ps1`、`dir-args-test.ps1`、`dl-inno.sh`、`final-check.sh`、7 张 `qa-notice-*.png`、`CardProbe/`、`InstallProbe/`。`ft-folder/` **262 个文件**原样保留 —— 它是 `installer/FastTranslation.iss:81` 的 `Source: "..\\publish\\ft-folder\\*"` 输入,删了就打不出安装包。

**验证**

- 删净 `bin/obj` 后 `dotnet build FastTranslation.slnx -c Release`:**已成功生成 / 0 个警告 / 0 个错误**,10.59 秒 —— 证明 `obj` 里没有任何不可重建的状态。
- `dotnet test`:4 个项目 **408 全绿**,失败 0 跳过 0(Core 138 / QwenMt 58 / Platform.Windows 87 / Server 125)。
- `git fsck --no-progress` 退出码 0,无 corrupt / missing,只剩 11 个 dangling blob。
- `git worktree list` 只剩 `D:/FTranslation  cb22e9c [master]`。
- `git status --porcelain` 只剩 `.agents/design` 下他人进行中的 4 条,没有任何 `publish/` 残留。

**残留风险**

- `publish/` 仍有 240.32 MiB,主体是 `ft-folder`(262 文件)与 dist 的 1.2.2 两个包。这是打包流水线的输入与输出,**不是可回收空间**;真要再瘦只能改流水线成「按需 publish」。
- 1.0.x–1.2.1 的二进制现在只存在于 GitHub Release。哪天误删 Release,这些版本的产物就找不回来了。
- `assets/qa` 里被删的 180 张属于不存在于任何文档的调试截图,不可恢复;但 IMPL.md 引用的 10 张都在,证据链完整。
- `bin/obj` 会在下次 `dotnet build` 时重新长出来(实测重建 18 个目录 / 97.25 MB),这是正常构建产物,不是垃圾。


## v49 商业闭环:激活码兑换(`POST /api/v1/redeem`)✅

**为什么这样改**

阶段 2 把「免费额度 + 开发者网关 + 订阅闸门」做完之后,服务端的付费能力是齐的——但没有任何一条路径能让用户**自己**变成付费用户:只能靠管理员拿 `X-Admin-Token` 调 `POST /api/v1/admin/accounts/{email}/plan` 手动改套餐。官网(阶段 v46)已经写着「付费套餐用激活码开通」,而 `/api/v1/redeem` 在服务端是零实现,这句话是空的。

三个刻意的决定:

- **先发码、后绑定支付,而不是等接支付再开闸**。码本身就是「已付款」的凭证,从哪卖出去(面包多、淘宝、微信收款)是运营问题,不是代码问题。把码的生成、兑换、幂等、降级保护做扎实,支付渠道以后接什么都不会改这一层。
- **码用 Crockford base32,不是 GUID 或 UUID 的变形**。16 个字符 × 5 bit = 80 bit 熵,猜不出来;字母表去掉 I/L/O/U 且兑换时把 O→0、I/L→1、U→V 折叠,所以用户抄错、看错、手打错的概率被压到很低——`Redeem_AcceptsWhatAHumanRetypes` 测的就是「把横线去掉、前缀截掉、全部小写」还能兑。
- **降级保护放在 claim 之前**。花 99 元买了标准版的人,手里剩一张轻享码,如果兑换流程先 `UPDATE redeem_codes` 再判套餐,码就废了、用户还降级。现在的顺序是:先判「已经是更贵的有效订阅」→ 直接 400 `redeem_plan_downgrade`,码一个字节都不动,可以送给别人。

**做了什么**

服务端(全部落在 `server/FastTranslation.Server/`):

| 文件 | 内容 |
| --- | --- |
| `Domain/RedeemCode.cs`(新) | `Generate()` 用 `RandomNumberGenerator.Fill` 取 16 字节,每字节 `& 31` 索引 32 字符字母表(256 是 32 的整数倍,所以分布均匀);`Format()` 产出 `FT-XXXX-XXXX-XXXX-XXXX`(22 字符);`Normalize()` 折叠歧义字母并校验长度 |
| `Storage/RedeemStore.cs`(新) | `CreateCodes` 事务批量插入;`Find`;`TryClaim` = `UPDATE … WHERE code=$code AND redeemed_at IS NULL`,靠 WHERE 实现「只能用一次」而不需要显式锁;`ReadStats` |
| `Storage/ServerDatabase.cs` | Schema 加 `redeem_codes` 表(`code` 主键、`plan`、`days`、`note`、`created_at`、`redeemed_by` 外键 `ON DELETE SET NULL`、`redeemed_at`)与 `ix_redeem_codes_redeemed` |
| `Api/RedeemEndpoints.cs`(新) | `POST /api/v1/redeem`(需登录,与 auth 共用按 IP 的限流):归一化 → 账号健康 → 码存在 → 未兑换 → 不降级 → `TryClaim` → 算到期 → `SetPlan` → 返回新 `AccountDto`+`UsageDto` |
| `Api/AccountEndpoints.cs` | 管理端 `POST /api/v1/admin/codes`(plan/days/count 三重校验,一次最多 200 张) |
| `Api/Contracts.cs` | `RedeemRequest/RedeemResponse/CreateCodesRequest/CreateCodesResponse` |

到期时间的两条规则:`stacks = 同档位 && 现有到期时间还没过`,同档则往后**续**,不同档从**现在**起算。这样「标准版还有 10 天,再买一张标准版码」= 40 天,而「轻享升标准」不会把那 10 天送出去。

客户端:

- `src/FastTranslation.Core/Account/AccountTypes.cs`:三个错误码 `CodeInvalid`/`CodeUsed`/`PlanDowngrade` 与中文文案;`AccountProfile` 增加 `PlanExpiresAtUnix`(末位带默认值,旧调用点无需改)与派生属性 `ExpiryLine`;新增 `AccountRedeem`
- `src/FastTranslation.Core/Account/AccountApiClient.cs`:`RedeemAsync`;`MapFailure` 认三个 `redeem_*` 错误码。**注意错误码刻意带 `redeem_` 前缀**——`code_invalid` 这个名字已经被邮箱验证码占用了,不加前缀会在用户眼里变成「验证码不对」
- `src/FastTranslation.App/AccountCoordinator.cs`:`RedeemAsync` 走既有的 `WithSessionAsync`(自带 401 自动刷新),成功后就地替换 `Profile` 并写回 `AccountSessionStore`,不重启窗口也能看到新套餐
- `src/FastTranslation.App/AccountWindow.xaml(.cs)`:账号窗新增「激活码」卡片(输入框 + 兑换按钮 + 一行提示),套餐行末尾追加 `· 有效期至 2026-11-28`

**验证**

- `dotnet build FastTranslation.slnx -c Release`:**0 警告 0 错误**
- `dotnet test`:**439 全绿**(Core 143 / QwenMt 58 / Platform.Windows 87 / Server 151),本次 +31:服务端 8 个兑换端点测试 + 13 个码格式测试,客户端 3 个
- 真实服务器端到端(Development、`http://127.0.0.1:5199`、`Server__AdminToken=dev-admin`):注册免费账号 → 管理端发一张标准版 30 天码 `FT-T8KW-ETEH-G3FP-V5C0` → 应用内兑换 → `GET /api/v1/account/me` 变 `standard`、`quotaDaily` 变 `null`、`planExpiresAt` = 30 天后
- 界面证据(UI Automation 驱动真实 exe,不是静态稿):`assets/qa/redeem-account-before.png`(770×936,免费版 + 每日 2,000 行 + 激活码卡)与 `assets/qa/redeem-account-after.png`(770×823,标准版 · 有效期至 2026-11-28,「今日用量」整行消失,提示「已开通 标准版。」)。脚本 `tools/qa/shot-redeem.ps1` 可复现

**残留风险**

- **发码只回一次响应,库里没有「未兑换码清单」查询接口**。管理端现在只能「生成 → 自己复制保存」,重跑一次就拿不到了。真要运营,得在 `RedeemStore` 上加按 `redeemed_at IS NULL` 过滤的列表端点。
- **码没有有效期**。`redeem_codes` 只有 `created_at`,没有 `expires_at`——一张两年前发出去的码今天仍然能兑。这是刻意的(不惩罚用户屯码),但意味着没有「作废过期码」的手段。
- **码没绑订单,理论可转卖/泄露**。任何拿到 22 字符的人都能兑,赎回形态无法追溯来源。当前靠「一次性 + 80 bit 熵」兜底。
- **兑换与登录共用按 IP 的限流(默认 20 次/分)**。同一 NAT(学校、公司、网吧)下十几个人同时兑换会撞限流,报的是 `rate_limited` 而不是更具体的提示。

## v50 上线准备:域名定为 `ftranslation.cn`,备案号留槽 ✅

**为什么这样改**:备案要等 1~20 天,而这段时间里凡是「只差一个字符串」的改动都该先落地并验证完。否则备案号下来那天,还要临时改四处、临时验证四遍——把等待期变成准备期。

**改了什么**

| 文件 | 改动 | 为什么 |
|---|---|---|
| `deploy/Caddyfile` | 站点键改成 `__DOMAIN__ www.__DOMAIN__` | 一个 block 服务两个主机名,一张证书覆盖两条;占位符出现两次,一条 sed 同时替换 |
| `deploy/Caddyfile` 头注释 | 写明真实域名与可直接复制的 sed 命令 | 部署时不用回来翻聊天记录确认域名 |
| `site/index.html` | `og:image` 改绝对 URL,新增 `rel=canonical` | 微信/QQ 抓 `og:image` 不解析相对路径,分享出去没有缩略图 |
| `site/index.html` 页脚 | 新增 `__ICP_NUMBER__`,链到 `https://beian.miit.gov.cn/` | 境内网站底部必须展示备案号并可点查 |
| `deploy/install.sh` | 页脚仍含占位符时打印警告 | 防止备案号忘了替换就对外 |
| `deploy/README.md` | 真实 sed 命令 + 新增「页脚的备案号」小节 | 同上 |

**刻意没改的一处**:`src/FastTranslation.Core/Account/AccountApiClient.cs:18` 的 `DefaultBaseUrl` 仍是 `http://127.0.0.1:5199`。服务器还没部署、备案还没通过,现在改成线上域名会让本机全部 E2E 与 `dotnet run --project server/FastTranslation.Server` 直接连不上。它属于**第一次正式发版**那个批次,不是现在。

**验证**

- `caddy adapt`(v2.10.2,占位符替换为真实域名后)退出码 **0**;展开 JSON 的 `apps.tls.automation.policies[0].subjects` = `["ftranslation.cn", "www.ftranslation.cn"]`,确认一张证书覆盖两主机名
- `caddy fmt --diff deploy/Caddyfile` 无差异(格式合规)
- `bash -n deploy/install.sh` 通过
- 页脚证据:`assets/qa/site-footer-before.png` 与 `-after.png`(1280×360,用站点自身的 `assets/site.css` 渲染,非伪造样式),像素 diff 覆盖整幅;after 页脚右栏第三行是带下划线的 `__ICP_NUMBER__` 链接,颜色继承页脚灰字
- 本轮没动任何 C# 文件,`git status --short` 只有 `deploy/` 三个 + `site/index.html` + `docs/IMPL.md`

**残留风险**

1. **`www` 必须单独加解析记录**。云解析里只加主域 A 记录的话,`www.ftranslation.cn` 无解析,Caddy 对两主机名签证书时 www 那条会失败,连带影响整张证书。上线时 `@` 与 `www` 两条一起加。
2. **占位符会原样显示**。备案通过前站点本就不该对外(此刻 Caddy 被 `install.sh` 刻意 disable),但若有人手工 `systemctl start caddy`,页脚会露出 `__ICP_NUMBER__`。install.sh 的警告是唯一防线。
3. **域名实名与工信部同步仍未完成**(注册局审核中 → 通过后还需 2~3 天同步),备案尚未提交。若管局要求「域名证书」,在阿里云域名控制台 → 域名列表 → 管理 → 下载域名证书。
4. `beian.miit.gov.cn` 在部分网络环境下访问慢,备案号链接点开可能超时;这是工信部站点自身的问题,不影响合规性。

## v51 学习闭环:截图存单词本 + 四档复习 + 单窗口七页 ✅

**为什么这样改**

v44 做完了「保存」,但那时候的账只结了一半:

- **截图翻译和单词本是两件事**。截图翻译走的是 `qwen-mt-image` 直接把图翻成文字,屏幕上出现的译文看得见、抄不走。用户想记下里面某个词,只能自己再敲一遍。OCR 单独抽一路之后,识别到的原文进了结果窗的可编辑框,「保存到单词本」才算真的可用。
- **存了不复习等于没存**。v44 的生词本只有三个视图(词/短语/句),没有任何调度:存 500 条和存 5 条在界面上没有区别。这次补上四档复习与到期队列,`notes` 表加了 6 列复习状态。
- **四扇窗各管一段,入口是散的**。此前主窗口、设置窗、账号窗、结果卡各自为政:换一个 API Key 要开设置窗,看套餐要开账号窗,读完一条结果要回主窗口才有列表。现在合成一扇窗 + 左侧栏七页,唯一还独立的是复习窗 —— 它要独占键盘(空格揭示、1–4 打分),被嵌到主窗口里反而会被侧栏抢焦点。

三个阶段刻意这样切:

- **复习状态存在服务端,调度算法在客户端**。多端同步要的是「这张卡现在到哪一台阶」,不是「这张卡什么时候该出现」;算法留在客户端以后调参不用动数据库。服务端只存 `stage`/`due_at`/`last_grade`/`review_count`/`lapses`/`reviewed_at` 六个结果字段。
- **四档而不是两档**。两档(认识/不认识)会让「模糊」这一档无处安放,用户只能二选一地撒谎;四档(忘记/模糊/熟悉/记住)对应 1/2/3/4 四个键,判断成本低。
- **单窗口而不是继续加窗**。窗口越多,「这个功能在哪」这个问题就越贵;侧栏一列七项一眼看完。

**算法口径**

四档 → 下一台阶 → 下次到期(`src/FastTranslation.Core/Notes/ReviewSchedule.cs`,服务端 `Domain/ReviewSchedule.cs` 是镜像,改一处必须改另一处):

| 评分 | 下一台阶 | 说明 |
| --- | --- | --- |
| 忘记 `forgot` | 1 | 回到 10 分钟后,`lapses` +1 |
| 模糊 `fuzzy` | 2 | 1 天后 |
| 熟悉 `familiar` | 3 | 3 天后 |
| 记住 `known` | 从 stage ≥ 4 起逐级 +1,否则直接 4 | 4 → 7 → 14 → 28 → 56 天,封顶 stage 7 |

台阶间隔表:`0 = 未排期`、`1 = 10 分钟`、`2 = 1 天`、`3 = 3 天`、`4 = 7 天`、`5 = 14 天`、`6 = 28 天`、`7 = 56 天`。**服务端只存结果,不参与调度**;`review_count` 每次评分 +1,`lapses` 只在「忘记」时 +1。

复习队列的组装:到期卡(`due_at <= now AND stage > 0`,按 `due_at` 升序)+ 今日新卡(`stage = 0`,按 `created_at` 倒序),新卡日上限默认 20、队列上限 50;`due` 与 `fresh` 分开返回,前端进度条用的是两者之和。

**做了什么**

服务端(`server/FastTranslation.Server/`):

| 文件 | 内容 |
| --- | --- |
| `Storage/ServerDatabase.cs` | `notes` 加 6 列(`stage`/`due_at`/`last_grade`/`review_count`/`lapses`/`reviewed_at`),走 `EnsureColumn` 而非改 `CREATE TABLE`:`CREATE TABLE IF NOT EXISTS` 对老库一行都不执行,新列只能 `ALTER TABLE` 补。`ix_notes_user_due` 也必须在 `EnsureColumn` 之后建,否则老库上建索引会因缺列报错 |
| `Storage/NoteStore.cs` | 四个新方法:`ListDue`(到期卡)、`ListFresh` + `CountFreshSince`(今日新卡与配额)、`SetReview`(写台阶、到期时间、次数与 lapses);列清单五处同步扩到 18 列 |
| `Domain/ReviewSchedule.cs`(新) | 上面那张表的镜像;服务端项目不引用 `FastTranslation.Core`,与 `Domain/NoteKind.cs`、`Domain/NoteClassifier.cs` 同理 |
| `Api/NotesEndpoints.cs` | `GET /api/v1/notes/review`(队列 + `freshQuota`)与 `POST /api/v1/notes/{id}/review`(打分);两个端点都先过「账号可用 → 有单词本权益」两道闸 |
| `Api/Contracts.cs` | `GradeNoteRequest`、`ReviewQueueResponse` |
| `Gateway/GatewayEndpoints.cs` | `POST /v1/ocr`:客户端把截图 Base64 上来,网关转 `qwen-vl-ocr`,按张计费并记入 `usage_events` |
| `Domain/Pricing.cs` | `qwen-vl-ocr` 单价条目 |

客户端:

| 文件 | 内容 |
| --- | --- |
| `Core/Notes/ReviewSchedule.cs`(新) | 调度算法、`ReviewGrade` 枚举、`IntervalLabel`、`Display`、`TryParseGrade` |
| `Core/Notes/NoteTypes.cs` | `Note` 加 6 个尾参数与 `GradeDisplay`/`DueLine` 派生属性;`ReviewQueue` 记录 |
| `Core/Notes/NotesApiClient.cs` | `ReviewQueueAsync` / `GradeAsync` |
| `Core/ImageOcrTypes.cs`(新)、`Providers.QwenMt/QwenOcrClient.cs` / `GatewayOcrProvider.cs`(新) | 截图识别的两条路:直连 DashScope 与走自建网关 |
| `App/ImageTranslationCoordinator.cs`、`Platform.Windows/ImageResultWindow.cs` | 结果窗从「一图一译」变成「图片 + 可编辑识别框 + 复制图片/另存为/保存到单词本/关闭」 |
| `App/MainWindow.xaml(.cs)`、`App/Pages/*`(新,7 个页面) | 单窗口 + 左侧栏七页:首页、单词、短语、语句、复习、套餐、设置;设置窗与账号窗被删,内容搬进页面 |
| `App/ReviewWindow.xaml(.cs)`(新) | 今日复习:题目在上、虚线槽在下,空格揭示译文,1–4 评四档,Esc 退出;评完进空态 |
| `App/AccountCoordinator.cs` | `ReviewQueueAsync` / `GradeNoteAsync`,继续走 `WithSessionAsync`(401 自动刷新) |

**验证**

- `dotnet build FastTranslation.slnx -c Release`:**0 警告 0 错误**
- `dotnet test`:4 个项目 **541 全绿**(Core 192 / QwenMt 87 / Platform.Windows 87 / Server 175),失败 0 跳过 0。本轮新增:`ReviewScheduleTests` 43 例(8 台阶 × 4 档的转移 + 自定义档测试 + 显式元组表的间隔校验)、`NotesReviewTests` 15 例(队列拆分、配额夹到 0、评分后离开新卡、非法档 400、别人的卡 404、`forgot` 只加 lapses、老库迁移补 6 列)、OCR 相关若干
- 真实服务端端到端(Development、`http://127.0.0.1:5199`、SQLite `publish/devserver/qa.db`):注册 → 存 4 条笔记 → `GET /notes/review` 返回 `due:[]`、`fresh:4`、`freshQuota:20` → 对两张卡打分 → `stage`/`due_at` 落库正确 → 再查队列,到期卡按 `due_at` 升序排出
- 界面证据(UI Automation 驱动真实 exe,不是静态稿;脚本 `publish/uia-nav.ps1`、`publish/select-row.ps1`、`publish/review-shot.ps1`、`publish/wheel-test.ps1`):

| # | 文件 | 取样位置 |
| --- | --- | --- |
| 1 | `publish/shots-v49/HomeButton.png` | 侧栏「首页」高亮,输入区 + 语言下拉 + 复制译文/存到单词本/翻译 |
| 2 | `publish/shots-v49/WordSelected.png` | 侧栏「单词」→ 选中第 1 条 `resilient` → 右侧详情「复习」四档 + 「上次记住 · 7 天后」 |
| 3 | `publish/shots-v49/PhraseButton.png` | 侧栏「短语」高亮,列表为短语 |
| 4 | `publish/shots-v49/SentenceButton.png` | 侧栏「语句」高亮,列表为整句 |
| 5 | `assets/qa/page-plan.png` | 侧栏「套餐」→ 账号卡 + 你的套餐 + 本月用量 + 激活码 + 侧栏「标准版」角标 |
| 6 | `assets/qa/page-settings.png` | 侧栏「设置」→ 五组卡片 + 底部固定动作条 |
| 7 | `assets/qa/review-mask.png` | 复习窗 1/2 未揭示:虚线槽 + 四档置灰 + 小字 10 分钟/1 天/3 天/7 天 |
| 8 | `assets/qa/review-revealed.png` | 空格揭示后:译文 + `Code.exe · 10-29` 来源行 + 四档可点 |
| 9 | `assets/qa/review-empty.png` | 队列评完:「这一轮过完了。」+ 再看看单词本 |
| 10 | `publish/shots-v49/ImageResult.png` | 译图结果窗:复制图片/另存为/保存到单词本/关闭 四个 pill + 可编辑识别框 |
| 11 | `publish/shots-v49/HomeDark.png`、`assets/qa/review-mask-dark.png` | 深色:首页与复习窗 |
| 12 | `assets/qa/main-review-badge.png` | 两张今天到期的卡 → 侧栏「复习」红点 `2`,列表行显示 `今天到期` |

  设置页的滚轮回归单独测过:指针停在 API Key 输入框上滚 5 格,`Body` 的 `VerticalScrollPercent` 从 0 → 43.57% —— 输入框不能把滚轮吃掉(`Body_PreviewMouseWheel` 隧道逻辑从 `SettingsWindow` 搬到 `SettingsPage` 后仍生效)。

**兼容性**

- 老库打开即迁移:`EnsureColumn` 逐列补 6 个复习字段,`notes` 读取位置扩到 `0..17`;迁移测试用 v47 形状的库跑过一遍。
- v1.2.x 客户端打到新服务端不受影响:`/notes/review`、`/notes/{id}/review`、`/v1/ocr` 都是新增端点;`NoteDto` 只是多出 7 个带默认值的字段,旧客户端反序列化会忽略。
- 客户端与服务端的 `ReviewSchedule` 是两份镜像代码,靠同一组单测数字(8×4 转移表)对齐;改档位必须同时改两处,两处文件头都写了这句。

**残留风险**

- **套餐页不收款,「订阅」按钮只记录意向**。真正的开通通道是激活码(v49),页面上没有支付流程;用户点「订阅」看到的是「用激活码开通」的引导。
- **OCR 单价未逐字核对**。`qwen-vl-ocr` 的 `Pricing` 条目按 DashScope 文档的图片计费档抄写,没有拿真实账单对过;计费只影响网关侧的用量记录,不影响用户能不能用。
- **复习调度的参数没有真实用户数据支撑**。10 分钟 / 1/3/7/14/28/56 天是照通用间隔重复经验取的,`MaxStage=7` 之后不再延长;真跑一段时间后可能要按留存数据调。
- **托盘原生菜单没有「复习」入口**。加一项要动 `TrayIconService` 的菜单结构(Platform.Windows),本版入口是侧栏「复习」与单词页右上角「去复习」。
- **`FT_UITEST=image-result` 是 QA 钩子**,用合成图打开译图窗只为截图;真实截图翻译路径是「热键 → 全屏框选 → OCR → 结果窗」,那段区域选择浮层是分层窗口,`PrintWindow` 拍不到内容,自动化截图必须绕开它。

## v52 部署:整套流程在真机 ECS 上跑通,修掉两个只在真机才暴露的缺陷 ✅

**为什么这样改**

v47 把 deploy/ 五个脚本写完了,但残留风险第一条就是「整套流程从没在真机跑过」。今天公钥进了 `/root/.ssh/authorized_keys`,就把这台 2 核 2G 的杭州 ECS(Ubuntu 26.04.1,`resolute`)当成验收环境,从 `bootstrap.sh` 一路跑到网关真翻译。纸面推演漏掉的两件事都在这一次撞出来了。

- **`vm.swappiness` 静默失效**。旧脚本把 sysctl 的写入放在「新建 /swapfile」的分支里,而阿里云的 Ubuntu 镜像**自带** `/swapfile`(4G),分支根本不进 —— 跑完 `sysctl -n vm.swappiness` 还是 20。修法:swappiness 的写入挪到分支之外,两条路径都写(`deploy/bootstrap.sh:11-24`)。
- **升级路径是坏的**。旧 `install.sh` 上来就 `cp -r server/. /opt/fasttranslation/`,服务在跑的时候拷自己在运行中的 apphost 上会撞 Linux 的 ETXTBSY:`cp: cannot create regular file '/opt/fasttranslation/./FastTranslation.Server': Text file busy`,`set -euo pipefail` 让脚本在第一步就退出。首装看不出来,因为首装时服务还没起过。修法:调整成「先校验生产配置 → 停服务 → 拷二进制 → 装单元/站点/备份/Caddyfile → 起服务并打健康检查」,配置校验放在停服务之前,这样配置缺失而 `exit 1` 的那次不会把已经在跑的服务撂下(`deploy/install.sh:14-49`)。
- **Caddy 装完就自己 started**。cloudsmith 的 deb 会 enable 并拉起 `caddy.service`,监听 `:80` —— 备案号还没下来,这一层不能对外。`bootstrap.sh` 现在在 `apt-get install caddy` 之后立刻 `systemctl disable --now caddy`,`install.sh` 只在 `Caddyfile` 的占位符被替换掉之后才重新 enable。
- **加固原来压根没有**。README 承诺的只有 swap/账号/目录/Caddy/libicu,`sshd -T` 实测 `permitrootlogin yes` + `passwordauthentication yes`,主机防火墙 `Status: inactive`(整台机器只靠阿里云安全组挡着)。补了一段 hardening:`/etc/ssh/sshd_config.d/99-ft.conf` 写 `PermitRootLogin prohibit-password` / `PasswordAuthentication no` / `KbdInteractiveAuthentication no` / `MaxAuthTries 4`,`sshd -t` 通过才 reload;ufw 先 `allow OpenSSH/80/443` 再 `--force enable`(顺序反了就把当前这条 SSH 掐了)。两处都是**有前提的**:authorized_keys 为空时刻意跳过 sshd 并打印警告,否则这脚本就是把自己锁在门外的最后一个动作;想全程跳过就 `FT_SKIP_HARDEN=1`。

**真机验收数据(2026-10-29,杭州 / 2 核 2G / Ubuntu 26.04.1)**

| 检查项 | 实测 |
| --- | --- |
| `bootstrap.sh` 两次(初装 + 加固后重跑) | 退出 0;`ftserver` uid 999 / gid 983;`vm.swappiness=10` 生效 |
| Caddy 源在境内 ECS 可达 | `caddy` v2.11.7,18.1MB / 16s;apt 走 `mirrors.cloud.aliyuncs.com` 内网镜像 58MB/s |
| 上传 49MB 产物 | scp 39 秒(入向不限,只有出向 3Mbps) |
| `install.sh` 首装 | `enabled + active`,`MemoryCurrent` ≈ 84 MB(可用内存 1288 MB),`/health` = `{"status":"ok"}` |
| `install.sh` 重复执行(升级) | 停→拷→起,数据不丢(2 用户 / 1 笔记),`NRestarts=0` |
| 端口暴露 | 只有 `0.0.0.0:22` 与 `127.0.0.1:5080`;Caddy disabled,80/443 无监听 |
| 网关真翻译 | `POST /v1/chat/completions` → 200 / 535ms,上游 `usage` = 43 / 17 tokens |
| 按字符记账 | 请求 71 字符 → 月额度与日额度各 +71;`usage_events` 落 `cost_micros` 75–77(≈0.0008 元/次) |
| 免费日额度闸门 | 1975 字符后的下一次 → `429` + 头 `X-FT-Quota-Reason: daily_quota_exhausted` + 体 `{"error":"daily_quota_exhausted"}`,正是客户端 `QuotaRefusal.Describe` 认的形状 |
| 跨设备隔离 | 同账号换指纹仍 200(日额度按设备算,月额度按账号算) |
| 订阅闸门 | 免费账号 `POST /api/v1/notes/` → `403 {"error":"subscription_required"}`;`POST /api/v1/admin/accounts/{email}/plan` 升 light 后 → 200 且 `quotaDaily` 变 null |
| 只读不受闸门影响 | 免费账号 `GET /api/v1/notes/` → 200(total 0) |
| 401 / refresh | 无令牌 → 401;`/auth/refresh` 换到新 access(399 字符)并轮换 refresh |
| 备份 | `ft-backup.sh` 产出 4909 字节 `.db.gz`,`/etc/cron.d/fasttranslation-backup` 04:17 UTC |
| 磁盘 | 7.1G / 40G(19%) |

**验证**

- `bash -n` 过 bootstrap 与 install;`sshd -T` 读回 `permitrootlogin prohibit-password` / `passwordauthentication no` / `maxauthtries 4`;`ufw status numbered` 四条 ALLOW(22/80/443 + v6);加固后仍能用密钥登录,隧道里的 `/health` 仍返回 ok。
- 网关/闸门/记账这一批断言全部打在真服务器上,脚本 `publish/e2e-server.mjs`、`publish/e2e-quota.mjs`(publish 不入库,记录数据后删)。上游 Key 用 DPAPI 从本机 `qwenmt-api-key.bin` 取(**熵是 `ft-qwenmt-api-key`**,见 `src/FastTranslation.Platform.Windows/DpapiSecretStore.cs:25-39` —— 传 `$null` 会抛 `InvokeMethodOnNull`),值不落对话、不落仓库,只落 `/opt/fasttranslation/appsettings.Production.json`(root:ftserver 640)。
- 本轮只改 `deploy/` 三个文件与本文档,没动任何 C#;全量 build/test 由并行会话负责,当前工作区有他们未提交的 `Pages/ReviewPage.xaml` 等改动,提交时按路径挑文件,没有 `git add -A`。

**残留风险**

1. **真 WPF 客户端还没连过这台服务器**。`src/FastTranslation.Core/Account/AccountApiClient.cs:18` 的 `DefaultBaseUrl` 仍是 `http://127.0.0.1:5199`,而服务器只在回环 5080 上;HTTP 契约(头、体、429/403 形状)已用 Node 逐条打过,但界面层的回落提示与账号窗读数要等域名 + Caddy 起来后发首个正式版才算验过。
2. **签名密钥与管理令牌目前只有两份**:服务器上的配置文件 + 本机 `publish/.server-secrets.json`(gitignore 内)。轮换 SigningKey 会让所有已登录设备的 access token 立刻失效(refresh 仍可用),轮换 AdminToken 会打断管理脚本 —— 真要换记得两处一起改。
3. **备案没下来之前 80/443 是开着的**(ufw 与安全组都放行),只是 Caddy disabled 无进程监听。备案号到手后要按 README 的 sed 替换域名并 enable caddy,顺带把 `site/index.html` 的 `__ICP_NUMBER__` 填掉。
4. **`/swapfile` 是镜像自带的 4G**,脚本不校验已存在那份的权限与 fstab 条目;换镜像或换可用区时这条要重看。
5. **单实例单进程**。SQLite WAL + `ProtectSystem=strict` 决定了这台机器不能横向加副本,升级期间会有一两秒的 502 窗口(`Restart=always` / `RestartSec=3`),对翻译工具可以接受。

## v53 部署:控制台文件管理与密钥单点(2026-10-29) ✅

**为什么这样改**: 引导脚本把 `PasswordAuthentication no` 一刀切之后,阿里云 Workbench 的「文件管理」报 `login.error.passwordAuthenticationIsProhibited` 打不开。日志(`journalctl -u ssh`)显示这些会话来自 `100.104.86.13` / `100.104.86.48` —— **文件管理走的是真实 sshd 会话并用实例密码认证**,和「免密连接」(云助手 `aliyun` agent)是两条路。同时查 `/root/.ssh/authorized_keys` 发现加固前**只有一把密钥**,就是开发机笔记本上的 `ft-workstation` —— 换电脑或重装系统就只能走控制台重置密码,是单点。

**改了什么**: 不整体回退,而是**按来源放行**。在 `/etc/ssh/sshd_config.d/99-ft.conf` 末尾加 `Match Address 100.104.0.0/16` 段(`PasswordAuthentication yes` + `PermitRootLogin yes`),阿里云内网段可用实例密码,公网仍只认密钥。`deploy/bootstrap.sh` 的 drop-in 同步补上这段(脚本用 `if [ ! -f ]` 守卫,已有该文件的机器不会自动补,需手工加,本次就是手工加的)。另外新建一把 ed25519 应急密钥 `ft-emergency-save-2026-10-29` 入 authorized_keys,私钥在开发机 `~/.ssh/ft-emergency`(另存一份 `publish/ft-emergency-copy-put-this-off-this-laptop`,**需拷到别的设备才算兜底**)。

**怎么验证**:
- `sshd -T -C user=root,host=<hostname>,addr=203.0.113.9` → `permitrootlogin prohibit-password`、`passwordauthentication no`(公网)
- `sshd -T -C user=root,host=<hostname>,addr=100.104.86.13` → `permitrootlogin yes`、`passwordauthentication yes`(阿里云内网)
- 笔记本强制品种 `-o PreferredAuthentications=password` 连公网:仍 `Permission denied (publickey)`;带密钥连:正常;`sshd -t` 通过后 `systemctl reload ssh`
- authorized_keys 现有两把:`ft-workstation`(RSA 3072)与 `ft-emergency-save-2026-10-29`(ed25519);`/etc/passwd` 里只有 `root` 有登录 shell

**残留风险**: ①`Match Address` 的可信度依赖来源 IP 不被伪造 —— 100.104.0.0/16 是阿里云内网服务段,不可从公网路由,但仍属「按 IP 白名单放行弱面」的折中。②Ubuntu 26.04 默认开 `PerSourcePenalties`,日志里 `198.51.100.10` 的大量连接被判 `connections without attempting authentication` 丢弃;若 Workbench 复用同一批来源 IP,文件管理可能出现偶发失败,届时需对该段关掉罚分。③实例密码仍可用于控制台 VNC 与 `sudo`,这两条路不受本改动影响。④上一版把「实例没设密码」当成事实是错的,实测 `passwd -S root` = `P 2026-10-29`。

## v54 复习进页内 + 套餐与设置改两列 ✅

**为什么**:v51 把复习做成一扇独立窗口,从单窗口外壳点出去再弹新窗,和「一扇窗七页」的其余部分不一致;套餐页与设置页则各自把一个 720/760 宽的竖列靠左摆,窗口右侧整片空白,像没做完。两件事都是「形状和内容不匹配」,一起改。

**复习改成页内**(`feat(复习): 复习改成主窗口内的一页`,2eadbef)
- 流程:侧栏「复习」→ 页内列出今天要看的条目(单词/短语/语句混排,原文 + 译文 + 「今天到期」或「今日新词」)→ 每行复选框 + 顶部全选 →「开始复习(6)」→ 同一页进入逐卡复习(空格揭示,1-4 评四档,跳过不算评过)→ 「这一轮过完了」可「再选一批」或回单词本
- 新增 `src/FastTranslation.Core/Notes/ReviewSession.cs`:`ReviewSession` 只持有「勾选了哪些卡、当前第几张」,界面不管顺序;12 个单测覆盖跳过、评完、越界、空队列
- 删除 `ReviewWindow.xaml(.cs)`;`App.xaml.cs` 去掉 `ShowReview`;`MainWindow` 去掉 `ReviewRequested`,`ShowPage(MainPageId.Review)` 就是入口
- 键盘:页面宿主的空格/1-4 靠 `PreviewKeyDown` 隧道处理,只在卡片模式接管;否则在列表页打字会被吃掉
- 四档按钮条 `MaxWidth=620` 居中。满宽铺开时四个按钮各 320px,像工具栏;`HorizontalAlignment=Center` 又会退化成内容宽度(一排小按钮),正确写法是保留 Stretch 只加 MaxWidth —— WPF 在 Stretch + MaxWidth 受约束时居中排布

**套餐与设置改两列**(`feat(界面): 套餐与设置改成两列`,93ff05a)
- 两页都用 `MaxWidth=980` 的居中容器;依据 ui-ux-pro-max 的「正文行宽 65-75ch、不要让文本铺满视口」——不是把内容拉满屏,而是把宽度用在两列上
- 套餐页:横跨顶部的账号卡(邮箱/套餐/刷新用量/退出登录)+ 左列「能买什么」(套餐卡、月付年付、订阅)+ 右列「用了多少」(本月/今日用量、激活码)
- 套餐页未登录态:左列登录表单,右列新增「登录后有什么不一样」三条(免费额度按账号算 / 生词本存在服务端 / 复习进度跟着账号走),原来这半边是空的
- 设置页:左列「它怎么给我翻译」(翻译与外观、取词与写回、系统),右列「它连到哪里」(服务、排除应用);底部动作条加「这些设置只存在这台机器上,不随账号同步」

**顺带修掉的两个真 bug**
1. `MainWindow.ShowPage` 只在「页面已缓存」时调 `Reload`,第一次进入的页面是 `Build` 出来的、从没读过数据。表现是套餐页首访用量空白(要点一次「刷新用量」才出数字)、单词本首访永远显示不出内容(点「刷新」才有空态文案)。改成 `Build` 之后也走一次 `RefreshPage`,缓存命中与新建两条路径用同一个方法。
2. `PlanPage.OnAccountChanged` 只重画不重取用量。启动时的会话恢复是异步的,账号晚到就再也不会补取,用量卡一直空白。现在账号变化时一并 `LoadUsageAsync`。

**验证**
- `dotnet build FastTranslation.slnx -c Release` → 0 警告 0 错误
- `dotnet test` → 548 全绿(Core 199 / Platform.Windows 87 / QwenMt 87 / Server 175)
- 截图(1820×1155 = 1040×660 逻辑 × 175% DPI,取样即在整张窗口图上直读):
  - `assets/qa/layout-plan.png`:左列套餐卡,右列用量卡(首访即有 `0 / 10,000 字符(本月)`)与激活码卡
  - `assets/qa/layout-settings.png`:左列三个分组,右列两个分组,两列底边基本齐平
  - `assets/qa/layout-word.png`:首访即显示「这一栏还是空的」空态,而不是空白列表
  - `assets/qa/review-pick.png` / `review-card.png` / `review-revealed-inpage.png` / `review-done.png` / `review-pick-empty.png`:复习五态
- 本地开发服务端:`Server__SigningKey=ft-dev-signing-key-0123456789abcdef`、DB `publish/devserver/qa.db`、`http://127.0.0.1:5199`;客户端设置页「账号服务地址」指向它,截图里的账号是 `qa-layout@example.com`(免费版)

**兼容性**:服务端零改动,数据库零改动;`ReviewWindow` 删除后不再有机型依赖。客户端设置的键名未变,老 `settings.json` 直接可用。

**残留风险**:①复习页的键盘处理只覆盖空格/1-4/回车/Esc,输入法组合键仍会先到输入框(页面里没有可聚焦输入框,实际无影响)。②两列宽度按 1040 逻辑宽设计,窗口拖到很窄时两列会各让一半宽度,未做断点折叠。③`ReviewSession` 只保证顺序,重复点击「开始复习」不会重新洗牌,题目顺序恒为队列顺序。

## v55 账号与套餐拆页:改名称、改密码 ✅

**为什么**:套餐页原来同时承担「我是谁」和「买什么」两件事——已登录的人想看一眼剩余额度或改个名字,得先经过一张价格表;未登录的人进套餐页先撞上登录表单,而他真正想看的可能是价格。v54 把两页铺成两列后,这个问题反而更明显:左上角那张横跨整行的账号卡里塞着邮箱、套餐、刷新、退出登录,和下面的订阅卡抢注意力。拆页的判据是「这一页回答一个问题」:账号页回答「我现在是什么身份、还剩多少」,套餐页回答「我能买什么、额度怎么算」。

**页面结构**(`feat(账号): 拆出账号页`,deb7f2a)
- 侧栏在「套餐」上方插入「账号」,`MainPageId` 加 `Account`,`ShowPage` 走同一个 `RefreshPage` 通道
- 账号页已登录态:圆形首字头像(`AccountProfile.Initial`,中文取首字、邮箱取首字母大写)+ 名称 + 套餐角标 + 邮箱;右侧「刷新」「退出登录」
  - 左列「当前套餐」(`PlanNameText`/`PlanValidityText`/本月与今日 `MeterBar`/「看套餐与价格」跳套餐页)
  - 左列「修改名称」、右列「修改密码」、右列「数据」说明(生词本与复习进度都在服务端,退出登录不会删)
  - 没设名称时不再重复显示邮箱:第二行 `EmailText` 隐藏,第一行 `DisplayNameOrEmail` 已经是邮箱
- 账号页未登录态:左列登录表单(邮箱/密码/验证码/去注册),右列「登录后有什么不一样」三条(免费额度按账号算 / 生词本存在服务端 / 复习进度跟着账号走),底部「账号:未登录」
- 套餐页删掉账号卡,只留订阅卡、「包括什么」三条、用量、额度怎么重置(月/日/复习队列三段)、激活码

**服务端两条写接口**
- `PATCH /api/v1/account/profile`(`UpdateProfileRequest(string? DisplayName)`):去空白后 ≤24 字符、拒绝控制字符(名称会进窗口标题区与侧栏,换行会撑坏布局)、空串表示清空回落到邮箱。返回更新后的 `AccountDto`
- `POST /api/v1/account/password`(`ChangePasswordRequest(CurrentPassword, NewPassword, DeviceFingerprint, DeviceName)`):当前密码错 `400 password_mismatch`,新密码 <8 位 `400 password_too_short`,与旧密码相同 `400 password_unchanged`;成功后 `RevokeAllRefreshTokens` + 给本机换发一对 `AuthResponse`,所以「这台不用重新登录,别的设备下次刷新令牌时被拒」
- `users` 表加 `display_name TEXT NOT NULL DEFAULT ''`,走 `EnsureColumn` 热迁移;`AccountDto` 末尾加带默认值的 `DisplayName`,老客户端解析不受影响
- `AuthEndpoints.IssueTokens` 由 `private` 改 `internal`,让改密码复用同一套发牌逻辑,不复制粘贴

**客户端**
- `AccountApiClient.UpdateProfileAsync` / `ChangePasswordAsync`;`AccountErrorCode` 已有 `InvalidProfile`/`PasswordMismatch`,只补 `MapFailure` 的两个 case
- `AccountCoordinator.UpdateProfileAsync` / `ChangePasswordAsync`,都走 `WithSessionAsync` 并回写会话(改密码会换 refresh token,不写回下次就 401)
- `AccountProfile.DisplayName` + `DisplayNameOrEmail` + `Initial`

**验证**
- `dotnet build FastTranslation.slnx -c Release` → 0 警告 0 错误
- `dotnet test` → **560 全绿**(Core 203 / Platform.Windows 87 / QwenMt 87 / Server 183);v54 是 548,本次 +12:8 个服务端用例(`AccountProfileTests`:改名/清空/超长与换行被拒/未登录 401/当前密码错/新密码过短或重复/换密码后本机仍可用且旧密码失效/换密码踢掉其它设备的 refresh token)+ 4 个客户端映射用例(`AccountClientTests`:PATCH 路径与字段、错误码映射、改密码字段完整性、`password_mismatch` 映射)
- 提交后用 `git worktree add` 单独检出 `deb7f2a` 重跑 build 与 test,确认这一提交自身可编译可测试(工作区里还有别的会话未完成改动,不能只看工作区)
- 截图(1820×1155 = 1040×660 逻辑 × 175% DPI,取样即在窗口图上直读):
  - `assets/qa/account-signedin-free.png`:账号页已登录(头像「Q」、邮箱、免费版角标、本月 `0 / 10,000 字符`、今日 `0 / 2,000 字符`、改名称、改密码、数据说明)
  - `assets/qa/account-named.png`:点「保存名称」后头像变「永」、名称变「永威」、状态行「名字已保存。」,服务端 `display_name` 已落库
  - `assets/qa/account-signedout.png`:未登录双栏(左表单、右「登录后有什么不一样」、底部「账号:未登录」)
  - `assets/qa/account-dark.png`:深色下的账号页
  - `assets/qa/plan-trimmed.png`:精简后的套餐页(左订阅卡 + 包括什么,右用量 + 额度怎么重置 + 激活码)

**兼容性**:数据库只加一列且带默认值,老库自动迁移;`AccountDto` 新字段在末尾且可缺省。客户端删掉了 `PlanPage` 里的账号卡与 `RefreshAccountStatus` 调用,`App.xaml.cs` 的 `RefreshAccountStatus` 改为只更新侧栏与窗口标题。

**残留风险**:①改名称与改密码都必须联网,离线时按钮报「连不上账号服务器」,没有做离线排队。②改密码后旧 access token 在 30 分钟内仍然有效(服务端只撤销 refresh token),这是故意的,不做即时踢下线。③名称不去重,两个人可以都叫「永威」;账号页只用邮箱做唯一标识,没有暴露用户 id。④`EnsureColumn` 只加列不改类型,`display_name` 将来若想加长度约束需另写迁移。

## v56 激活码补完:作废与台账、跨档兑换不再亏天数 ✅

**为什么**:v49 把码做到了能发能兑,但运营侧三件事没落地(v49 自己列的残留风险):①发出去的码用没用,只能拿 sqlite3 手查;②打错批次、发错人、泄露的码没有任何作废手段;③跨档兑换亏客户。第三条是这次真机 E2E 实测出来的:手里 light/30 天(到期 1798446194),再兑一张 standard/30 天,到期变成 1795854194 —— **反而少 30 天**。根因在 `RedeemEndpoints.cs` 的 `stacks` 判断只在同档时才叠加,跨档走 `now + days` 把剩余天数扔了。而降档早已被 400 `redeem_plan_downgrade` 挡在门外,所以能走到这一步必然是同档或升档,没有任何理由重设计时起点。

**服务端**
- `server/FastTranslation.Server/Storage/ServerDatabase.cs`:redeem_codes 建表补 `revoked_at INTEGER`,并在 `Initialize()` 里 `EnsureColumn(connection, "redeem_codes", "revoked_at", "INTEGER")` —— 生产机这张表已经存在,`CREATE TABLE IF NOT EXISTS` 永远不会补列
- `server/FastTranslation.Server/Storage/RedeemStore.cs`:`RedeemCodeRecord` 加 `long? RevokedAt` + `IsRevoked`;`TryClaim` 的 WHERE 改成 `code = $code AND redeemed_at IS NULL AND revoked_at IS NULL`(**作废挡在存储层,不是只在大端点判一下**,并发兑换也不可能绕过);新增 `Revoke(code, now)`(只撤未兑的)、`LedgerRow(...)`、`List(status, take, skip)`(LEFT JOIN users 取买家邮箱,`ORDER BY created_at DESC, code`)、`Count(status)`、`IsKnownStatus`;四种 fate 的 WHERE 片段集中在私有 `Where(status)` 一处,保证 List 与 Count 口径不会分叉
- `server/FastTranslation.Server/Api/RedeemEndpoints.cs`:兑换分支在 `redeem_code_used`(409)之后插入 `redeem_code_revoked`(**410 Gone**);到期计算改成公平叠加 —— `long from = now; if (user.PlanExpiresAt is { } current && current > now) { from = current; } var expiresAt = from + (record.Days * SecondsPerDay);`
- 新增 `RedeemAdminEndpoints`:`GET /api/v1/admin/codes?status=open|used|revoked|all&limit=&offset=`(status 缺省 open,未知 status 400 `status_invalid`,limit 默认 50 上限 200)与 `POST /api/v1/admin/codes/{code}/revoke`(幂等 200 `code_revoked`;已兑换 409 `code_already_redeemed`;未知码 404 `code_missing`;规范化失败 400 `redeem_code_invalid`),两条都挂 `AdminEndpoints.RequireAdminToken`;`Program.cs` 里 `app.MapAdminEndpoints()` 之后 `app.MapRedeemAdminEndpoints()`
- `server/FastTranslation.Server/Api/Contracts.cs`:`RedeemCodeDto(Code, Plan, Days, Note, CreatedAt, RedeemedEmail, RedeemedAt, RevokedAt)`、`RedeemCodePageResponse(Items, Total, Limit, Offset)`
- 码的键形态没动:行按**打印形态** `FT-XXXX-XXXX-XXXX-XXXX` 存,端点先把用户输入 `Normalize` 再 `Format` 回来查(`RedeemEndpoints.cs:37-42`),这样运营在数据库里 grep 到的就是当初打印出来的那一串

**客户端**
- `src/FastTranslation.Core/Account/AccountTypes.cs`:`AccountErrorCode` 尾部加 `CodeRevoked`,`Display` 文案「这张激活码已经作废了,请联系我们。」
- `src/FastTranslation.Core/Account/AccountApiClient.cs`:`MapFailure` 加 `case "redeem_code_revoked"`
- 套餐页零改动:`Pages/PlanPage.xaml.cs` 的 `Redeem_Click` 统一读 `Error.Display`,新错误码自动露出

**验证**
- 四个测试项目 **571 全绿**:Core 204 / Platform.Windows 87 / Providers.QwenMt 87 / Server 193;`dotnet build src/FastTranslation.App -c Release -o publish/tmp-appbuild` → **0 警告 0 错误**(整套 `FastTranslation.slnx` 的 Release build 会被另一会话正在运行的 FastTranslation.exe 锁住 App 输出目录,是文件占用不是代码问题,故改用独立输出目录验证 App 侧编译)
- 新增 10 个测试。`tests/FastTranslation.Server.Tests/RedeemStoreTests.cs`(4 个,新建):作废的码 `TryClaim` 返回 false、已兑出去的撤不回、open/used/revoked/all 四种计数互斥、**旧库补列迁移**(手工建 pre-v56 的 redeem_codes 表 + 插一张老码,`Initialize()` 后 PRAGMA 里确有 `revoked_at`,老码照样能查到、能作废)
- `tests/FastTranslation.Server.Tests/RedeemEndpointTests.cs` 新增 6 个:`Redeem_RefusesARevokedCode`(410 + `redeem_code_revoked`)、`Redeem_UpgradeKeepsTheDaysAlreadyBought`(light/30 在手兑 standard/30,断言升档后到期时间比兑换前**更远**且差值 ≈ 30 天 —— 就是这次实测的亏天数场景)、`Admin_ListsCodesByFate`(open 里只见到没兑的那张且 `RedeemedEmail` 为空,used 里那张带上买家邮箱,`status=all&limit=1` 只回 1 行而 `Total` 仍是全量)、`Admin_ListRejectsAnUnknownStatus`、`Admin_RevokeIsIdempotentAndCannotClawBackSpentCodes`、`Admin_CodeRoutesNeedTheAdminToken`(无令牌 GET/POST 都 403)
- 真机 E2E(YOUR_SERVER_IP,服务只听 127.0.0.1:5080,走 ssh 隧道)在**部署了 v49 的生产机**上跑通收钱链路:注册 free → `POST /api/v1/notes/` **403 `subscription_required`** → admin 发码 → `POST /api/v1/redeem` 200 → 同一账号再保存 **200**(id=b486f96d-05c0-4fe3-be91-2bd71dea6315,kind=word);同码复用 409 `redeem_code_used`;同一张码的四种书写形态(横杠/无横杠/空格分隔/裸 16 位)全部认;standard 在手兑 light 码 → 400 `redeem_plan_downgrade` 且**码未被消耗**(`TryClaim` 在所有检查之后)。跨档亏天数就是这一轮暴露的,已修。当天 16:54 整套发到生产机(YOUR_SERVER_IP):`install.sh` 原地升级,迁移前先跑 `ft-backup.sh` 留下 `fasttranslation-20261029T085403Z.db.gz`(6.3K),起服务后 `/health` = `{"status":"ok"}`。真机验收 8 项:① 老库 `PRAGMA table_info(redeem_codes)` 由 7 列补到 8 列(多出 `revoked_at`),迁移前那 7 张已兑码在台账里照样读得到买家邮箱;② `POST /api/v1/admin/codes` 发 light×2 + standard×1;③ 新注册 free 账号兑 light/30 到期 1795856084,再兑 standard/30 到期 1798448084,**差 30.0 天**(同一条序列在修复前是 −30 天);④ 对没兑的那张连撤两次都是 200 `code_revoked`;⑤ 拿作废的码去兑 → **410 `{"error":"redeem_code_revoked"}`**;⑥ 台账 `status=open|used|revoked|all` 的 total = 0 / 7 / 1 / 8,四种 fate 互斥且总数对得上;⑦ 未知 status 400、无令牌 403;⑧ 清掉 E2E 造的 6 个账号 / 8 张码 / 27 条用量事件 / 2 条笔记后全库 0 行,服务仍 active —— 对外开放时第一个注册的人是干净的。

**兼容性**:只加一列且可空,已有库由 `EnsureColumn` 自动补;新路由全在 `/api/v1/admin/` 下,不改既有 `/api/v1/admin/codes`(发码)与 `/metrics`;`AccountErrorCode` 新增值在枚举尾部;老客户端遇到 410 的 `redeem_code_revoked` 会落到映射的默认分支,显示「账号服务器出错了,请稍后再试。」而不是报错崩溃。

**残留风险**:①码仍然没有有效期,这是故意的(v49 定的「不惩罚屯码」),要按批次过期得再加一列。②作废只能撤未兑换的;已兑出去的期数要收回只能靠 `POST /api/v1/admin/accounts/{email}/plan` 手工降档,没有「退款」这个概念。③发码响应是码的唯一副本(bearer instrument,库里只有码没有批次凭据),运营发一批必须自己存一批,台账接口只能查已发出的,不能替谁找回没保存的那份。④兑换与注册/登录共用按 IP 的 20 次/分限流(`RateLimiting.AuthPolicy`),真有人拿码表来刷会把同 IP 的登录一起挤掉 —— v49 已记,本次仍未动。⑤公平叠加后,升档时买家把 light 的剩余天数并进 standard 的到期,等于「standard 的价格 + 附送几天」,这是刻意选择,定价时要算进去。⑥生产库已清空(部署当天删掉我自己造的 6 个测试账号与 8 张码),所以 v56 的迁移路径只在「有存量数据」的备份里验证过,真出现存量用户后再次升级仍需先看备份。

## v57 侧栏顺序、激活码落位、网关异常回落 ✅

**为什么**:v55 拆完账号页之后,用户拿真机截图回了三条:①划词翻译的结果卡只剩一句红字「翻译失败,请重试」;②套餐页激活码单独挂在右列末尾,左列比右列矮一大截;③侧栏顺序希望是 套餐、账号、设置。

第一条不能靠猜。`SelectionCoordinator.RunSessionAsync` 的 `catch (Exception)` 把异常吞掉只留一句兜底文案,而这个类被 `App` 构造时压根没传 `log`——**线上出现这句话时没有任何线索可查**,这本身就是个缺陷。所以这一轮先把"看不见"变成"看得见",再去修能修的那部分:

- `src/FastTranslation.App/App.xaml.cs`:构造 `SelectionCoordinator` 时补上 `log: Trace`,`%TEMP%\ft-uitest-errors.log` 里会出现 `coordinator error: ...`。`Trace` 本来就存在(FT_UITEST 钩子用它),不需要新机制。
- 新增 QA 钩子 `FT_UITEST=translate`:启动后 1.8 秒跑一次**真实**的翻译链路(先 `ShowLoading` 再 `TranslateAsync` 再 `ShowCard`,和生产路径同序),把 `ok / err.Code / notice / 译文` 写进同一份日志。不模拟划词,所以桌面忙的时候也能用。用它在用户当前配置(账号服务地址为默认 `http://127.0.0.1:5199`、本机无服务端在跑、已登录一个旧会话)下复现:**`ok=True`,译文正常**,网关连不上时回落用户自己的 Key 这条链路是通的。也就是说那句红字来自一次性与环境相关的异常,不是链路设计问题——日志补上之后才有下次的定性依据。
- `src/FastTranslation.Providers.QwenMt/GatewayTranslationProvider.cs`:`_gateway` 回调整个包进 try。网关侧抛异常(取令牌、拼地址、意外的运行时错误)现在按"网关这次没结果"处理,继续走用户自己的 Key;只有**调用方自己取消**(`cancellationToken.IsCancellationRequested`)才原样上抛,交给 `SelectionCoordinator` 的取消分支静默处理。此前这类异常会让整次翻译直接落到兜底文案,而它其实与翻译本身无关。

另外两条是纯排版:侧栏 `<Button>` 顺序换成 套餐(带套餐名角标)→ 账号 → 设置,套餐名从账号行挪到套餐行(读作"当前套餐");套餐页把激活码卡片从左列尾搬到"包括什么"下面、右列只留用量与额度说明,两列高度对齐。

**验证**
- `dotnet build FastTranslation.slnx -c Release` → 0 警告 0 错误。
- `dotnet test FastTranslation.slnx -c Release --no-build` → **573 全绿**(Core 204 / Platform.Windows 87 / QwenMt **89** / Server 193),QwenMt 比 v56 多 2 个:新增 `GatewayThrowing_FallsBackToTheOwnKey`(网关抛 `HttpRequestException` → 用自己 Key 的译文,`own.Calls == 1`)与 `CallerCancelled_StillThrows`(调用方取消必须上抛、且不碰自己的 Key)。
- 截图:`assets/qa/plan-cols.png`(1820×1155,1040×660 逻辑 × 175% DPI;侧栏 套餐/账号/设置 顺序、激活码在左列、两列底部齐平)、`assets/qa/account-after-reorder.png`(账号页在新顺序下仍正常,套餐名角标挂在「套餐」行)。
- `FT_UITEST=translate` 实测日志:`translate hook: ok=True err=/ notice= out=那只敏捷的棕色狐狸跳过了那只懒狗。`

**兼容性**:无接口与数据结构变化;`GatewayTranslationProvider` 只多一层 catch,原有五条回落规则(网关未登录 → 用自己 Key;网关成功 → 用网关;取消/超长 → 不重试;其余失败 → 用自己 Key 并附说明)全部保持,`Cancelled_DoesNotFallBack` 等旧用例未改仍绿。

**残留风险**:①用户那次「翻译失败,请重试」在本机没能复现,现在只能保证**下次**一定留下 `coordinator error:` 行;真要定位还得等它再出现一次。②账号服务仍未部署到公网(默认 `AccountBaseUrl` 还是 `http://127.0.0.1:5199`),所以没架服务端时用量卡会显示「连不上账号服务器,请检查网络后重试。」——文案归文案,这不是网络问题,是还没有地址可连,v50 的域名槽没落地之前会一直这样。③`FT_UITEST=translate` 用的固定英文样例,中文选区的路径没被它覆盖。
## v58 首译提速:网关探测、连接被拒不重试 ✅

用户反馈「第一次启动之后的第一次翻译相较于后面几次很慢」。实测确实如此,而且慢得离谱:冷启动后第一次翻译约 **5 秒**,之后每次 **0.2 秒**。查下来是三个问题叠在一起,其中两个是真 bug。

**一、`QwenMtProvider` 改写了别人给的 `HttpClient.Timeout`(真 bug,影响正常生产)**

构造里原来无条件执行 `_http.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds)`。网关链路每个请求都会 `new QwenMtProvider(..., _gatewayHttp)` 复用同一个 `HttpClient`,而 `HttpClient` **只要发过一次请求就拒绝再改 Timeout**,抛 `InvalidOperationException: This instance has already started one or more requests`。于是:

- 第一次翻译:网关正常被问一次(超时设置成功);
- 第二次起:`new QwenMtProvider` 直接抛异常 → 被 `GatewayTranslationProvider` 的 catch 当成「网关这次没结果」→ 静默回落到用户自己的 Key。

也就是说**网关只会被问一次,之后永远被跳过**——在网关活着、额度在扣的生产环境下,这会让「先用网关、用完再回落自己的 Key」的整套策略失效,而且完全无声。修法:只给自己造的那个 client 设超时,别人给的一律不动(`_ownsHttp` 的语义顺便对齐)。

**二、「积极拒绝」不该重试(真 bug,让每次翻译白等 2 秒)**

`QwenMtProvider` 的异常重试对所有网络异常一视同仁:失败 → 等 400ms → 再来一次。而本机网关没起的时候,"目标计算机积极拒绝"重试一次结果一模一样。更要命的是这台机器上**连一个没人监听的本地端口要 2 秒才拿到 RST**(裸 `TcpClient.ConnectAsync` 也一样,和 .NET、和代理设置都无关,是系统层的过滤驱动/代理驱动拖的),两次就是 4.5 秒。

修法:新增 `IsWorthRetrying` —— `ConnectionRefused / HostNotFound / NoData` 不重试,超时与连接重置照旧重试一次。三个新单测钉住:`ConnectionRefused_IsNotRetried`(只发 1 次)、`ConnectionReset_IsStillRetriedOnce`(发 2 次)、`InjectedHttpClient_KeepsItsOwnTimeout`(构造不再动 `Timeout`)。

**三、连不上的网关要记住,别人不用陪着等**

即使只重试一次,每次翻译仍要花 2 秒去问一个注定被拒的网关。加了两个机制:

- 启动时 `ProbeGatewayAsync` 用一次 `TcpClient.ConnectAsync`(3 秒上限)探网关端口。**先乐观地标记「不可用 60 秒」再探**,探通就立刻清掉——不先占位的话,探测本身要等两秒,启动后头几秒里发生的第一次翻译仍会把这 2 秒吃进去。
- 文本 / 图片 / OCR 三条网关链路都先看这个标记(`_gatewayDownUntil`),真发请求失败且错误码是 `Network` 时也写一次标记,60 秒后自动重试。429、额度用完、401 这些是网关**活着并且在回答**,不标记。

**实测(同一台机器、本机网关未启动、用户自己的 Key 有效)**

| 场景 | 改前 | 改后 |
|---|---|---|
| 冷启动后第 1 次翻译 | 5076 ms(其中网关两次连接 4510 ms) | **565 ms** |
| 第 2 次 | 231 ms | 216 ms |
| 第 3 次 | 216 ms | 293 ms |

剩下的 565 − 216 ≈ 350 ms 是到 DashScope 的 TLS 握手 + 首次 JIT,属于正常的冷启动代价;没有为了这 350 ms 去加人工预热请求。

**同时补的观测手段**:`FT_UITEST=translate` 钩子改成连续跑三次真实链路(三句不同文本,绕开 LRU 缓存),每次分别记录 `loading / call / provider / card` 四段耗时,这组数字是上面那张表的来源。

**验证**
- `dotnet build FastTranslation.slnx -c Release` → 0 警告 0 错误。
- `dotnet test FastTranslation.slnx -c Release --no-build` → **576 全绿**(Core 204 / Platform.Windows 87 / QwenMt **92** / Server 193),QwenMt 比 v57 多 3 个(见上)。
- 版本号三处同步到 **1.3.4**。

**改动文件**:`src/FastTranslation.Providers.QwenMt/QwenMtProvider.cs`(超时归属、`IsWorthRetrying`)、`src/FastTranslation.App/App.xaml.cs`(`CreateApiHttpClient` 统一三个 HttpClient、`ProbeGatewayAsync`、`_gatewayDownUntil`/`NoteGatewayFailure`、`FT_UITEST=translate` 三段计时)、`tests/FastTranslation.Providers.QwenMt.Tests/QwenMtProviderTests.cs`。

**残留风险**:①`_gatewayDownUntil` 是进程内的,换机器/重启都要重新探一次;②网关恢复后最长要等 60 秒才会再被问到;③探测只连 TCP 不看 HTTP,端口通但服务 500 的情况仍按原逻辑走(会重试一次)。④那 350 ms 的 TLS 冷启动没有处理,如果以后要求「第一次也 <300 ms」再考虑预热连接。


## v59 注释全面中文化（AGENTS 注释规范改为「一律中文」）

**背景**
- 用户要求仓库内注释一律中文，历史注释同样翻译。
- `AGENTS.md` 的「注释」小节第一条从「语言跟随文件」改为「**一律用中文**」，并明确标识符、类型名、路径、URL、命令、版本号、错误码与代码片段保持原样；第三方文件（如 `installer/Languages/ChineseSimplified.isl`）不动。

**改动**
- 范围：174 个已跟踪文本文件（+1583/−1643 行），只改注释文字，不动代码、字符串字面量与标识符。
- 分布：tools 32、server 30、tests 30、App 26、Core 20、Platform.Windows 18、deploy 7、Providers.QwenMt 3、installer 2、站点与根配置 6。
- 类型：`.cs` 118、`.xaml` 8、`.ps1` 32、`.sh` 4，`.iss`/`.wxs`/`.py`/`.css`/`.csproj`/`.props`/`.gitignore`/`.gitattributes`/`Caddyfile`/`.service`/cron 各 1。
- 26 个原本无 BOM 的 `.ps1` 补 BOM（PowerShell 5.1 读中文注释不乱码）；其余文件编码、行尾保持原状。
- 保留英文：标识符、类型名、路径、URL、命令、版本号、错误码、引用的英文异常消息，以及 `<summary>` 等 XML 标签本身。

**验证**
- 代码不变性：`publish/verify_comments.py` 按语言剥离注释后与 `HEAD` 逐行比较，174 个文件 0 处代码差异；残留英文散文注释 0。
- 语法：32 个改动 `.ps1` 全部通过 PowerShell AST 解析；4 个 `.sh` 通过 `bash -n`；11 个 XAML/XML/csproj/wxs 可解析；`.py` 可编译；`installer/FastTranslation.iss` 用 Inno Setup 6 编译通过（生成 1.3.4 安装包）。
- `dotnet build FastTranslation.slnx -c Release`：0 警告 0 错误；`dotnet test`：576 全绿（Core 204 / Platform.Windows 87 / Providers.QwenMt 92 / Server 193）。
- 注释中的中文标点统一为全角，行内代码片段、路径与命令保持半角。

**风险**
- 纯注释改动，无行为变化；构建、测试与安装器编译均证实。
- `core.autocrlf` 下 `git` 会提示 “LF will be replaced by CRLF”，属仓库既有归一化行为，与本次无关。
- `deploy/README.md` 等既有中文文档里的半角标点未在本轮统一。

## v60 配色与动效:四档自评色、页面渐变、卡片与悬停 ✅

**背景**
- 用户用 `/frontend-agent /ui-ux-pro-max` 提了三条：四档按钮要能靠颜色一眼分清（靖绿 / 雾蓝 / 橘黄 / 鹤红）、整页底色要换成从左上到右下的珠光白→雾蓝渐变、按钮与卡片要有悬停浮起与换页衔接。
- `ui-ux-pro-max` 的本地库没有 WPF 栈的条目（已如实告知用户）；它给的产品色板与仓库既定调色板不一致，按 `AGENTS.md`「配色只走 `ThemePalette`」不采纳，只取两条通用规则：动效必须给「减少动态效果」留退路、进入与离开要用不同缓动。

**改动**
- `src/FastTranslation.Platform.Windows/ThemePalette.cs`：`ThemeColors` 从 17 个字段扩到 32 个，新增四档自评的「文字 / 底色 / 描边」共 12 条、页面与侧栏渐变端点 4 条、投影色 1 条；浅深两套各自给值（浅色靖绿 `#0E7C66`、雾蓝 `#35648F`、橘黄 `#A9611A`、鹤红 `#A9453B`）。
- `src/FastTranslation.App/ThemeBridge.cs`：新增 12 个画刷键、`ShadowColor`（`DropShadowEffect` 要的是 `Color` 不是 `Brush`）、`LiftShadow`（悬停抬起，Blur 14 / Depth 2）、`CardShadow`（卡片常驻，Blur 18 / Depth 1 / Opacity 0.55），以及 `SetGradient` 组装的两支渐变（页面 0.15→0.85、侧栏 0→1，侧栏只取同一色相更窄的一段）。
- `src/FastTranslation.App/App.xaml`：默认按钮与主按钮重写模板——圆角 8、悬停抬起 1.5px 并叠一层淡投影、按下回沉 1px；新增全局 `Card`（圆角 12 + `CardShadow`）；新增四档样式族 `GradeButton` 与四个变体（悬停叠 9% 同色 veil、按下 16% 并下沉、禁用转中性灰）；`SidebarItem` 悬停整行右移 2px。
- `src/FastTranslation.App/MainWindow.xaml(.cs)`：窗口底色改 `PageGradientBrush`、侧栏改 `SidebarGradientBrush`；`ShowPage` 换页时新页面淡入（170ms）并从下方 10px 就位（210ms），只动页面宿主不动侧栏。
- 六个页面 `UserControl` 的 `Background` 由 `WindowBgBrush` 改成 `Transparent`，让渐变从窗口透上来；三份重复的本地 `Card` 样式删掉，统一用全局那份。
- `src/FastTranslation.App/Pages/ReviewPage.xaml`：四档按钮换用新样式族，页内本地样式表减去 40 余行。

**踩到的坑**
- `Border` 没有 `BorderDashArray` 这类属性可借用（揭开前的虚线槽此前用 `Rectangle` 画的），本轮保持 `Rectangle` 方案。
- `TranslateTransform` 带 `x:Name` 后不能当 `Setter TargetName`：`Freezable` 不在模板 name scope 里，会报 `MC4111 无法找到 Trigger 目标`。改成对容器设 `RenderTransform` 并内联 `<TranslateTransform Y="1"/>`。
- `Padding` 必须设在 `ContentPresenter` 上：留在外层 `Border` 时主按钮的蓝底会缩在描边里，截图里成了「小胶囊 + 外边框」。

**验证**
- `dotnet build FastTranslation.slnx -c Release`：0 警告 0 错误；`dotnet test`：576 全绿（Core 204 / Platform.Windows 87 / Providers.QwenMt 92 / Server 193）。
- 截图（1400×1000 逻辑、175% DPI 下 1820×1155）：`v59-home.png`、`v59-home-dark.png`（首页浅/深）、`v59-review-pick.png`（勾选列表）、`v59-review-card.png`（未揭开，四档置灰）、`v59-review-revealed.png` / `-dark.png`（揭开后四档上色）、`v59-hover-grade.png`（悬停抬起）。
- 四档颜色取色：忘记 `#FAEAE7` 底 / `#A9453B` 字、模糊 `#FBF0E0` / `#A9611A`、熟悉 `#E9F0F8` / `#35648F`、记住 `#E7F5F0` / `#0E7C66`。

**风险**
- 渐变是静态的，不随窗口尺寸重排；极端窄窗口下侧栏渐变压缩后色差变小，但仍在同一色相内。
- 换页动效已接 `SystemParameters.ClientAreaAnimation`：系统里关掉动画时直接到位、不做淡入位移；但悬停抬起与按钮下沉走的是样式触发器，没有单独的「不动效」分支。
- 悬停抬起依赖 `DropShadowEffect`，软件渲染兜底下会有轻微模糊，深色主题里不明显。
