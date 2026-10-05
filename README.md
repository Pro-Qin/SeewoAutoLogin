# SeewoAutoLogin（希沃自动登录）

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/assets/project-map-dark.svg?v=20261004b">
  <img alt="SeewoAutoLogin 项目版图" src="docs/assets/project-map-light.svg?v=20261004b">
</picture>

基于 [CJKmkp/SeewoAutoLogin](https://github.com/CJKmkp/SeewoAutoLogin)（ICC-CE 插件版）重构的 **Windows 独立应用（WPF）**，为希沃白板提供 SSO 快捷登录能力。

> [!IMPORTANT]
> **这是 `win7` 兼容分支**：目标框架降到 .NET Framework 4.8，让 Windows 7 SP1 也能跑。
> 主干（`main`）要求 Windows 10 19041+，两边构建出来的安装包**不能混装**。

> [!WARNING]
> **运行环境**：Windows 7 **SP1** / 8.1 / 10 / 11，**64 位**（32 位系统不支持）。
> 需要 .NET Framework 4.8 与 WebView2 运行时（Win7 上最高 109 系列）——详见下方「Windows 7 兼容说明」。

> 项目主页：<https://pro-qin.github.io/SeewoAutoLogin/> | 下载：<https://github.com/Pro-Qin/SeewoAutoLogin/releases/latest>

## 功能

- 多希沃账号管理：账号密码登录、希沃官方二维码登录（120s 倒计时、过期/拒绝/网络错误状态）
- 账号列表分「正在生效（最多 6 个）/ 暂未生效」两栏，支持备注、标签、设为当前、删除、排序、移入/移出生效区
- 主界面账号列表右下角蓝色「+」按钮：点击后加号旋转 45° 并弹出二级菜单（密码添加 / 扫码添加），直接跳转对应页面
- 本地 SSO 网关（端口 24300）：为希沃白板提供账号列表与登录令牌（`/getData/SSOLOGIN`、`/SSOLOGIN/{userId}`、`/SSOLOGOUT`、`/savedata`）
- 用户列表轮换：按 4/6 个账号分组，希沃快捷登录窗口短时间重开时切换账号组
- 扫码会话恢复与每日令牌自动刷新
- 账号密码 DPAPI 加密落盘；扫码令牌 DPAPI 加密存储（`%LOCALAPPDATA%\SeewoAutoLogin\Sessions`）
- 应用设置密码保护（进入设置/托盘切换账号需验证）
- 托盘常驻、单实例、自动 UAC 提权
- **开机自启（v1.9.0 起为计划任务）**：首次启动的欢迎界面会显示「开机自动启动」选项并回显当前状态（默认勾选、可当场取消）；自启改用**最高权限计划任务**（登录触发 + `RL HIGHEST`），开机后不再弹 UAC。创建失败时回退 HKCU 启动项，旧版本/安装包写入的注册表启动项会在管理员运行时**自动升级**为计划任务；启动项缺失或程序换目录导致路径失效时也会自动重建；卸载时一并清理
- **WebView2 运行时自动修复**：检测到未安装时自动下载官方安装器静默安装，安装成功后自动重试；失败时在主界面内显示原生修复面板（自动安装 / 重新检测 / 手动下载 / 查看日志），不再出现白屏
- **主界面加速**：启动时预热 WebView2 环境与浏览器进程；前端资源落盘后经虚拟主机映射加载（浏览器并行解析 CSS/JS，无 `NavigateToString` 的 2MB 上限），加载期间用遮罩覆盖，无白屏闪烁
- **首次打开主界面开场动画**：`Seewo Autologin` 标题与 `Made by Qin_zzq` 副标题渐显并自大缩小，随后向左渐隐，遮罩渐隐进入主界面（每个进程只播放一次，点击可跳过）
- **内置版本更新检查**：默认使用 GitHub 镜像源（多个备用源自动降级，全部失败时用 jsDelivr 兜底），发现新版本在主界面提示并提供下载入口，可在设置中关闭自动检查
- 应用图标（exe / 托盘 / 主界面品牌）为多尺寸 squircle + 蓝色渐变 + 闪电标识，托盘按系统 DPI 选取最清晰尺寸
- WebView2 管理界面、SeewoOverlay 悬浮窗（一键切换账号并重启希沃）
- 遮罩显示管理：设置界面可控制「希沃打开但未登录时自动显示切换遮罩」（默认关闭），托盘/设置独立手动「显示遮罩」
- 一键诊断（含 WebView2 运行时检测）、日志查看器、配置导出/导入、中英文界面
- **开机自启升级为最高权限计划任务**：登录触发、最高权限运行，开机不再弹 UAC；创建失败自动回退注册表启动项，卸载时一并清理；若配置要求自启但系统里启动项缺失（被杀软清理、程序换目录）会自动重建
- **自动修复（默认开启）**：启动后与运行期间定时自检，hosts 缺失自动重建、SSO 网关异常自动重启、端口被 EasiAgent 占用时自动接管、开机自启缺失自动重建，不依赖打开主界面
- **主界面自检状态栏**：实时显示 SSO 网关端口、hosts 映射、管理员权限、希沃进程状态，异常项标红并提供「一键修复」（重写 hosts 映射 + 重启网关）
- **账号健康巡检**：一键校验每个账号的密码 / 扫码令牌是否仍然有效，失效账号在列表中标记并给出具体原因（凭据解不开会提示"需要重新录入"，不再误报"密码错误"）
- **批量导入账号**：粘贴多行「账号,密码[,备注]」即可批量添加
- **配置自动备份与还原**：配置每次变化自动归档（最多保留 20 份，内容去重），设置页可查看备份并一键恢复；配置改为原子写入并保留 `.bak`
- **SSO 网关安全加固**：只服务本机回环来源（局域网其它机器即使伪造 `Host: local.id.seewo.com` 也会被 403 拒绝）；24300 端口被占用时自动切换备用端口；开启自动修复时按授权自动结束 EasiAgent 并接管 24300，关闭自动修复时仍会先弹窗确认
- **凭据与日志加固**：应用设置口令改为 PBKDF2（12 万次迭代）+ 加盐，哈希值再用 DPAPI 加密落盘，并带失败次数锁定；日志按 5MB 轮转、保留 14 天，多线程写入不再丢日志
- **更新通道收敛**：只信任 GitHub 官方 API（第三方镜像源移除），下载地址强制 https + 主机白名单校验，不再把任意 URL 交给 `ShellExecute` 打开
- **内置使用教程（v1.10.0）**：首次启动在欢迎界面询问是否观看；教程依次演示「从任务栏托盘打开主界面」→「右下角 + 号与侧边栏两种添加账号入口」（聚光灯平滑跟随）→「添加后希沃登录界面长什么样」→ 完成。设置 → 帮助与维护里可随时重看
- **恢复出厂设置（v1.10.0）**：一键清空账号、扫码凭据、全部设置与配置备份，完成后自动重启回到首次引导流程（设置 → 帮助与维护）
- **更新包按网络动态选源（v1.10.0）**：下载更新时并发探测官方直链与 5 个常用加速通道，挑最快可用的下载，边下边校验 SHA256（发版时随 Release 提供 `SHA256SUMS.txt`），校验不通过自动换源；下载进度实时显示
- **残留代理自动回退（v1.8.2）**：系统里残留的本地代理（如 `127.0.0.1:10809`）端口不可达时自动改为直连；走代理的请求在传输层失败也会自动回退直连，避免「添加密码账号失败」和希沃侧「登录信息过期」

## 常见问题

### 添加密码账号提示「登录失败：由于目标计算机积极拒绝，无法连接。 (127.0.0.1:10809)」

本地代理软件（Clash / v2ray 等）退出后，Windows 里可能仍残留指向本机端口的系统代理设置，所有请求都会打到没人监听的端口上：既加不了账号，SSO 网关也拿不到令牌，希沃侧就会提示「登录信息过期」。

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

## 构建

需要 Windows 和 .NET 8 SDK（编译 net48 目标要用它，Windows 上还要有 .NET Framework 4.8 的 targeting pack；
装了 Visual Studio 或 .NET Framework 4.8 Developer Pack 就有）：

```powershell
dotnet restore SeewoAutoLogin.csproj
dotnet build SeewoAutoLogin.csproj -c Release
dotnet test  tests\SeewoAutoLogin.Tests\SeewoAutoLogin.Tests.csproj -c Release
```

发布（net48 不支持单文件发布，产物是「exe + 依赖 DLL」的目录）：

```powershell
dotnet publish SeewoAutoLogin.csproj -c Release -o bin\Release\net48\publish
```

依赖：.NET Framework 4.8 + WebView2 运行时。安装包会检测 .NET Framework 4.8（缺了就提示下载并中止安装）；
WebView2 在 Win10/11 上由程序自动安装，在 Win7 上按下面「Windows 7 兼容说明」处理。
打包发布由 GitHub Actions 完成：推送 `v*` 标签触发 `.github/workflows/release.yml`。
Win7 分支建议用 `vX.Y.Z-win7` 这样的 tag，避免和主干的 tag 撞名。

内置版安装包也可本地编译（需已安装 Inno Setup）：

```powershell
# 先把 WebView2 409 版离线安装器放到 publish\：
# 必须是 109.0.1518.78（Win7 能用的最后一版），不要去下 go.microsoft.com 的最新版
iscc /DBundleWebView2=1 setup.iss
```

## Windows 7 兼容说明

### 与主干（main）的差异

| 项目 | main | win7 分支 |
|---|---|---|
| 目标框架 | .NET 8（Windows 19041+） | **.NET Framework 4.8** |
| 最低系统 | Windows 10 19041 | **Windows 7 SP1**（安装包 `MinVersion=6.1sp1`） |
| 位数 | x64 | x64（32 位系统直接拒绝安装） |
| 发布形态 | 单文件 exe | exe + 依赖 DLL 目录（免安装版打包成 zip） |
| 安装包名 | `..._Setup_vX.Y.Z.exe` | `..._Setup_vX.Y.Z_Win7.exe` |
| 自动更新 | 走主干 Release | **走 Win7 分支自己的 Release**（tag 带 `-win7`、标记为 pre-release；只会看到 Win7 版，绝不会把用户升级成主干版本） |

为保证两边能互相覆盖安装（同一个 `AppId` 与安装目录），安装包本身不做区分，靠文件名后缀和 `MinVersion` 区分。

### 三个前提

1. **Windows 7 SP1，64 位**。SP1 之前或 32 位系统不在支持范围内。
2. **.NET Framework 4.8**。Win7 默认不带；安装包启动时检测 `HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full` 的 `Release`，
   低于 528040 就提示去官方页面下载并中止安装。
   官方对 Win7 的要求是 **SP1 + 离线安装前先装 Microsoft Root Certificate Authority 2011**
   （[安装说明](https://learn.microsoft.com/en-us/previous-versions/dotnet/framework/install/on-windows-7)）；
   实践中安装器报「证书链错误 / 时间戳签名无法验证」通常还缺 SHA-2 代码签名支持（`KB4474419`）
   与提供 `d3dcompiler_47.dll` 的 `KB4019990`。
   自己确认是否已装：

   ```powershell
   (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full').Release   # >= 528040 即已装 4.8
   ```
3. **WebView2 运行时 109**（109 系列，最后一版，例如 `109.0.1518.78`）。这是 Win7/8.1 上能用的**最后一版**
   （[微软公告](https://blogs.windows.com/msedgedev/2022/12/09/microsoft-edge-and-webview2-ending-support-for-windows-7-and-windows-8-8-1/)：
   Edge 与 WebView2 运行时自 110 起不再支持 Win7/8.1，WebView2 **SDK 1.0.1519.0** 起同样如此，
   所以本项目把 SDK 锁在 `1.0.1462.37`，升级依赖时不要动它）。
   自动安装仍然可用：Evergreen Bootstrapper 在 Win7 上会装到 109；程序装完会校验版本号，
   万一拿到 110 以上会明确报错并引导改用手动安装 109（固定版本 Fixed Version → x64）。

### 已知限制

- Win7 上如果被别的软件（如 Edge）升到了 110+ 的运行时，主界面会加载不出来：需要卸载该运行时并重装 109 版本。程序日志会记录检测到的版本号。
- 109 是 Win7 上 WebView2 的终点，**不再有功能与安全更新**（Win7 本身也已于 2023-01-10 结束支持）。
  这个分支解决的是「还能不能用」，不等于安全基线仍然达标 —— 能升到 Win10/11 的机器请优先用主干版本。
- **Win7 需要在系统层启用 TLS 1.2**：.NET Framework 4.7+ 的 `ServicePointManager` 默认是 `SystemDefault`（由 Schannel 决定协议），
  微软明确不建议硬编码协议版本，所以程序不动这个设置；而 Win7 SP1 的 Schannel 默认不开 TLS 1.2 客户端，
  只接受 TLS 1.2+ 的服务端（希沃接口）会握手失败，症状是「添加账号失败 / 网络错误 / 登录信息过期」。
  程序启动时会把检测结果写进日志（`[网络] ...` 一行）。启用办法（管理员，改完重启系统）：
  先装 [`KB3140245`](https://support.microsoft.com/help/3140245)，再在
  `HKLM\SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols\TLS 1.2\Client`
  下建 `Enabled`=DWORD `1`、`DisabledByDefault`=DWORD `0`。
- 应用清单没有声明 DPI 感知级别：WPF 在 net48 上默认是 System DPI aware，而 Win7 会忽略
  `dpiAwareness`（该写法从 Win10 1607 起才生效），所以高 DPI（125%/150%）下界面按系统缩放渲染，可能不如主干清晰。
  这是 Win7 的固有限制，加 manifest 也提不到 Per-Monitor。
- 更新通道与主干物理隔离：Win7 版只会看到 tag 带 `-win7` 的发布，主干版本用同一条规则把自己排除在外，两边互不干扰。

## 怎么在没有 Win7 真机的情况下验证

把「兼容性」拆成两层，只有一层真的需要 Win7：

**第一层：编译期与 API 面（不需要 Win7，本机就能做，且能拦住绝大多数「装完打不开」）**

`Microsoft.NETFramework.ReferenceAssemblies`（v4.8 targeting pack）只暴露 4.8 真实存在的 API：
只要换成 `net48` 编译通过，就不可能出现「调用了 .NET 8 才有的方法」这类在 Win7 上必崩的问题。
这一层就是本项目已经完成的改造——`CryptoCompat` / `AesGcmCompat` / `IsExternalInit` 都是这么补出来的。

```powershell
# 本机（Win10/11）就能跑，验证 API 面 + 架构 + 业务逻辑
dotnet build SeewoAutoLogin.csproj -c Release
dotnet test  tests\SeewoAutoLogin.Tests\SeewoAutoLogin.Tests.csproj -c Release
# 交叉检查 PE 架构：exe 与 WebView2Loader.dll 必须都是 x64
```

本机运行 net48 产物也有意义：Win7 与 Win10 上跑的是**同一个 CLR**，程序逻辑、DPAPI、WPF 行为基本一致，
能提前发现界面与网关层的 bug。它测不出来的是系统级差异（见下一层）。

**第二层：系统级行为（必须在真的 Win7 上，无法用模拟器代替）**

真正只能在 Win7 上验证的只有这几件，且都能在装完后 10 分钟内测完：

1. WebView2 109 能否初始化（打开主界面，看到界面而不是白屏/修复面板）
2. HTTPS 是否走得通（添加一个密码账号、检查更新页面能否打开）
3. WPF 界面能否正常渲染（字体、图标、遮罩、托盘）
4. 计划任务自启能否创建（`schtasks /query /tn` 看得到，日志无报错）
5. hosts 写入与 SSO 网关（`local.id.seewo.com:24300` 能在希沃登录界面看到快捷登录入口）

**不建议的做法**：Windows 的「兼容模式」不改 API 可用性，对这类问题没有任何参考价值；
用 Win10 假装 Win7 也不成立。真机/虚拟机成本其实很低：

- 一次性验收：自备 Win7 SP1 ISO，用 VirtualBox / VMware 建一台虚拟机，
  装上 .NET Framework 4.8 与 WebView2 109，跑一遍上面 5 条。
  （微软官方的 Win7 评估版/ISO 下载页、以及 Edge 开发者虚拟机镜像都已下线，ISO 只能自己有；
  GitHub Actions 的官方 runner 也没有 Win7，别指望在 CI 里自动跑。）
- 或者直接让合作方那台 Win7 机器跑——**但请让他先跑本仓库 `scripts/` 之外的这套最小检查**，
  并回传 `%LOCALAPPDATA%\SeewoAutoLogin\Logs\` 里的日志：日志里已经包含运行时版本、TLS 结果、网关状态与自启结果，
  比「打不开」三个字有用得多。
- 诊断包：主界面 `设置 → 维护与诊断 → 导出诊断包`，一次就能把系统版本、WebView2 版本、hosts、端口、日志摘要带出来。

**结论**：不需要为了这个分支常备一台 Win7。
改造阶段靠 net48 编译 + 本机运行就够了；发布前必须有一次 Win7 真机（或虚拟机）验收，重点是上面 5 条。

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

- [隐私政策](docs/PRIVACY.md)
- [合规与许可](docs/COMPLIANCE.md)