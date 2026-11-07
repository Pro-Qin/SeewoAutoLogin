using System.Security.Principal;
using System.Windows;

namespace SeewoAutoLogin
{
    public partial class App
    {
        #region Self Check / Health / Batch Import

        /// <summary>自检状态（供主界面状态栏展示）</summary>
        internal object BuildSelfCheckStatus()
        {
            // Process 实现 IDisposable：每次自检都会枚举一遍全系统进程，句柄不释放就一路堆到 GC
            var seewoProcs = Process.GetProcessesByName("EasiNote");
            bool seewoRunning;
            try { seewoRunning = seewoProcs.Length > 0; }
            finally { foreach (var p in seewoProcs) p.Dispose(); }

            return new
            {
            gatewayRunning = _gateway?.IsRunning == true,
            gatewayPort = _gateway?.Port ?? 0,
            expectedPort = SeewoSsoGateway.SeewoExpectedPort,
            gatewayPortOk = _gateway != null && !_gateway.IsPortMismatched,
            gatewayPortWarning = _gateway != null && _gateway.IsPortMismatched
                ? $"网关端口 {_gateway.Port} ≠ 希沃固定请求的 {SeewoSsoGateway.SeewoExpectedPort}，希沃不会显示快捷登录入口"
                : "",
            hostsOk = Services.HostsFileService.HasLoopbackMapping(),
            hostsState = Services.HostsFileService.DescribeState(),
            isAdmin = IsAdministrator(),
            seewoRunning = seewoRunning,
            // 距最近一次 SSOLOGIN 的分钟数（null = 从没收到过）。这是「快捷入口到底有没有生效」的
            // 唯一硬信号：我们这边全绿、希沃却一个请求都没发过来，入口就是没出现。
            lastSsoMinutes = SeewoSsoGateway.LastSsologinUtc == DateTime.MinValue
                ? (double?)null
                : Math.Round((DateTime.UtcNow - SeewoSsoGateway.LastSsologinUtc).TotalMinutes, 0),
            autoStartEnabled = Services.AutoStartService.IsEnabled,
            autoStartMode = Services.AutoStartService.CurrentMode(),
            autoStartText = Services.AutoStartService.DescribeState(),
            lastBackup = Services.ConfigBackupService.DescribeLatest(),
            maxVisibleAccounts = PluginConfig.MaxVisibleAccounts
            };
        }

        private int _autoRepairRunning;

        // 开机自启重建的退避状态：连续失败说明环境层面被卡住（组策略禁用计划任务、
        // 注册表被别的守护进程回滚等），每 3 分钟硬重试只会把日志刷满，对恢复毫无帮助。
        private int _autoStartRepairFailures;
        private DateTime _autoStartNextAttemptUtc = DateTime.MinValue;

        /// <summary>一键修复：重写 hosts 映射并重启 SSO 网关</summary>
        internal async Task<string> RepairSsoAsync()
        {
            // 连点「一键修复」时不能并发进来：Stop/Start 交错会抛
            // "Collection was modified" / "Cannot access a disposed object"，还会把网关搅成不可用。
            // 与自动修复共用同一个原子标志，避免两者同时折腾网关。
            if (Interlocked.CompareExchange(ref _autoRepairRunning, 1, 0) != 0)
            {
                WriteDiagnosticLog("[SelfCheck] 一键修复：上一次修复还在进行，本次跳过");
                return "正在修复中，请稍候…";
            }

            try
            {
                var messages = new List<string>();

                if (Services.HostsFileService.EnsureLoopbackMapping(out var hostsError))
                    messages.Add("hosts 映射已修复");
                else
                    messages.Add("hosts 修复失败：" + hostsError);

                try
                {
                    if (_gateway.IsRunning) _gateway.Stop();
                    await Task.Run(() => _gateway.Start()).ConfigureAwait(true);
                    messages.Add(_gateway.IsPortMismatched
                        ? $"SSO 网关已启动，但端口为 {_gateway.Port}（希沃固定请求 {SeewoSsoGateway.SeewoExpectedPort}），快捷登录仍不会出现"
                        : $"SSO 网关已启动（端口 {_gateway.Port}）");
                }
                catch (Exception ex)
                {
                    messages.Add("网关启动失败：" + ex.Message);
                }

                if (!IsAdministrator())
                    messages.Add("当前不是管理员权限，hosts 与网关可能无法生效");

                var summary = string.Join("；", messages);
                WriteDiagnosticLog("[SelfCheck] 一键修复: " + summary);
                return summary;
            }
            finally
            {
                Interlocked.Exchange(ref _autoRepairRunning, 0);
            }
        }

