using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace SeewoAutoLogin.Services
{
    /// <summary>
    /// 静默更新的健康确认与自动回滚。
    ///
    /// 安装前：把当前 exe 备份为 SeewoAutoLogin.Rollback.exe，写 update-health.json，
    /// 并启动 watcher 进程（进程名不同，不会被安装器 /CLOSEAPPLICATIONS 一起关闭）。
    /// 新版本启动 60 秒内确认网关与配置健康后删除标记，watcher 轮询到标记消失即退出。
    /// 新版本启动失败或健康确认未通过时，watcher 超时发现标记仍在，
    /// 结束新版本、用备份覆盖目标 exe 并启动旧版本。
    /// </summary>
    internal static class UpdateHealthGuard
    {
        private const string MarkerFileName = "update-health.json";
        private const string BackupExeName = "SeewoAutoLogin.Rollback.exe";
        private const int DefaultTimeoutSeconds = 150;

        private static string DataDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SeewoAutoLogin");
        private static string MarkerPath => Path.Combine(DataDir, MarkerFileName);
        private static string BackupDir => Path.Combine(DataDir, "backup");
        private static string BackupExePath => Path.Combine(BackupDir, BackupExeName);

        internal sealed class HealthMarker
        {
            public string TargetExe { get; set; } = "";
            public string BackupExe { get; set; } = "";
            public string NewVersion { get; set; } = "";
            public DateTimeOffset CreatedAtUtc { get; set; }
        }

        /// <summary>安装前建立回滚保护。失败时返回 false，调用方继续更新即可。</summary>
        public static bool BeginGuard(string targetExe, string newVersion, Action<string> log)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(targetExe) || !File.Exists(targetExe))
                {
                    log?.Invoke("[Update] 回滚保护：当前 exe 路径无效，跳过备份");
                    return false;
                }

                Directory.CreateDirectory(BackupDir);
                File.Copy(targetExe, BackupExePath, true);
                File.WriteAllText(MarkerPath, JsonSerializer.Serialize(new HealthMarker
                {
                    TargetExe = targetExe,
                    BackupExe = BackupExePath,
                    NewVersion = newVersion ?? "",
                    CreatedAtUtc = DateTimeOffset.UtcNow
                }));

                var psi = new ProcessStartInfo
                {
                    FileName = BackupExePath,
                    Arguments = $"--rollback-watch --marker \"{MarkerPath}\" --target \"{targetExe}\" --timeout {DefaultTimeoutSeconds}",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                Process.Start(psi);

                log?.Invoke($"[Update] 回滚保护已启动：备份={BackupExePath}; 超时={DefaultTimeoutSeconds}s");
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke($"[Update] 回滚保护创建失败（继续更新，但无自动回滚）: {ex.GetType().Name} - {ex.Message}");
                return false;
            }
        }

        /// <summary>新版本健康确认通过：删除标记，watcher 会自行退出。</summary>
        public static void ConfirmHealthy(Action<string> log)
        {
            try
            {
                if (File.Exists(MarkerPath)) File.Delete(MarkerPath);
                log?.Invoke("[Update] 健康确认通过，已移除回滚标记");
            }
            catch (Exception ex)
            {
                log?.Invoke($"[Update] 移除回滚标记失败: {ex.Message}");
            }
        }

        /// <summary>watcher 入口：等待标记被清除；超时仍在则执行回滚。</summary>
        public static void RunRollbackWatch(string[] args, Action<string> log)
        {
            var marker = GetArg(args, "--marker") ?? MarkerPath;
            var target = GetArg(args, "--target");
            var timeout = int.TryParse(GetArg(args, "--timeout"), out var seconds)
                ? Math.Max(30, seconds)
                : DefaultTimeoutSeconds;

            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(timeout);
                while (DateTime.UtcNow < deadline)
                {
                    if (!File.Exists(marker))
                    {
                        log?.Invoke("[Update] 回滚 watcher：健康确认已通过，退出");
                        return;
                    }
                    Thread.Sleep(2000);
                }

                if (!File.Exists(marker))
                {
                    log?.Invoke("[Update] 回滚 watcher：标记已清除，退出");
                    return;
                }

                log?.Invoke("[Update] 回滚 watcher：超时仍未确认健康，开始回滚");
                Rollback(marker, target, log);
            }
            catch (Exception ex)
            {
                log?.Invoke($"[Update] 回滚 watcher 异常: {ex.GetType().Name} - {ex.Message}");
            }
        }

        private static void Rollback(string markerPath, string fallbackTarget, Action<string> log)
        {
            HealthMarker marker = null;
            try
            {
                marker = JsonSerializer.Deserialize<HealthMarker>(File.ReadAllText(markerPath));
            }
            catch (Exception ex)
            {
                log?.Invoke($"[Update] 读取回滚标记失败: {ex.Message}");
            }

            var target = marker?.TargetExe;
            if (string.IsNullOrWhiteSpace(target)) target = fallbackTarget;
            var backup = marker?.BackupExe;
            if (string.IsNullOrWhiteSpace(backup)) backup = BackupExePath;

            if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(backup) || !File.Exists(backup))
            {
                log?.Invoke("[Update] 回滚信息不完整，放弃回滚");
                TryDelete(markerPath);
                return;
            }

            try
            {
                // 结束所有正在运行的主程序实例（watcher 自己叫 SeewoAutoLogin.Rollback，不在列表里）
                foreach (var process in Process.GetProcessesByName("SeewoAutoLogin"))
                {
                    try
                    {
                        if (process.Id != Environment.ProcessId)
                        {
                            process.Kill();
                            process.WaitForExit(5000);
                        }
                    }
                    catch { }
                    finally { process.Dispose(); }
                }
                Thread.Sleep(800);

                File.Copy(backup, target, true);
                log?.Invoke($"[Update] 已用备份覆盖 {target}");

                Process.Start(new ProcessStartInfo
                {
                    FileName = target,
                    Arguments = "--elevated --minimized",
                    UseShellExecute = true
                });
                log?.Invoke("[Update] 已启动回滚后的版本");
                TryDelete(markerPath);
            }
            catch (Exception ex)
            {
                log?.Invoke($"[Update] 回滚失败: {ex.GetType().Name} - {ex.Message}");
            }
        }

        private static string GetArg(string[] args, string name)
        {
            if (args == null) return null;
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }
            return null;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}