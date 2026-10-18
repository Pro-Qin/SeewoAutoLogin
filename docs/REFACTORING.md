# 结构改良记录

这份文档说明本项目在 2026-10 做的一次系统性结构改良：**只动结构，不动行为**。
读它的目的不是回顾历史，而是让你在下次动手前知道「边界在哪、约定是什么、为什么这么切」。

---

## 一、当时的问题

不是「代码不好看」，而是四类会实际咬人的问题：

1. **消息协议没有契约。**
   前端发一条 JSON，C# 用 `root.GetProperty("x").GetString()` 直接取。字段缺失抛 `KeyNotFoundException`、
   类型不符抛 `InvalidOperationException`，两者都被消息循环的 catch 吞成一行日志。
   用户看到的是「点了没反应」，日志里只有「消息处理异常」，看不出是哪个字段。

2. **异常边界靠运气。**
   十几个 `async void` 命令处理器：谁都没 await，异常直接跳到 `DispatcherUnhandledException`。
   轻则一条日志看不出是哪个操作炸的，重则整个进程被带走 —— 而这是装在教室一体机上、
   老师上课时不能消失的程序。

3. **单文件即模块。**
   `ManagementWindow.Handlers.cs` 1057 行装下六类职责；`App.xaml.cs` 994 行里 OnStartup 一段 340 行；
   `UpdateChecker.cs` 一个文件四个类型。改一处要在 1000 行里找位置，review 时也没法只看一块。

4. **前端入口文件是所有东西。**
   `app.js` 1996 行，通信、导航、账号列表、教程、粒子、更新提示全在一个全局作用域里。
   改一个页面要滚动整个文件，而且「改前端必须抬版本号」这条自保规则（见 .learnings/LRN-20260925-005）
   本质上是缓存机制设计不当的补偿。

另外，60+ 个文件各自贴一份同样的 11 行 `using` 墙，partial class 的每个分片都贴一遍。

---

## 二、非目标（这次刻意没做）

- **不改行为**：没有新增界面元素、设置项、网络请求、写盘位置；没有改默认值。
- **不换技术栈**：仍是 WPF + WebView2 + .NET 8，没有引入 MVVM 框架、没有换 DI 容器。
- **不重写协议层**：希沃侧的 SSO 网关、扫码、保活逻辑一行没动（那是经过实战的代码，
  重构收益远小于回归风险）。

---

## 三、现在的结构

```
App.xaml.cs          进程外壳：字段、生命周期（OnExit / Restart / 显隐主窗口）
App.Startup.cs       启动流水线：每一步一个方法，顺序写在 OnStartup 里
App.*.cs             按领域拆的 partial：账号、配置、保活、自检、日志、提权、升级提醒
Core/                希沃侧协议与领域模型 + 纯逻辑（CSV 解析、账号排序）
Services/            操作系统与基础设施：托盘、更新、自启、hosts、加密、诊断包
Ui/                  主界面命令层（C# 侧）+ 消息参数解析 + 对话框
ManagementWindow.*.cs 窗口骨架、WebView2 生命周期、渲染与资源
frontend/js/*.js     前端按职责拆分的脚本（普通 defer，共享全局作用域，顺序即依赖）
```

### 约定

| 约定 | 内容 |
| --- | --- |
| 消息参数 | 一律用 `root.Str/Flag/Int/Node/Strings`（`Ui/JsonArgs.cs`），不直接 `GetProperty` |
| 前端命令 | 处理器写成 `async Task XxxAsync`，对外只暴露 `void Xxx`，内部走 `RunCommand`（异常与取消统一兜底） |
| 用户确认 | 走 `Dialogs.Confirm / ConfirmDanger`，不要直接 `MessageBox.Show`；后台路径不许弹模态框 |
| 纯逻辑 | 不碰 UI、不写日志 → 放 `Core/`，配单测（CSV、排序、参数解析都已覆盖） |
| partial 拆分 | 按**职责**切，不按「技术阶段」切；一个文件里只有一个主题 |
| 前端脚本 | 新文件放 `frontend/js/`，在 `index.html` 按依赖顺序加 `<script defer>`；文件名不要带点 |
| 资源名 | `SeewoAutoLogin.frontend.<子目录>.<文件名>.<扩展名>`，由 MSBuild 自动生成；`WebContentProvisioner` 负责还原 |

