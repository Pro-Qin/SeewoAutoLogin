# 希沃自动登录（独立版）

基于 [CJKmkp/SeewoAutoLogin](https://github.com/CJKmkp/SeewoAutoLogin)（ICC-CE 插件版）重构的 **Windows 独立应用（WPF）**，为希沃白板提供 SSO 快捷登录能力。

## 功能

- 多希沃账号管理：账号密码登录、希沃官方二维码登录（120s 倒计时、过期/拒绝/网络错误状态）
- 账号列表分「正在生效（最多 6 个）/ 暂未生效」两栏，支持备注、标签、设为当前、删除、排序、移入/移出生效区
- 主界面账号列表右下角蓝色「+」按钮：点击后加号旋转 45° 并弹出二级菜单（密码添加 / 扫码添加），直接跳转对应页面
- 本地 SSO 网关（端口 24300）：为希沃白板提供账号列表与登录令牌（`/getData/SSOLOGIN`、`/SSOLOGIN/{userId}`、`/SSOLOGOUT`、`/savedata`）
- 用户列表轮换：按 4/6 个账号分组，希沃快捷登录窗口短时间重开时切换账号组
- 扫码会话恢复与每日令牌自动刷新
- 账号密码 DPAPI 加密落盘；扫码令牌 DPAPI 加密存储（`%LOCALAPPDATA%\SeewoAutoLogin\Sessions`）
- 应用设置密码保护（进入设置/托盘切换账号需验证）
- 托盘常驻、开机自启（`--minimized`）、单实例、自动 UAC 提权
- **WebView2 运行时自动修复**：检测到未安装时自动下载官方安装器静默安装，安装成功后自动重试；失败时在主界面内显示原生修复面板（自动安装 / 重新检测 / 手动下载 / 查看日志），不再出现白屏
- **主界面加速**：启动时预热 WebView2 环境与浏览器进程；前端资源落盘后经虚拟主机映射加载（浏览器并行解析 CSS/JS，无 `NavigateToString` 的 2MB 上限），加载期间用遮罩覆盖，无白屏闪烁
- **首次打开主界面开场动画**：`Seewo Autologin` 标题与 `Made by Qin_zzq` 副标题渐显并自大缩小，随后向左渐隐，遮罩渐隐进入主界面（每个进程只播放一次，点击可跳过）
- **内置版本更新检查**：默认使用 GitHub 镜像源（多个备用源自动降级，全部失败时用 jsDelivr 兜底），发现新版本在主界面提示并提供下载入口，可在设置中关闭自动检查
- 应用图标（exe / 托盘 / 主界面品牌）为多尺寸 squircle + 蓝色渐变 + 闪电标识，托盘按系统 DPI 选取最清晰尺寸
- WebView2 管理界面、SeewoOverlay 悬浮窗（一键切换账号并重启希沃）
- 遮罩显示管理：设置界面可控制「希沃打开但未登录时自动显示切换遮罩」（默认关闭），托盘/设置独立手动「显示遮罩」
- 一键诊断（含 WebView2 运行时检测）、日志查看器、配置导出/导入、中英文界面

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
| `SeewoAutoLogin_Setup_v1.8.1.exe` | 约 8MB | 能联网：缺运行时由程序自动下载并静默安装 |
| `SeewoAutoLogin_Setup_v1.8.1_WithWebView2.exe` | 约 210MB | 断网 / 内网机器：安装时离线装好 WebView2，杜绝运行时缺失导致的白屏 |

- 安装：运行上述任一个安装包（Inno Setup 打包，需要管理员权限）。
- 卸载：控制面板卸载程序卸载。卸载时会以 `--uninstall` 启动应用，自动清理 hosts 中的 `local.id.seewo.com` 映射与本应用数据目录。

> 运行时自动 UAC 提权：希沃快捷登录依赖 hosts 映射与本地 SSO 网关，需要管理员权限。程序非管理员启动时会自动提权重启；若拒绝 UAC 会说明后果，可重试或以降级模式运行（降级下 SSO 快捷登录可能失效）。

## 数据与安全

- 配置：`%LOCALAPPDATA%\SeewoAutoLogin\config.json`（账号密码为 DPAPI 密文，带 `dpapi:` 前缀）
- 扫码令牌：`%LOCALAPPDATA%\SeewoAutoLogin\Sessions\*.bin`
- 前端资源：`%LOCALAPPDATA%\SeewoAutoLogin\web\`；WebView2 用户数据：`%LOCALAPPDATA%\SeewoAutoLogin\WebView2\`
- 日志：`%LOCALAPPDATA%\SeewoAutoLogin\Logs\yyyy-MM-dd.log`
- SSO 网关只监听本机（localhost / 127.0.0.1 / local.id.seewo.com），CORS 仅放行本机来源，令牌 Cookie 带 `HttpOnly`
- 更新检查只读取公开的版本信息（GitHub API / jsDelivr），不上传任何本地数据
- 请只在可信设备上使用，不要将配置文件与日志发送给他人。

## 与上游插件版的关系

本项目功能对齐并超过上游 ICC-CE 插件版：上游 2026-07-18~07-19 的「账号切换 / 账号持久化 / 登录校验」等功能已全部迁移；本项目额外提供独立安装包、托盘、自启动、生效区管理、SSO 请求统计、DPAPI 密码加密等能力。插件专用部分（InkCanvas.PluginSdk、manifest.json、ICC-CE 路径）不适用。

## 许可

GPL-3.0，见 [LICENSE](LICENSE)。上游项目同样为 GPL-3.0。