        /// <summary>
        /// 自动修复：定时自检并就地恢复 hosts、SSO 网关与开机自启。
        /// 只做能自动恢复的事；需要管理员权限但当前没有时只记录日志。
        /// </summary>
        internal async Task<string> RunAutoRepairAsync()
        {
            if (_config == null || !_config.AutoRepairEnabled) return "";
            if (Interlocked.CompareExchange(ref _autoRepairRunning, 1, 0) != 0) return "";
            try
            {
                var actions = new List<string>();

                if (!Services.HostsFileService.HasLoopbackMapping())
                {
                    if (Services.HostsFileService.EnsureLoopbackMapping(out var hostsError))
                        actions.Add("已重建 hosts 映射");
                    else
                        actions.Add("hosts 修复失败：" + hostsError);
                }

                // 端口不符多半是希沃的 EasiAgent 占着 24300，而要不要结束它由上面的空闲判断决定。
                // 每 3 分钟把网关停掉重启一次，既抢不回端口，又会打断正在进行的 SSOLOGIN —— 只告警。
                if (_gateway != null && _gateway.IsRunning && _gateway.IsPortMismatched)
                {
                    WriteDiagnosticLog($"[AutoRepair] 网关端口 {_gateway.Port} 与希沃请求的 {SeewoSsoGateway.SeewoExpectedPort} 不一致，"
                        + "希沃可能正占用该端口；本轮只记录，不重置网关");
                }
                else if (_gateway != null && !_gateway.IsRunning)
                {
                    try
                    {
                        if (_gateway.IsRunning) _gateway.Stop();
                        await Task.Run(() => _gateway.Start()).ConfigureAwait(true);
                        actions.Add(_gateway.IsPortMismatched
                            ? $"网关端口 {_gateway.Port} 仍与希沃请求的 {SeewoSsoGateway.SeewoExpectedPort} 不一致"
                            : $"已重启 SSO 网关（端口 {_gateway.Port}）");
                    }
                    catch (Exception ex)
                    {
                        actions.Add("网关修复失败：" + ex.Message);
                    }
                }

                if (_config.AutoStartEnabled && !Services.AutoStartService.IsEnabledForCurrentPath())
                {
                    if (DateTime.UtcNow < _autoStartNextAttemptUtc)
                    {
                        // 冷却期内只留一条 DEBUG 痕迹：运维排查时能看到「还在等」，
                        // 但不会每 3 分钟往日志里塞一条 INFO。
                        WriteDiagnosticLog(LogLevel.Debug,
                            $"[AutoRepair] 开机自启仍未指向当前路径，处于重建冷却期，{RemainingAutoStartCoolDownMinutes()} 分钟后重试");
                    }
                    else if (Services.AutoStartService.Enable(out var autoStartError, out var mode))
                    {
                        // 写完必须复检：Enable 只代表命令执行没报错，任务是否真的指向当前 exe
                        // 要再读一次。少了这一步，「重建成功」就会变成每 3 分钟一次的假修复。
                        if (Services.AutoStartService.IsEnabledForCurrentPath())
                        {
                            _autoStartRepairFailures = 0;
                            actions.Add("已重建开机自启（" + mode + "）");
                        }
                        else
                        {
                            NoteAutoStartRepairFailure($"重建后复检仍未生效（{mode}）", actions);
                        }
                    }
                    else
                    {
                        NoteAutoStartRepairFailure(autoStartError, actions);
                    }
                }

                if (actions.Count == 0) return "";

                var summary = string.Join("；", actions);
                var level = summary.Contains("失败") || summary.Contains("未生效") || summary.Contains("不一致")
                    ? LogLevel.Warn
                    : LogLevel.Info;
                WriteDiagnosticLog(level, "[AutoRepair] " + summary);
                // 只写日志，不碰托盘：自动修复是无人值守的运维行为，每 3 分钟巡检一次。
                // 悬停托盘应当是「账号状态」的仪表，不是我们的修复流水账。
                return summary;
            }
            finally
            {
                Interlocked.Exchange(ref _autoRepairRunning, 0);
            }
        }

