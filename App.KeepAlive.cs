using System.Security.Principal;
using System.Windows;

namespace SeewoAutoLogin
{
    public partial class App
    {
        #region Token Refresh

        /// <summary>后台保活的检查频率：每 5 分钟看一次有哪些账号该续期了</summary>
        private static readonly TimeSpan KeepAliveTick = TimeSpan.FromMinutes(5);
        /// <summary>账号超过这么久没有续期，就主动刷新一次</summary>
        // 15 分钟（原 25）：扫码凭据的有效期不是固定的，越早续上越不容易断档；
        // 断档一次就得让老师重新扫码，那才是真正麻烦的事。
        private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromMinutes(15);

        /// <summary>
        /// 密码账号的续期间隔：6 小时。
        ///
        /// 密码账号的"续期"本质是重新登录一次。每 15 分钟重登一次，一天 96 次，
        /// 希沃那边就是典型的高频登录 —— 4906（要验证码）、4909（假密码错误）都由此而来。
        /// 扫码账号才需要频繁换令牌（令牌会过期）；密码账号的 token 失效时，
        /// 希沃发 SSOLOGIN 那一刻会自动重登，不需要后台盲刷。
        /// </summary>
        private static readonly TimeSpan PasswordKeepAliveInterval = TimeSpan.FromHours(6);

        private bool _keepAliveRunning;
        private int _keepAliveRunningFlag;

        /// <summary>
        /// 是否为占位账号（由「添加假账号」生成，凭据在希沃侧并不存在）。
        /// 判断依据是账号上的显式标记，不做命名猜测 —— 用户完全可以把某个账号命名为 test 之类的名字。
        /// </summary>
        private static bool IsPlaceholderAccount(SeewoAccount account) => account?.IsPlaceholder == true;

        /// <summary>
        /// 兼容历史数据：早期版本生成的假账号没有标记，这里按当时固定的命名补上（仅在必要时执行一次）。
        /// 补完之后判断就完全依赖标记。
        /// </summary>
        private void MigratePlaceholderFlags()
        {
            var changed = false;
            foreach (var account in _config.Accounts)
            {
                if (account.IsPlaceholder) continue;
                if ((account.Id ?? "").StartsWith("FAKE_", StringComparison.OrdinalIgnoreCase)
                    || (account.Username ?? "").StartsWith("fake_fake_", StringComparison.OrdinalIgnoreCase))
                {
                    account.IsPlaceholder = true;
                    changed = true;
                    WriteDiagnosticLog($"[Config] 历史测试账号已补占位标记; account-id={account.Id}");
                }
            }

            if (changed) SaveConfig();
        }

        /// <summary>
        /// 启动账号凭据的后台保活。
        ///
        /// 希沃的登录令牌会随时间失效，一旦失效，该账号在希沃的登录界面上就无法再用于快捷登录，
        /// 表现为“账号数据已过期”。而换发令牌本身需要“用当前令牌换新令牌”，
        /// 所以必须在令牌失效之前主动续期 —— 事后补救是来不及的。
        ///
        /// 原先的实现是 `Timer(..., FromDays(1), FromDays(1))`：启动满一天才执行第一次，
        /// 而程序每次重启计时器都从头开始，实际几乎从不触发；且它只处理扫码账号，
        /// 密码账号完全不刷新。这里改为每 5 分钟检查、超过 15 分钟未续期即刷新，两类账号都覆盖。
        /// </summary>
        private void StartDailyTokenRefresh()
        {
            if (_dailyTokenRefreshTimer != null) return;

            // 启动后 20 秒先跑一轮（等界面与网关就绪），此后每 5 分钟检查一次
            _dailyTokenRefreshTimer = new Timer(_ => _ = RunKeepAliveAsync(), null,
                TimeSpan.FromSeconds(20), KeepAliveTick);

            WriteDiagnosticLog($"[KeepAlive] 后台保活已启动：每 {KeepAliveTick.TotalMinutes:0} 分钟检查，" +
                               $"账号超过 {KeepAliveInterval.TotalMinutes:0} 分钟未续期即自动刷新");
        }

