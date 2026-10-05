# Windows 7 兼容说明

> 本说明对应 **`win7` 兼容分支**（.NET Framework 4.8 / Windows 7 SP1）。
> README 与本文档以 `main` 为唯一来源，改完由 `.github/workflows/sync-docs-to-win7.yml` 自动同步到 `win7` 分支——
> 要改就改 `main` 这一份，别去改分支上的副本（那份会被覆盖）。

## 与主干（main）的差异

| 项目 | main | win7 分支 |
|---|---|---|
| 目标框架 | .NET 8（Windows 19041+） | **.NET Framework 4.8** |
| 最低系统 | Windows 10 19041 | **Windows 7 SP1**（安装包 `MinVersion=6.1sp1`） |
| 位数 | x64 | x64（32 位系统直接拒绝安装） |
| 发布形态 | 单文件 exe | exe + 依赖 DLL 目录（免安装版打包成 zip） |
| 安装包名 | `..._Setup_vX.Y.Z.exe` | `..._Setup_vX.Y.Z_Win7.exe` |
| 自动更新 | 走主干 Release | **走 Win7 分支自己的 Release**（tag 带 `-win7`、标记为 pre-release；只会看到 Win7 版，绝不会把用户升级成主干版本） |

为保证两边能互相覆盖安装（同一个 `AppId` 与安装目录），安装包本身不做区分，靠文件名后缀和 `MinVersion` 区分。

## 三个前提

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

## 已知限制

- Win7 上如果被别的软件（如 Edge）升到了 110+ 的运行时，主界面会加载不出来：需要卸载该运行时并重装 109 版本。程序日志会记录检测到的版本号。
- 109 是 Win7 上 WebView2 的终点，**不再有功能与安全更新**（Win7 本身也已于 2023-01-10 结束支持）。
  这个分支解决的是「还能不能用」，不等于安全基线仍然达标 —— 能升到 Win10/11 的机器请优先用主干版本。
- **Win7 需要在系统层启用 TLS 1.2**：.NET Framework 4.7+ 的 `ServicePointManager` 默认是 `SystemDefault`（由 Schannel 决定协议），
  微软明确不建议硬编码协议版本，所以程序不动这个设置；而 Win7 SP1 的 Schannel 默认不开 TLS 1.2 客户端，
  只接受 TLS 1.2+ 的服务端（希沃接口）会握手失败，症状是「添加账号失败 / 网络错误 / 登录信息过期」。
  程序启动时会把检测结果写进日志（`[网络] ...` 一行），并在拥有管理员权限时自动写入启用所需的注册表项。
  手工处理办法（管理员，改完重启系统）：先装 [`KB3140245`](https://support.microsoft.com/help/3140245)，再在
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
- 或者直接让合作方那台 Win7 机器跑，并回传 `%LOCALAPPDATA%\SeewoAutoLogin\Logs\` 里的日志：
  日志里已经包含运行时版本、TLS 结果、网关状态与自启结果，比「打不开」三个字有用得多。
- 诊断包：主界面 `设置 → 维护与诊断 → 导出诊断包`，一次就能把系统版本、WebView2 版本、hosts、端口、日志摘要带出来。

**结论**：不需要为了这个分支常备一台 Win7。
改造阶段靠 net48 编译 + 本机运行就够了；发布前必须有一次 Win7 真机（或虚拟机）验收，重点是上面 5 条。
