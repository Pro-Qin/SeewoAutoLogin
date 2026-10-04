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
        #region Self Check / Health / Batch Import

        /// <summary>自检状态（供主界面状态栏展示）</summary>
        internal object BuildSelfCheckStatus() => new
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
            seewoRunning = Process.GetProcessesByName("EasiNote").Length > 0,
            autoStartEnabled = Services.AutoStartService.IsEnabled,
            autoStartMode = Services.AutoStartService.CurrentMode(),
            autoStartText = Services.AutoStartService.DescribeState(),
            lastBackup = Services.ConfigBackupService.DescribeLatest(),
            maxVisibleAccounts = PluginConfig.MaxVisibleAccounts
        };

        private int _autoRepairRunning;

        /// <summary>一键修复：重写 hosts 映射并重启 SSO 网关</summary>
        internal async Task<string> RepairSsoAsync()
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

                if (_gateway != null && (!_gateway.IsRunning || _gateway.IsPortMismatched))
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
                    if (Services.AutoStartService.Enable(out var autoStartError, out var mode))
                        actions.Add("已重建开机自启（" + mode + "）");
                    else
                        actions.Add("开机自启修复失败：" + autoStartError);
                }

                if (actions.Count == 0) return "";

                var summary = string.Join("；", actions);
                WriteDiagnosticLog("[AutoRepair] " + summary);
                try { _trayIcon?.SetStatusText("自动修复：" + summary); } catch { }
                return summary;
            }
            finally
            {
                Interlocked.Exchange(ref _autoRepairRunning, 0);
            }
        }

        /// <summary>自动修复循环：启动后延迟首检，之后每 3 分钟巡检一次，不依赖主界面。</summary>
        private async Task RunAutoRepairLoopAsync()
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15));
                while (!_isExiting)
                {
                    await RunAutoRepairAsync();
                    await Task.Delay(TimeSpan.FromMinutes(3));
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                WriteDiagnosticLog("[AutoRepair] 循环异常: " + ex.GetType().Name + " - " + ex.Message);
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
