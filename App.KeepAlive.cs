using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using SeewoAutoLogin.Services;

namespace SeewoAutoLogin
{
    public partial class App
    {
        #region Token Refresh

        /// <summary>后台保活的检查频率：每 5 分钟看一次有哪些账号该续期了</summary>
        private static readonly TimeSpan KeepAliveTick = TimeSpan.FromMinutes(5);
        /// <summary>账号超过这么久没有续期，就主动刷新一次</summary>
        private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromMinutes(25);

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
        /// 密码账号完全不刷新。这里改为每 5 分钟检查、超过 25 分钟未续期即刷新，两类账号都覆盖。
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
        internal async Task<(int refreshed, int failed)> RunKeepAliveAsync(bool force = false)
        {
            if (_keepAliveRunning) return (0, 0);
            // Timer 回调与界面的健康巡检可能并发进来，用原子交换保证同一时间只有一轮。
            if (Interlocked.CompareExchange(ref _keepAliveRunningFlag, 1, 0) != 0) return (0, 0);
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

                    // 需要用户处理的凭据问题：不再每 5 分钟自动重试，避免反复用错误密码打接口、触发风控。
                    // 用户改好密码或重新扫码后，健康巡检（force=true）会立刻重试；
                    // 另外每 6 小时给一次自动复查机会，便于旧版本误标的 bad 状态自动恢复。
                    if (!force && account.HealthState == "bad")
                    {
                        var lastCheck = account.LastHealthCheckAtUtc ?? DateTime.MinValue;
                        if (DateTime.UtcNow - lastCheck < TimeSpan.FromHours(6)) continue;
                    }

                    // 临时性网络故障：退避到 NextRetryAtUtc 之前不再请求希沃接口。
                    if (!force && account.NextRetryAtUtc.HasValue && now < account.NextRetryAtUtc.Value) continue;

                    if (!force && account.LastTokenExchangeAtUtc.HasValue &&
                        now - account.LastTokenExchangeAtUtc.Value < KeepAliveInterval)
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
                        return (false, result.ErrorMessage ?? "密码登录失败", result.FailureKind);
                    if (result.UserInfo != null) account.UserInfo = result.UserInfo;
                    return (true, "已自动重新登录", SeewoFailureKind.None);
                }

                // 扫码账号：用当前令牌换发新令牌，换发成功后立即写回本地加密凭据
                if (!string.IsNullOrWhiteSpace(account.QrCredentialId))
                {
                    if (!_qrSessionStore.TryLoad(account.QrCredentialId, out var session))
                        return (false, "扫码凭据不可用，需要重新扫码", SeewoFailureKind.Credential);

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
                        return (false, "扫码令牌已失效，需要重新扫码添加", SeewoFailureKind.Credential);
                    }

                    WriteDiagnosticLog($"[KeepAlive] 扫码凭据续期成功; account-id={account.Id}; 凭据年龄={age}");
                    // 记一条观测：这个年龄的凭据还能续期。攒够样本就能算出真实有效期。
                    if (session.AcquiredAtUtc != default)
                        _lifetimeTracker?.Record(isQr: true,
                            (DateTimeOffset.UtcNow - session.AcquiredAtUtc).TotalHours, success: true);
                    if (!string.IsNullOrWhiteSpace(service.Token)) OnQrTokenValidated(account, service.Token);
                    if (service.UserInfo != null) account.UserInfo = service.UserInfo;
                    return (true, "已自动续期", SeewoFailureKind.None);
                }

                return (false, "没有可用于续期的凭据", SeewoFailureKind.Credential);
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
                var needAttention = _config.Accounts.Any(a =>
                    !IsPlaceholderAccount(a) && a.HealthState == "bad");
                _trayIcon?.SetAlert(needAttention);
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
                if (pwdCount > 0) hint.Add($"{pwdCount} 个密码账号：请确认密码是否已修改");
                if (qrCount > 0) hint.Add($"{qrCount} 个扫码账号：需要重新扫码添加");

                NotifyInfo("有账号自动续期失败",
                    $"以下账号需要处理：\n\n{nameList}\n\n{string.Join("\n", hint)}" +
                    "\n\n后续不再重复弹窗，可查看托盘图标或账号列表了解状态。");
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
                var task = RunKeepAliveAsync(force: true);
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
