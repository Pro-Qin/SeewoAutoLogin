## 这个 PR 做了什么

<!-- 一两句话说清动机与结果，不要逐文件罗列 -->

## 关联 issue

<!-- Fixes #123 / Closes #456；没有就写「无」 -->

## 怎么验证的

- [ ] `dotnet build SeewoAutoLogin.csproj` 0 警告 0 错误
- [ ] `dotnet test tests/SeewoAutoLogin.Tests` 全通过
- [ ] 手工跑过受影响的流程（写明跑了什么）

## 提交前检查

- [ ] 没有把账号、密码、手机号、日志原文、令牌提交进仓库
- [ ] 用户可见的新文案已同时写进 `Resources/i18n/zh-CN.json`
- [ ] 行为变化写进了 `CHANGELOG.md` 当前未发布的那一段
- [ ] 后台新行为没有引入弹窗或托盘通知（只在日志与界面状态里体现）

## 截图

<!-- 界面有改动时贴前后对比 -->