        /// <summary>
        /// 后台保活：在凭据失效前主动续期，返回（成功数, 失败数）。
        /// force=true 时忽略时间间隔 —— 供「健康巡检」按钮手动触发，即“发现问题就地修好”。
        /// </summary>
        internal async Task<(int refreshed, int failed)> RunKeepAliveAsync(bool force = false, string onlyAccountId = null)
        {
            // 返回 -1 表示「这一轮根本没跑」：调用方要能区分它和「跑了但没问题」，
            // 否则界面会显示「已自动修复 0 个」，让用户以为账号都没事。
            if (_keepAliveRunning) return (-1, -1);
            // Timer 回调与界面的健康巡检可能并发进来，用原子交换保证同一时间只有一轮。
            if (Interlocked.CompareExchange(ref _keepAliveRunningFlag, 1, 0) != 0) return (-1, -1);
            _keepAliveRunning = true;

            var refreshed = 0;
            var failed = 0;
            var transient = 0;
            var stateChanged = false;
            try
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var account in _config.Accounts.ToList())
                {
                    // 测试 / 占位账号不参与保活，也不显示健康状态
                    if (IsPlaceholderAccount(account))
                    {
                        if (!string.IsNullOrEmpty(account.HealthState) || !string.IsNullOrEmpty(account.HealthMessage))
                        {
                            account.HealthState = "";
                            account.HealthMessage = "";
                            stateChanged = true;
                        }
                        continue;
                    }

                    // 界面上的「续期」按钮只刷一个账号
                    if (!string.IsNullOrEmpty(onlyAccountId) && account.Id != onlyAccountId) continue;

                    // 需要用户处理的凭据问题：不再每 5 分钟自动重试，避免反复用错误密码打接口、触发风控。
                    // 用户改好密码或重新扫码后，健康巡检（force=true）会立刻重试；
                    // 另外每 6 小时给一次自动复查机会，便于旧版本误标的 bad 状态自动恢复。
                    // 冷却 15 分钟（原先是 6 小时）：老师可能下一节课就要用，不能让他等半天。
                    // 反复用错误密码打接口会被风控，所以这里不能无间隔地试。
                    if (!force && account.HealthState == "bad")
                    {
                        var lastCheck = account.LastHealthCheckAtUtc ?? DateTime.MinValue;
                        if (DateTime.UtcNow - lastCheck < TimeSpan.FromMinutes(15)) continue;
                    }

                    // 临时性网络故障：退避到 NextRetryAtUtc 之前不再请求希沃接口。
                    if (!force && account.NextRetryAtUtc.HasValue && now < account.NextRetryAtUtc.Value) continue;

                    // 扫码账号频繁换令牌；密码账号只在兜底间隔到点时才重登（见 PasswordKeepAliveInterval）
                    var keepAliveInterval = string.IsNullOrEmpty(account.Password)
                        ? KeepAliveInterval
                        : PasswordKeepAliveInterval;
                    if (!force && account.LastTokenExchangeAtUtc.HasValue &&
                        now - account.LastTokenExchangeAtUtc.Value < keepAliveInterval)
                        continue;

                    var (ok, message, kind) = await RefreshAccountCredentialAsync(account).ConfigureAwait(true);
                    account.LastHealthCheckAtUtc = DateTime.UtcNow;
                    stateChanged = true;

                    if (ok)
                    {
                        account.HealthState = "ok";
                        account.HealthMessage = message;
                        account.LastTokenExchangeAtUtc = DateTimeOffset.UtcNow;
                        account.NextRetryAtUtc = null;
                        account.TransientFailureCount = 0;
                        refreshed++;
                        if (force) WriteDiagnosticLog($"[KeepAlive] 续期成功; account-id={account.Id}; {message}");
                        continue;
                    }

                    // 需要图片验证码：密码多半是对的，是登录太频繁触发了希沃风控。
                    // 自动流程过不了验证码，只能等冷却；给它更长的退避，并且文案不许说成"密码错了"。
                    if (kind == SeewoFailureKind.Captcha)
                    {
                        transient++;
                        account.TransientFailureCount++;
                        var captchaDelay = TimeSpan.FromHours(2);
                        account.NextRetryAtUtc = DateTimeOffset.UtcNow + captchaDelay;
                        account.HealthState = "warn";
                        account.HealthMessage = "希沃要求图片验证码（登录频率过高触发风控），"
                                                + "约 2 小时后自动重试；也可在希沃白板里手动登录一次解除";
                        WriteDiagnosticLog(
                            $"[KeepAlive] 希沃要求图片验证码，暂缓续期; account-id={account.Id}; " +
                            $"连续第 {account.TransientFailureCount} 次; 下次重试={captchaDelay.TotalHours:0} 小时后; reason={message}");
                        continue;
                    }

                    // 只有凭据问题才标 bad；网络 / 超时 / 5xx 一律标 warn 并自动退避重试。
                    if (kind == SeewoFailureKind.Network || kind == SeewoFailureKind.Server)
                    {
                        transient++;
                        account.TransientFailureCount++;
                        var delay = GetTransientRetryDelay(account.TransientFailureCount);
                        account.NextRetryAtUtc = DateTimeOffset.UtcNow + delay;
                        account.HealthState = "warn";
                        account.HealthMessage = $"网络异常，{FormatRetryDelay(delay)}后自动重试：{message}";
                        WriteDiagnosticLog(
                            $"[KeepAlive] 网络原因暂缓续期; account-id={account.Id}; kind={kind}; " +
                            $"连续第 {account.TransientFailureCount} 次; 下次重试={delay.TotalMinutes:0} 分钟后; reason={message}");
                        continue;
                    }

                    // 凭据类失败也先给一次机会：希沃对同一个账号偶发会返回「密码不正确」（风控、
                    // 瞬时限流、DNS 抖动都见过），一次就标红会让老师和用户都以为密码真的错了。
                    // 连续两次才算需要人工处理 —— 真改了密码的话第二次照样会失败，结论不变。
                    if (kind == SeewoFailureKind.Credential && account.TransientFailureCount < 1)
                    {
                        transient++;
                        account.TransientFailureCount++;
                        account.HealthState = "warn";
                        account.HealthMessage = message + "（15 分钟后自动重试）";
                        account.NextRetryAtUtc = DateTime.UtcNow.AddMinutes(15);
                        WriteDiagnosticLog(
                            $"[KeepAlive] 凭据续期失败，先按可恢复处理; account-id={account.Id}; " +
                            $"reason={message}; 15 分钟后重试");
                        continue;
                    }

                    failed++;
                    account.HealthState = "bad";
                    account.HealthMessage = message;
                    account.NextRetryAtUtc = null;
                    WriteDiagnosticLog($"[KeepAlive] 续期失败（需要处理）; account-id={account.Id}; kind={kind}; reason={message}");
                }