---

## 四、这次具体做了什么

**去重与现代化**
- 新增 `GlobalUsings.cs`，删掉 64 个文件里共 401 行重复 `using`。
- 新增 `Ui/JsonArgs.cs`：消息参数「缺失即默认值」，删掉 UI 层全部裸 `GetProperty`。
- 新增 `Ui/Dialogs.cs`：确认/危险确认/告知三个出口，替换散落的 `MessageBox.Show`。

**拆分**
- `ManagementWindow.Handlers.cs`(1057) → 账号 / 登录 / 设置 / 导入导出 / 诊断 五个命令文件 + `Helpers`。
- `App.xaml.cs`(994) → `App.xaml.cs`(外壳) + `App.Startup.cs`(启动流水线，OnStartup 40 行)。
- OnStartup 内部再抽成 9 个步骤方法（一次性命令、崩溃处理、单实例、提权、环境准备、网关、保活、界面、后台任务）。
- `UpdateChecker.cs`(871) → UpdateInfo / UpdateChecker / UpdatePackageCheck / UpdateInstaller。
- `DiagnosticBundleService.cs`(596) → Context / Result / Service。
- `frontend/app.js`(1996) → `frontend/js/` 下 17 个脚本 + 一个只写加载顺序说明的 `app.js`。

**健壮性**
- 11 个 `async void` 命令处理器改为 `async Task` + `RunCommand` 包装：异常按命令名记日志并给出 toast。
- 设置写入先校验 JSON 值类型再落盘（类型不符整条丢弃，不再「把没看懂的值当成 false」）。
- `WebContentProvisioner` 的虚拟主机名改为「版本 + 前端资源内容指纹」：
  改前端不再需要抬版本号，WebView2 也不可能命中旧缓存。
- `BuildInlineHtml` 兜底路径按 `index.html` 里的 script 顺序内联全部脚本（以前只内联单一入口，
  拆分后会得到一个「页面在、脚本全死」的白板）。

**测试**
- 新增 `CsvParserTests`（编码探测、RFC4180、表头识别、明细截断）、
  `AccountOrderingTests`（生效区边界、拖拽重排）、`JsonArgsTests`（缺字段/类型不符）。
- 测试数 39 → 80。

---

## 五、怎么验证

```powershell
dotnet build SeewoAutoLogin.csproj -v q     # 0 warning / 0 error
dotnet test tests/SeewoAutoLogin.Tests/SeewoAutoLogin.Tests.csproj
```

手工回归时优先看这几条（结构改动最容易在这里出问题）：

1. 主界面能加载（`frontend/js/` 的加载顺序错了会白屏）。
2. 账号增删改、拖拽排序、切换生效区。
3. CSV 导入（含 GBK 文件）与账号导出/导入。
4. 设置页开关落盘，重启后保持。
5. 后台更新链路：检查 → 下载 → 安装（这条链路没被改，但 `UpdateChecker` 家族被搬过文件）。

前端拆分的等价性另有一次性验证：17 个文件逐行比对原 `app.js`（去掉文件头注释后 0 差异），
并在 vm 沙箱里用同一组消息与交互分别跑「拆分后」与「git 原版」，错误集合完全一致。

---

## 六、还留着的债

按「下一个人最可能被咬到」排序：

1. `Core/SeewoSsoGateway.cs`(722 行) 仍是单文件协议核心：HTTP 监听、请求路由、账号轮转、
   EasiAgent 端口接管混在一起。它是最不该出错、也最不该大改的一块，拆之前先补端口占用与
   请求路由的单测。
2. `Services/UpdateChecker.cs`(619 行) 里的网络请求与解析仍然耦合：`HttpClient` 直接 new 在静态方法里，
   想测「API 返回畸形 JSON 时怎么办」只能连真实网络。
3. `Services/DiagnosticBundleService.cs`(558 行) 的收集流程还能按「日志 / 配置 / 系统信息 / 压缩」
   继续切，收益中等。
4. 静态服务类（`SecureStore`、`AutoStartService`、`HostsFileService` 等）没有接口：
   它们的调用点极少、行为是纯 OS 副作用，抽接口的收益低于读代码的成本，暂时保留。
5. `docs/index.html`（官网单页，1034 行）没有参与这轮改良。
