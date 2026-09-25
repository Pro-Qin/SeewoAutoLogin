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
