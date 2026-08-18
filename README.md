# 希沃自动登录（独立版）

基于 [CJKmkp/SeewoAutoLogin](https://github.com/CJKmkp/SeewoAutoLogin)（ICC-CE 插件版）重构的 **Windows 独立应用（WPF）**，为希沃白板提供 SSO 快捷登录能力。

## 功能

- 多希沃账号管理：账号密码登录、希沃官方二维码登录（120s 倒计时、过期/拒绝/网络错误状态）
- 账号列表分「正在生效（最多 6 个）/ 暂未生效」两栏，支持备注、标签、设为当前、删除、排序、移入/移出生效区
- 本地 SSO 网关（端口 24300）：为希沃白板提供账号列表与登录令牌（`/getData/SSOLOGIN`、`/SSOLOGIN/{userId}`、`/SSOLOGOUT`、`/savedata`）
- 用户列表轮换：按 4/6 个账号分组，希沃快捷登录窗口短时间重开时切换账号组
- 扫码会话恢复与每日令牌自动刷新
- 账号密码 DPAPI 加密落盘；扫码令牌 DPAPI 加密存储（`%LOCALAPPDATA%\SeewoAutoLogin\Sessions`）
- 应用设置密码保护（进入设置/托盘切换账号需验证）
- 托盘常驻、开机自启（`--minimized`）、单实例、自动 UAC 提权
- WebView2 管理界面、SeewoOverlay 悬浮窗（未登录自动遮罩、一键切换账号并重启希沃）
- 一键诊断、日志查看器、配置导出/导入、中英文界面

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

## 安装 / 卸载

- 安装：运行 `SeewoAutoLogin_Setup_v1.7.8.exe`（Inno Setup 打包，需要管理员权限）。
- 卸载：控制面板卸载程序卸载。卸载时会以 `--uninstall` 启动应用，自动清理 hosts 中的 `local.id.seewo.com` 映射与本应用数据目录。

## 数据与安全

- 配置：`%LOCALAPPDATA%\SeewoAutoLogin\config.json`（账号密码为 DPAPI 密文，带 `dpapi:` 前缀）
- 扫码令牌：`%LOCALAPPDATA%\SeewoAutoLogin\Sessions\*.bin`
- 日志：`%LOCALAPPDATA%\SeewoAutoLogin\Logs\yyyy-MM-dd.log`
- SSO 网关只监听本机（localhost / 127.0.0.1 / local.id.seewo.com），CORS 仅放行本机来源，令牌 Cookie 带 `HttpOnly`
- 请只在可信设备上使用，不要将配置文件与日志发送给他人。

## 与上游插件版的关系

本项目功能对齐并超过上游 ICC-CE 插件版：上游 2026-07-18~07-19 的「账号切换 / 账号持久化 / 登录校验」等功能已全部迁移；本项目额外提供独立安装包、托盘、自启动、生效区管理、SSO 请求统计、DPAPI 密码加密等能力。插件专用部分（InkCanvas.PluginSdk、manifest.json、ICC-CE 路径）不适用。

## 许可

GPL-3.0，见 [LICENSE](LICENSE)。上游项目同样为 GPL-3.0。