        private void NoteAutoStartRepairFailure(string reason, List<string> actions)
        {
            _autoStartRepairFailures++;
            if (_autoStartRepairFailures >= 3)
            {
                _autoStartNextAttemptUtc = DateTime.UtcNow.AddHours(1);
                actions.Add($"开机自启重建连续 {_autoStartRepairFailures} 轮未生效（{reason}），已暂停自动重建 1 小时");
            }
            else
            {
                actions.Add("开机自启修复失败：" + reason);
            }
        }

        private int RemainingAutoStartCoolDownMinutes()
        {
            var minutes = (_autoStartNextAttemptUtc - DateTime.UtcNow).TotalMinutes;
            return minutes <= 0 ? 0 : Math.Max(1, (int)Math.Ceiling(minutes));
        }

        /// <summary>自动修复循环：启动后延迟首检，之后每 3 分钟巡检一次，不依赖主界面。</summary>
        private async Task RunAutoRepairLoopAsync()
        {
            try { await Task.Delay(TimeSpan.FromSeconds(15)); }
            catch (OperationCanceledException) { return; }

            while (!_isExiting)
            {
                // 单轮异常不能带走整个循环：否则一次网关或 hosts 的意外失败，
                // 会让自动修复在此后永久失效，而表面上什么异常都没有。
                try { await RunAutoRepairAsync(); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    WriteWarn("[AutoRepair] 本轮异常: " + ex.GetType().Name + " - " + ex.Message);
                }

                await Task.Delay(TimeSpan.FromMinutes(3));
            }
        }

        /// <summary>
        /// 账号健康巡检：逐个校验凭据是否仍然有效，并就地修复。
        ///
        /// 与后台保活共用同一套逻辑：密码账号会用保存的密码重新登录，
        /// 扫码账号会换发新令牌（换发成功即写回本地凭据）。
        /// 确实修不了的（例如扫码令牌已过期）会标为异常，并说明需要人工做什么。
        /// </summary>
        internal async Task RunHealthCheckAsync()
        {
            WriteDiagnosticLog("[Health] 开始账号健康巡检（发现问题会自动重新导入）");
            var (refreshed, failed) = await RunKeepAliveAsync(force: true).ConfigureAwait(true);
            if (refreshed < 0)
            {
                WriteDiagnosticLog("[Health] 后台保活正在进行，本次巡检未执行（这不是「没有问题」）");
                return;
            }
            var transient = _config.Accounts.Count(a =>
                !IsPlaceholderAccount(a) && a.HealthState == "warn");
            WriteDiagnosticLog($"[Health] 巡检完成：已自动修复 {refreshed} 个，仍需人工处理 {failed} 个，网络暂缓 {transient} 个");
        }

        /// <summary>批量导入账号：每行 “账号,密码[,备注]”</summary>
        internal (int added, int failed, List<string> messages) BatchImport(string text)
        {
            var added = 0;
            var failed = 0;
            var messages = new List<string>();

            foreach (var raw in (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;

                var parts = line.Split(new[] { ',', '，', '\t', ';' }).Select(p => p.Trim()).ToArray();
                if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0)
                {
                    failed++;
                    messages.Add($"格式错误（需要 账号,密码[,备注]）：{line}");
                    continue;
                }

                var username = parts[0];
                var password = parts[1];
                var note = parts.Length >= 3 ? parts[2] : "";

                if (_config.Accounts.Any(a => string.Equals(a.Username, username, StringComparison.OrdinalIgnoreCase)))
                {
                    failed++;
                    messages.Add($"已存在，跳过：{username}");
                    continue;
                }

                try
                {
                    var account = new SeewoAccount
                    {
                        Username = username,
                        Password = Services.SecureStore.Encrypt(password),
                        DisplayName = string.IsNullOrWhiteSpace(note) ? username : note
                    };
                    _config.Accounts.Add(account);
                    if (_config.Accounts.Count == 1) _config.ActiveAccountId = account.Id;
                    added++;
                    messages.Add($"已导入：{username}");
                }
                catch (Exception ex)
                {
                    failed++;
                    messages.Add($"导入失败 {username}：{ex.Message}");
                }
            }

            if (added > 0) SaveConfig();
            WriteDiagnosticLog($"[BatchImport] 成功 {added} 个，失败 {failed} 个");
            return (added, failed, messages);
        }

        #endregion
    }
}
