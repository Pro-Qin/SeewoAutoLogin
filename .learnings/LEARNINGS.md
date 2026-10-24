# Learnings

Corrections, insights, and knowledge gaps captured during development.

**Categories**: correction | insight | knowledge_gap | best_practice

---

## [LRN-20260925-001] correction

**Logged**: 2026-09-25T03:20:00Z
**Priority**: high
**Status**: resolved
**Area**: frontend

### Summary
WebView2 不支持 `window.prompt`，调用后不报错也不弹窗，是静默失败

### Details
在 WebView2 宿主里 `window.prompt` 不生效（`alert` / `confirm` 正常，只有 `prompt` 不支持）。
表现为：按钮点下去没有任何反应，控制台也无报错，极易被误认为逻辑写错。
本项目受影响处：重命名账号、添加假账号、编辑标签（第三处由子代理发现）。

### Suggested Action
需要文本输入一律走 C# 原生 `TextInputDialog`，前端 `send({type:'xxx-dialog'})`。
新增交互前先确认 API 在 WebView2 中受支持。

### Metadata
- Source: user_feedback / subagent
- Related Files: frontend/app.js, ManagementWindow.xaml.cs
- Tags: webview2, prompt, silent-failure

---

## [LRN-20260925-002] correction

**Logged**: 2026-09-25T03:20:00Z
**Priority**: critical
**Status**: resolved
**Area**: frontend

### Summary
用 JS 程序化设置 checkbox.checked 会触发 change 事件，导致状态回填被误当成用户操作

### Details
切换口令功能里，C# 启动时下发 `enabled=false`，前端 `box.checked = false`，
change 监听把这次程序化赋值当成「用户取消勾选」，于是发送了 clear-switch-pin，
**把用户已设置的口令清掉了**。同一模式也会让任何「回填即保存」的开关失效。

### Suggested Action
回填受控控件前用抑制标志（如 `suppressChange = true/false`）包住赋值；
或改用 input 事件 + 值比较判断是否真的变化。

### Metadata
- Source: error
- Related Files: frontend/app.js
- Tags: dom, checkbox, change-event, silent-failure

---

## [LRN-20260925-003] correction

**Logged**: 2026-09-25T03:20:00Z
**Priority**: high
**Status**: resolved
**Area**: config

### Summary
为了调试临时改了用户真实配置，事后忘记还原，被当成产品缺陷报回来

### Details
为截图 UI 把 `StartMinimized` 改成 false（备份为 config.json.uibak），
但没有还原。用户随后报告「开机自启动会弹出主界面」，排查后确认是这次改动，不是代码问题。

### Suggested Action
改动用户真实配置前先备份，并在**同一轮工作结束前**还原；
更稳妥的做法是用独立的测试数据目录启动，不碰真实配置。

### Metadata
- Source: user_feedback
- Related Files: %LOCALAPPDATA%/SeewoAutoLogin/config.json
- Tags: test-hygiene, config

---

## [LRN-20260925-004] best_practice

**Logged**: 2026-09-25T03:20:00Z
**Priority**: medium
**Status**: resolved
**Area**: frontend

### Summary
Edge headless 截图有两个坑：--window-size 有最小宽度，data URI 大图会拍到未绘制的空白帧

### Details
1. `--window-size=390` 实际渲染宽度是 492px（Windows headless 有最小宽度），会伪造出「右侧被裁掉」的假性横向溢出。真正的窄视口验证要用 iframe 固定宽度。
2. `decoding="async"` 让 `--screenshot` 有约 3/4 概率拍到布局正确但图片未绘制的空白页；去掉该属性后一次成功。

### Suggested Action
窄视口用 iframe wrapper 验证；内联大图的页面截图前先移除 `decoding="async"`。

### Metadata
- Source: subagent
- Related Files: docs/index.html
- Tags: headless, screenshot, verification

---


## [LRN-20260925-005] correction

**Logged**: 2026-09-25T04:10:00Z
**Priority**: high
**Status**: resolved
**Area**: frontend

### Summary
改了前端资源却没提升版本号，WebView2 用缓存里的旧 JS 配新 HTML，出现「点了没反应」这类鬼现象

### Details
前端通过虚拟主机名加载，主机名只含主.次.修订三段版本号。版本不变时 WebView2 会命中缓存，
于是拿到旧的 app.js + 新的 index.html，两边 DOM 对不上，表现为页面切换失效、按钮无响应。
排查时极易误判成代码 bug（本次就花了一轮去查 navigate 的逻辑，其实代码是对的）。

### Suggested Action
**每次改 frontend/ 下任何文件，都要同时把 <Version> 往上抬一位**，并清掉 %LOCALAPPDATA%/SeewoAutoLogin/web。
或者在开发期给 WebView2 关掉缓存。判断「是不是缓存问题」最快的方法是先清缓存再复现一次。

