# SeewoAutoLogin（希沃自动登录）

> [!WARNING]
> **维护状态**：作者在学校的岗位发生了变更，学校的一体机也更换了，本项目进入低频维护。
> 程序依旧可以正常使用，现有功能不受影响；反馈和 issue 照常会看，只是修复与新功能的节奏会明显放慢。

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/assets/project-map-dark.svg?v=20261004b">
  <img alt="SeewoAutoLogin 项目版图" src="docs/assets/project-map-light.svg?v=20261004b">
</picture>

基于 [CJKmkp/SeewoAutoLogin](https://github.com/CJKmkp/SeewoAutoLogin)（ICC-CE 插件版）重构的 **Windows 独立应用（WPF）**，为希沃白板提供 SSO 快捷登录能力。

> [!WARNING]
> **运行环境**：Windows 10 19041（20H1）或更高版本 / Windows 11，64 位。  
> Windows 7 **不能使用本版本** ，请改用 Win7 兼容分支 [`win7`](https://github.com/Pro-Qin/SeewoAutoLogin/tree/win7)，下载时选择 **后缀有`win7`** 的下载
   
> 项目主页：<https://pro-qin.github.io/SeewoAutoLogin/>   
> 下载：<https://github.com/Pro-Qin/SeewoAutoLogin/releases/latest>

## 功能

- 多账号管理：密码登录与希沃官方二维码登录，支持备注、标签、排序
- 账号分「正在生效 / 暂未生效」两栏，最多 6 个参与轮换
- 本地 SSO 网关：在希沃登录界面提供快捷登录入口与账号列表
- 扫码会话自动恢复、每日令牌刷新、一键账号健康巡检
- 账号密码与扫码令牌经 Windows DPAPI 加密落盘，进入设置需口令
- 托盘常驻、单实例、自动提权；开机自启走计划任务，开机不弹 UAC
- 自动修复与一键诊断：hosts 映射、网关端口、开机自启异常时自愈
- 内置更新检查（多源加速 + SHA256 校验）、配置备份与导入导出
- 日志查看器（等级着色、筛选、导出）与疑难解答页（逐项自检、一键复制环境摘要）
- WebView2 缺失自动修复、内置使用教程、中英文界面

## 常见问题

### 添加密码账号提示「登录失败：由于目标计算机积极拒绝，无法连接。 (127.0.0.1:10809)」

本地代理软件退出后，Windows 里可能仍残留指向本机端口的系统代理设置，所有请求都会打到没人监听的端口上：既加不了账号，SSO 网关也拿不到令牌，希沃侧就会提示「登录信息过期」。

从 v1.8.2 起程序会自动识别这种残留代理并改为直连（日志中会写「系统代理 xxx 未在运行，已自动改为直连」），不需要手动关闭系统代理；如果确实需要经代理出网（例如校园网必须走代理访问外网），先启动代理软件再使用本程序即可。

### 希沃里看不到「快捷登录 / 账号列表」入口

SSO 入口只在 `local.id.seewo.com:24300` 由本程序应答时才会出现。如果 24300 被希沃自带的 **EasiAgent** 占用（本程序会把网关启动到备用端口并告警），希沃就只会看到 EasiAgent 的响应，入口不会出现。

处理：默认开启的自动修复会在检测到端口不符时自动接管 24300；也可在主界面状态栏点「一键修复」手动触发。修复后重新打开希沃的登录界面即可看到入口。

### 希沃快捷登录后提示「登录信息过期」

该提示表示 SSO 网关没有拿到有效登录令牌，常见原因与处理：

1. 打开「诊断」查看日志中的 `SSOLOGIN/...: 登录失败 - ...`，里面会写明具体原因；
2. 提示 `网络连接失败`：按上一条检查网络 / 代理；
3. 提示 `账户不存在` / `账号或密码错误`：保存的账号密码已失效，重新用密码添加账号；
4. 扫码账号提示 `Token 换发失败，需要重新扫码`：扫码会话已过期，重新扫码登录一次。

### 导入配置后所有账号都提示「密码无法解密 / 需要重新录入密码」

密码密文由 **Windows 的 DPAPI 按「当前用户」加密**，换一台电脑、换一个 Windows 账户、重装系统后，同一份密文都解不回明文——这是加密本身的边界，不是文件损坏。

从 v1.14.27 起导入时会逐条试解密并把结论直接告诉你：哪些账号能用、哪些必须重新录入密码、哪些原本是扫码账号需要重扫。处理方式：

1. 重新录入密码（账号卡片上的编辑入口），或
2. 改用「导出配置包（.swalcfg）」在两台机器之间迁移：包内密文在导入端用目标机器的密钥重新加密，不依赖导出端的 DPAPI

导入**旧版 JSON 配置**跨机器时必然出现这个提示；原样带着旧密文的账号一律标为异常，直到你重录。

### 日志在哪，怎么给开发者看

设置 → 诊断 → 打开日志窗口，右上角「导出日志」把它存到桌面（已自动把手机号打码）。日志里能直接看到 `[App] 启动 v1.14.27（构建于 ...）`、SSO 请求结果、SSO 网关与自启的每一次变更。

提 issue 时请贴**出问题那几分钟**的内容，并把学号、姓名、手机号打码——issue 是公开的。

## 构建

需要 Windows 和 .NET 8 SDK：

```powershell
dotnet restore SeewoAutoLogin.csproj
dotnet build SeewoAutoLogin.csproj -c Release
```

单文件发布（与 setup.iss 的 Source 路径一致）：

```powershell
dotnet publish SeewoAutoLogin.csproj -c Release -r win-x64 --self-contained false -o bin\Release\net8.0-windows10.0.19041.0\publish
```

依赖：WebView2 运行时。轻量安装包在缺失时由程序自动下载安装；**内置 WebView2 版**把官方离线运行时安装器打进安装包，安装时检测到缺失才静默安装（适合断网或无法联网下载运行时的机器）。打包发布由 GitHub Actions 完成：推送 `v*` 标签即触发 `.github/workflows/release.yml` 编译两种安装包并创建 Release。

内置版安装包也可本地编译（需已安装 Inno Setup）：

```powershell
# 先把官方离线运行时安装器放到 publish\（约 203MB）
Invoke-WebRequest -Uri "https://go.microsoft.com/fwlink/?linkid=2124701" -OutFile publish\MicrosoftEdgeWebView2RuntimeInstallerX64.exe
iscc /DBundleWebView2=1 setup.iss
```

## 安装 / 卸载

Release 提供两种安装包，功能完全一致，区别只是 WebView2 运行时怎么来：

| 安装包 | 体积 | 适用场景 |
|---|---|---|
| `SeewoAutoLogin_Setup_v1.12.10.exe` | 约 8MB | 能联网：缺运行时由程序自动下载并静默安装 |
| `SeewoAutoLogin_Setup_v1.12.10_WithWebView2.exe` | 约 210MB | 断网 / 内网机器：安装时离线装好 WebView2，杜绝运行时缺失导致的白屏 |

- 安装：运行上述任一个安装包（Inno Setup 打包，需要管理员权限）。
- 卸载：控制面板卸载程序卸载。卸载时会以 `--uninstall` 启动应用，自动清理 hosts 中的 `local.id.seewo.com` 映射与本应用数据目录。

> 运行时自动 UAC 提权：希沃快捷登录依赖 hosts 映射与本地 SSO 网关，需要管理员权限。程序非管理员启动时会自动提权重启；若拒绝 UAC 会说明后果，可重试或以降级模式运行（降级下 SSO 快捷登录可能失效）。

## 数据与安全

- 配置：`%LOCALAPPDATA%\SeewoAutoLogin\config.json`（账号密码为 DPAPI 密文，带 `dpapi:` 前缀）
- 扫码令牌：`%LOCALAPPDATA%\SeewoAutoLogin\Sessions\*.bin`
- 前端资源：`%LOCALAPPDATA%\SeewoAutoLogin\web\`；WebView2 用户数据：`%LOCALAPPDATA%\SeewoAutoLogin\WebView2\`
- 日志：`%LOCALAPPDATA%\SeewoAutoLogin\Logs\yyyy-MM-dd.log`
- SSO 网关只服务本机：除 CORS 仅放行本机来源、令牌 Cookie 带 `HttpOnly` 外，还会在请求入口校验来源地址必须是回环（127.0.0.1 / ::1），局域网内其它机器伪造 Host 头也无法拉取账号列表或换取令牌
- 应用设置口令为 PBKDF2-SHA256（12 万次迭代 + 随机盐），哈希值本身再用 DPAPI 加密后才写入配置；连续输错 5 次会临时锁定
- 更新检查只读取公开的版本信息（GitHub API / jsDelivr），不上传任何本地数据
- 请只在可信设备上使用，不要将配置文件与日志发送给他人。

## 与上游插件版的关系

本项目功能对齐并超过上游 ICC-CE 插件版：上游 2026-07-18~07-19 的「账号切换 / 账号持久化 / 登录校验」等功能已全部迁移；本项目额外提供独立安装包、托盘、自启动、生效区管理、SSO 请求统计、DPAPI 密码加密等能力。插件专用部分（InkCanvas.PluginSdk、manifest.json、ICC-CE 路径）不适用。

## 许可

GPL-3.0，见 [LICENSE](LICENSE)。上游项目同样为 GPL-3.0。


## 代码签名与自动更新

发布流水线支持 Authenticode 签名，需要自备代码签名证书：

1. 在 GitHub 仓库 Settings -> Secrets and variables -> Actions 添加：
   - `SIGNING_PFX_BASE64`：PFX 证书文件的 Base64，可用 `[Convert]::ToBase64String([IO.File]::ReadAllBytes("cert.pfx"))` 生成
   - `SIGNING_PFX_PASSWORD`：PFX 密码
2. 把证书指纹填进 `Services/AuthenticodeVerifier.cs` 的 `AllowedSignerThumbprints`（SHA-256 或 SHA-1 都支持）。
3. 打 tag 后，release.yml 会先给单文件 exe 和安装包签名，再生成 SHA256SUMS.txt。

没有配置 secrets 时流水线仍可发布，但更新器只校验 SHA256。注意：`WinVerifyTrust` 要求证书链受信任；如果使用自签名证书，需要先把根证书安装到目标机器的"受信任的根证书颁发机构"。

更新源只允许 GitHub 官方域名和 jsDelivr 等白名单镜像；自定义源命中白名单之外会回退到默认官方源。

## 卸载与迁移

- 卸载或迁移前必须运行安装目录里的卸载程序（`unins000.exe`），或执行 `SeewoAutoLogin.exe --uninstall`。
  只有卸载流程会清理开机自启计划任务、hosts 中的 `local.id.seewo.com` 映射和本地数据。
- 设置里可以开启"退出时恢复 hosts"；开启后每次正常退出都会移除本程序的 hosts 映射，下次启动自动写回。
- 直接删除程序目录不会触发上述清理，可能残留 hosts 映射和开机自启任务。

## 更多文档

- [安全与威胁模型](docs/SECURITY.md)
- [部署与迁移](docs/DEPLOYMENT.md)
- [兼容性矩阵](docs/COMPATIBILITY.md)
- [Windows 7 兼容说明](docs/WIN7.md)（win7 分支的前提、限制与验证方法）
- [结构改良记录](docs/REFACTORING.md)（目录约定、异常与消息边界、还留着的债）
- [界面文案（i18n）](docs/I18N.md)（改文案改哪里、怎么加一门语言）

- [隐私政策](docs/PRIVACY.md)
- [合规与许可](docs/COMPLIANCE.md)