                if (stateChanged)
                {
                    SaveConfig();
                    await RefreshAccountListUiAsync().ConfigureAwait(true);
                }

                if (refreshed > 0 || failed > 0 || transient > 0)
                    WriteDiagnosticLog($"[KeepAlive] 本轮完成：续期 {refreshed} 个，失败 {failed} 个，网络暂缓 {transient} 个");

                // 手动健康巡检时只更新界面，不弹窗打扰。
                if (failed > 0 && !force) NotifyKeepAliveFailure();
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"[KeepAlive] 本轮异常: {ex.GetType().Name} - {ex.Message}");
            }
            finally
            {
                // 无论本轮成功、失败还是异常，都按当前真实账号状态同步一次托盘，
                // 避免出现"托盘还亮着、点进去已经正常"的残留。
                UpdateTrayAlertForHealth();
                _keepAliveRunning = false;
                Interlocked.Exchange(ref _keepAliveRunningFlag, 0);
            }

            return (refreshed, failed);
        }

        /// <summary>
        /// 刷新单个账号的凭据。
        /// 使用独立的服务实例，避免与正在响应希沃请求的网关争用同一份会话状态。
        /// </summary>
        private async Task<(bool ok, string message, SeewoFailureKind kind)> RefreshAccountCredentialAsync(SeewoAccount account)
        {
            try
            {
                // 优先用扫码凭据续期：token 换发不经过密码登录接口，不会触发「需要图片验证码」的风控。
                // 老账号往往同时存着密码和扫码凭据；若先走密码分支，等于每 15 分钟拿密码登录一次，
                // 把账号一路推进 4906/4909。只有扫码这条真的不通（临时故障 / 凭据缺失）才回退密码。
                if (!string.IsNullOrWhiteSpace(account.QrCredentialId))
                {
                    var qrOutcome = await RefreshViaQrAsync(account).ConfigureAwait(true);
                    if (qrOutcome.kind != SeewoFailureKind.Network && qrOutcome.kind != SeewoFailureKind.Server)
                        return qrOutcome;
                    WriteDiagnosticLog($"[KeepAlive] 扫码续期遇到临时故障，改用密码兜底; account-id={account.Id}");
                }

                // 密码账号：用保存的密码重新登录，顺带刷新用户信息
                if (!string.IsNullOrEmpty(account.Password))
                {
                    var password = account.DecryptedPassword;
                    if (string.IsNullOrEmpty(password))
                        return (false, "本地凭据无法解密，请重新录入密码", SeewoFailureKind.Credential);

                    using var service = new SeewoAuthService();
                    service.DiagnosticMessage += WriteDiagnosticLog;
                    var result = await service.LoginAsync(account.Username, password).ConfigureAwait(true);
                    if (!result.Success)
                    {
                        var reason = result.ErrorMessage ?? "密码登录失败";
                        // 失败也写一条：否则「登录记录」里只有成功，
                        // 失败原因只活在这一次调用的内存里，界面刷新一次就查不到了。
                        LoginHistoryService.Record(account.Id, account.DisplayName, account.Username,
                            "keepalive", false, $"自动续期失败（密码重新登录）：{reason}");
                        return (false, reason, result.FailureKind);
                    }
                    if (result.UserInfo != null) account.UserInfo = result.UserInfo;
                    LoginHistoryService.Record(account.Id, account.DisplayName, account.Username,
                        "keepalive", true, "自动续期（密码重新登录）");
                    return (true, "已自动重新登录", SeewoFailureKind.None);
                }

                return (false, "没有可用于续期的凭据", SeewoFailureKind.Credential);
            }
            catch (Exception ex)
            {
                return (false, ex.Message, SeewoFailureKind.Unknown);
            }
        }

        /// <summary>
        /// 扫码凭据续期：用当前令牌换发新令牌，成功后立即写回本地加密凭据。
        /// 抽成独立方法是为了让「扫码优先、密码兜底」这条顺序在调用处一眼可见。
        /// </summary>
        private async Task<(bool ok, string message, SeewoFailureKind kind)> RefreshViaQrAsync(SeewoAccount account)
        {
            try
            {
                if (!_qrSessionStore.TryLoad(account.QrCredentialId, out var session))
                    return (false, "扫码凭据不可用，需要重新扫码", SeewoFailureKind.Credential);

                {
                    using var service = new SeewoAuthService();
                    service.DiagnosticMessage += WriteDiagnosticLog;
                    service.RestoreQrSession(session.Token, account.UserInfo);
                    var result = await service.ExchangeCurrentTokenAsync().ConfigureAwait(true);

                    // 记录凭据「年龄」：凭据是扫码那一刻拿到的，能续期说明还在有效期内。
                    // 积累几次「多大年龄仍能续期 / 从多大年龄开始续不动」，就能反推出实际有效期，
                    // 从而判断关机多久之内还能自动恢复。
                    var age = session.AcquiredAtUtc == default
                        ? "未知"
                        : (DateTimeOffset.UtcNow - session.AcquiredAtUtc).TotalHours.ToString("F1") + " 小时";

                    if (!result.Success)
                    {
                        // 网络 / 服务端问题不能算"扫码令牌失效"，否则会误导用户重新扫码。
                        if (result.FailureKind == SeewoFailureKind.Network || result.FailureKind == SeewoFailureKind.Server)
                        {
                            WriteDiagnosticLog($"[KeepAlive] 扫码凭据续期遇到临时故障; account-id={account.Id}; " +
                                               $"凭据年龄={age}; kind={result.FailureKind}; reason={result.ErrorMessage}");
                            return (false, result.ErrorMessage ?? "Token 换发失败", result.FailureKind);
                        }

                        // 真正的凭据失效：这个年龄的凭据已经续不动了
                        if (session.AcquiredAtUtc != default)
                            _lifetimeTracker?.Record(isQr: true,
                                (DateTimeOffset.UtcNow - session.AcquiredAtUtc).TotalHours, success: false);
                        WriteDiagnosticLog($"[KeepAlive] 扫码凭据续期失败; account-id={account.Id}; 凭据年龄={age}; " +
                                           $"说明=凭据已超出有效期，无法自动恢复，需要重新扫码");
                        LoginHistoryService.Record(account.Id, account.DisplayName, account.Username,
                            "keepalive", false, "扫码凭据已过期，需要重新扫码");
                        return (false, "扫码令牌已失效，需要重新扫码添加", SeewoFailureKind.Credential);
                    }

                    WriteDiagnosticLog($"[KeepAlive] 扫码凭据续期成功; account-id={account.Id}; 凭据年龄={age}");
                    // 记一条观测：这个年龄的凭据还能续期。攒够样本就能算出真实有效期。
                    if (session.AcquiredAtUtc != default)
                        _lifetimeTracker?.Record(isQr: true,
                            (DateTimeOffset.UtcNow - session.AcquiredAtUtc).TotalHours, success: true);
                    if (!string.IsNullOrWhiteSpace(service.Token)) OnQrTokenValidated(account, service.Token);
                    if (service.UserInfo != null) account.UserInfo = service.UserInfo;
                    LoginHistoryService.Record(account.Id, account.DisplayName, account.Username,
                        "keepalive", true, "自动续期（扫码凭据换发）");
                    return (true, "已自动续期", SeewoFailureKind.None);
                }
            }
            catch (Exception ex)
            {
                return (false, ex.Message, SeewoFailureKind.Unknown);
            }
        }

        /// <summary>上一次就续期失败弹窗的日期（弹窗每天最多一次）</summary>
        private DateTime _lastKeepAliveNotifyDate = DateTime.MinValue;

        /// <summary>
        /// 续期失败时的提醒策略。
        ///
        /// 续期是后台行为，失败原因往往只是网络抖动；每次都弹窗（还带系统提示音）
        /// 会在上课时突然响一声，很打扰。所以这里分三档：
        ///   · 托盘图标右下角点亮感叹号 —— 只要还有异常账号就一直亮着，恢复后自动熄灭
        ///   · 账号列表里逐条标注状态 —— 想细看的时候随时能看
        ///   · 弹窗每天最多一次，并列出具体是哪些账号
        /// </summary>
        /// <summary>
        /// 托盘感叹号只跟随需要用户处理的凭据问题（bad）。
        /// 网络暂缓（warn）会在账号列表标黄并自动退避重试，不应点亮红色感叹号，
        /// 否则网络恢复后很容易出现"托盘还亮着、点进去一切正常"的残留。
        /// </summary>
        private void UpdateTrayAlertForHealth()
        {
            try
            {
                var bad = _config.Accounts
                    .Where(a => !IsPlaceholderAccount(a) && a.HealthState == "bad")
                    .ToList();
                // 被希沃图片验证码拦住的那一类单独算：密码是对的，只是登录频率触发风控。
                // 托盘用黄点而不是红点，避免用户以为密码错了跑去改密码。
                var risk = bad.Count(a => (a.HealthMessage ?? "").Contains("验证码"));
                _trayIcon?.SetHealthState(bad.Count, risk);
            }
            catch { }
        }

        /// <summary>网络故障的退避时间：1、5、15、30 分钟，最多 30 分钟。</summary>
        private static TimeSpan GetTransientRetryDelay(int consecutiveFailures)
        {
            return consecutiveFailures switch
            {
                <= 1 => TimeSpan.FromMinutes(1),
                2 => TimeSpan.FromMinutes(5),
                3 => TimeSpan.FromMinutes(15),
                _ => TimeSpan.FromMinutes(30)
            };
        }

        private static string FormatRetryDelay(TimeSpan delay)
            => delay.TotalMinutes < 1 ? $"{delay.TotalSeconds:0} 秒" : $"{delay.TotalMinutes:0} 分钟";

        private void NotifyKeepAliveFailure()
        {
            try
            {
                // 只对需要用户处理的凭据问题弹窗；网络 / 服务端临时故障由 UpdateTrayAlertForHealth
                // 点亮托盘并自动退避重试，绝不弹窗 + 响铃。
                var bad = _config.Accounts
                    .Where(a => a.HealthState == "bad" && !IsPlaceholderAccount(a))
                    .ToList();

                if (bad.Count == 0) return;

                var today = DateTime.Today;
                if (_lastKeepAliveNotifyDate == today) return;
                _lastKeepAliveNotifyDate = today;

                var shown = bad.Take(8)
                    .Select(a => string.IsNullOrWhiteSpace(a.DisplayName) ? a.Username : a.DisplayName)
                    .ToList();
                var nameList = string.Join("\n", shown.Select(n => "  · " + n));
                if (bad.Count > shown.Count) nameList += $"\n  · 另有 {bad.Count - shown.Count} 个";

                var qrCount = bad.Count(a => !string.IsNullOrWhiteSpace(a.QrCredentialId));
                var pwdCount = bad.Count - qrCount;
                var hint = new List<string>();
                if (pwdCount > 0) hint.Add($"{pwdCount} 个密码账号：多为登录过于频繁触发风控，先在希沃白板里手动登录一次；确实改过密码的再重新录入");
                if (qrCount > 0) hint.Add($"{qrCount} 个扫码账号：需要重新扫码添加");

                // 用托盘气球而不是模态框：不抢焦点、会自己消失，也不会有系统提示音，
                // 一天最多一次。账号失效是必须让用户知道的事，但没必要在他上课时拦住他。
                var summary = $"有 {bad.Count} 个账号需要处理：" + string.Join("、", shown);
                WriteDiagnosticLog($"[KeepAlive] {summary}；{string.Join("；", hint)}");
                UpdateTrayAlertForHealth();
                _trayIcon?.Notify(summary + "（详情见账号列表或登录记录）");
            }
            catch { }
        }

        /// <summary>
        /// 退出前补一次续期。
        ///
        /// 凭据越「新鲜」，关机后还能撑的时间越长：如果凭据有效期是若干天，
        /// 那么在关机前刚换过一次，下次开机这段时间内都还有机会继续续期。
        /// 只在确实有账号需要续期时才做，且带超时，不会拖慢退出。
        /// </summary>
        private void TryFinalKeepAlive()
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var need = _config.Accounts.Any(a =>
                    !IsPlaceholderAccount(a) &&
                    (!string.IsNullOrEmpty(a.Password) || !string.IsNullOrWhiteSpace(a.QrCredentialId)) &&
                    (!a.LastTokenExchangeAtUtc.HasValue || now - a.LastTokenExchangeAtUtc.Value > TimeSpan.FromMinutes(5)));

                if (!need) return;

                WriteDiagnosticLog("[KeepAlive] 退出前刷新凭据…");
                // 必须丢到线程池再等：这个方法跑在 UI 线程，而续期内部是 ConfigureAwait(true)，
                // 直接 Wait 会等待一个「需要回到 UI 线程才能完成」的任务 —— 死锁到超时，白等 12 秒。
                var task = Task.Run(() => RunKeepAliveAsync(force: true));
                if (!task.Wait(TimeSpan.FromSeconds(12)))
                    WriteDiagnosticLog("[KeepAlive] 退出前刷新超时，已跳过（不影响退出）");
                else
                    WriteDiagnosticLog($"[KeepAlive] 退出前刷新完成：续期 {task.Result.refreshed} 个");
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"[KeepAlive] 退出前刷新异常: {ex.GetType().Name}");
            }
        }

        /// <summary>
        /// 界面上的「续期」/「全部续期」：立刻用保存的凭据重试一遍。
        /// accountId 为空表示全部；返回 (-1, -1) 表示后台那一轮正在跑。
        /// </summary>
        internal Task<(int refreshed, int failed)> RenewAccountsAsync(string accountId)
            => RunKeepAliveAsync(force: true, onlyAccountId: string.IsNullOrWhiteSpace(accountId) ? null : accountId.Trim());

        /// <summary>让主界面刷新账号列表（主界面未打开或已销毁时静默跳过）</summary>
        private async Task RefreshAccountListUiAsync()
        {
            // 账号增删、健康状态变化后同步托盘告警，避免已删除账号的告警残留。
            UpdateTrayAlertForHealth();
            try
            {
                if (MainWindow is ManagementWindow window && window.IsLoaded)
                    await window.RefreshAccountListAsync().ConfigureAwait(true);
            }
            catch { }
        }

        #endregion
    }
}