### Metadata
- Source: user_feedback
- Related Files: SeewoAutoLogin.csproj, frontend/*
- Tags: webview2, cache, version, false-symptom
- See Also: LRN-20260925-004

---


## [LRN-20261004-001] best_practice

**Logged**: 2026-10-04T12:00:00+08:00
**Priority**: high
**Status**: active
**Area**: product

### Summary
生产环境是教室一体机，故障修复必须默认自动完成，不能依赖用户打开主界面点一键修复。

### Details
一体机通常无人值守，出问题后没有机会打开界面。常见故障（hosts 映射丢失、SSO 网关未监听、
端口被 EasiAgent 占用、开机自启缺失）都要有自动修复路径，并在设置中默认开启。
手动一键修复只作为兜底，不再作为主要恢复手段。

### Suggested Action
新增故障处理时先问：能否在启动或定时巡检中自动恢复？能则实现自动修复并默认开启，
只在无法自动恢复时才提示用户或写诊断日志。

### Metadata
- Source: user_feedback
- Related Files: App.Health.cs, Core/Models.cs, ManagementWindow.Settings.cs, frontend/index.html
- Tags: auto-repair, unattended, product-principle

---

## [LRN-20261017-001] best_practice

**Logged**: 2026-10-17T15:20:00Z
**Priority**: high
**Status**: active
**Area**: frontend

### Summary
前端缓存不再靠「改代码就抬版本号」：虚拟主机名已带前端资源内容指纹

### Details
`WebContentProvisioner.VirtualHostName` 现在是 `seewo-v<程序版本>-<资源指纹>.example`，
指纹是全部 `frontend/**` 资源的 SHA256 前 8 位（含资源名）。改动任何前端文件都会换 origin，
WebView2 不可能再拿旧 JS 配新 HTML。

这解决了 LRN-20260925-005 那个坑：以前改前端必须把 `<Version>` 抬一位，忘了就会出现
「点了没反应」这类假 bug。现在版本号只用于发布，不再兼任缓存键。

### Suggested Action
改前端时照常构建即可，不必为了刷缓存抬版本号；发布时按正常流程改 `<Version>`。

### Metadata
- Source: refactor
- Related Files: Services/WebContentProvisioner.cs, frontend/**
- Tags: webview2, cache, version, false-symptom
- See Also: LRN-20260925-005

---

## [LRN-20261017-002] insight

**Logged**: 2026-10-17T15:20:00Z
**Priority**: medium
**Status**: active
**Area**: backend

### Summary
界面消息的参数一律走 JsonArgs，命令入口一律走 RunCommand

### Details
前端发来的 JSON 没有编译期约束。直接用 `GetProperty` 取字段，字段缺失或类型不符都会抛异常，
而异常被消息循环统一 catch 成一行日志 —— 现象是「点了没反应」，排查时看不出是哪个字段。

### Suggested Action
- 读参数：`root.Str/Flag/Int/Node/Strings`（`Ui/JsonArgs.cs`）
- 写命令：`private Task XxxAsync(...)` + `private void Xxx(...) => RunCommand(() => XxxAsync(...), nameof(Xxx))`
- 见 `docs/REFACTORING.md` 的约定表。

### Metadata
- Source: refactor
- Related Files: Ui/JsonArgs.cs, ManagementWindow.Helpers.cs, ManagementWindow.Messages.cs
- Tags: json, async-void, observability

---

## [LRN-20261024-001] correction

**Logged**: 2026-10-24T10:10:00Z
**Priority**: high
**Status**: active
**Area**: backend

### Summary
密码账号「续期失败」的真实原因是希沃返回 error_code 4906「需要图片验证码」，不是密码错

### Details
密码账号的续期 = 每 15 分钟用保存的密码重新调一次 `/api/v1/auth/login`。同一账号高频登录会触发希沃风控，
服务端返回 HTTP 200 + `{"error_code":4906,"message":"需要图片验证码"}`。
旧实现把 `error_code != 0` 一律归为 `SeewoFailureKind.Credential`，于是后台把"要验证码"当成"密码可能改了"，
标红并提示用户改密码 —— 而密码其实是对的。日志里能直接看到：

```
[Login] response; transport=ok; status=200; body={"error_code":4906,"message":"需要图片验证码"}
[KeepAlive] 续期失败（需要处理）; kind=Credential; reason=需要图片验证码
```

### Suggested Action
- 业务错误码要分类：含"验证码/captcha"→ `Captcha`，含"频繁/稍后/风控"→ `Server`。
- `Captcha` 标黄 + 长退避（2 小时），文案写明"登录频率过高触发风控"，不许说成密码错。
- 若验证码反复出现，考虑把密码账号的保活间隔从 15 分钟拉长（它不需要像扫码账号那样频繁换令牌）。

### Metadata
- Source: log-analysis
- Related Files: Core/SeewoAuthService.cs, App.KeepAlive.cs
- Tags: captcha, risk-control, keepalive, misclassification

---

## [LRN-20261024-002] insight

**Logged**: 2026-10-24T10:10:00Z
**Priority**: medium
**Status**: active
**Area**: frontend

### Summary
position:fixed 元素不要放在带 transform 动画的容器里

### Details
`.page.active` 的入场动画用 `transform: translate3d(0,8px,0)`；右下角加号 `.fab-group` 是 `position:fixed` 且嵌在这个 `.page` 内。
有 transform 的祖先会成为 fixed 后代的**包含块**，于是动画那 260ms 里加号跟着页面一起位移，动画结束才回到视口定位。
表现为"滑入动画时位置偏移，动画结束恢复"。

### Suggested Action
fixed 元素放 body 级（或任何没有 transform / filter / backdrop-filter 的容器）。
需要"只在某一页显示"时，用 `body[data-page="xxx"]` 这类选择器控制，而不是靠父元素的 display。

### Metadata
- Source: user_feedback
- Related Files: frontend/index.html, frontend/styles.css, frontend/js/nav.js
- Tags: css, transform, containing-block, fixed