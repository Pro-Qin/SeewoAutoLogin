using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;

namespace SeewoAutoLogin.Services
{
    /// <summary>
    /// 开机自启管理。
    /// 首选「计划任务」：登录触发 + 最高权限运行，开机后不再弹 UAC；
    /// 若当前不是管理员（无法创建最高权限任务），回退到 HKCU\...\Run（代价是每次登录会弹一次 UAC）。
    /// </summary>
    internal static class AutoStartService
    {
        private const string TaskName = "SeewoAutoLogin";
        private const string RegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RegistryValueName = "SeewoAutoLogin";

        internal static string ExecutablePath
        {
            get
            {
                try
                {
                    var path = Environment.ProcessPath;
                    if (string.IsNullOrEmpty(path))
                        path = Process.GetCurrentProcess().MainModule?.FileName;
                    return path ?? "";
                }
                catch { return ""; }
            }
        }

        internal static bool IsAdministrator()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        /// <summary>当前生效的自启方式：task / registry / none</summary>
        internal static string CurrentMode()
        {
            if (TaskExists()) return "task";
            if (!string.IsNullOrEmpty(RegistryValue())) return "registry";
            return "none";
        }

        internal static bool IsEnabled => CurrentMode() != "none";

        internal static string DescribeState()
        {
            var mode = CurrentMode();
            switch (mode)
            {
                case "task": return "已启用（计划任务·最高权限，开机免 UAC）";
                case "registry": return "已启用（注册表启动项，开机需要授权）";
                default: return "未启用";
            }
        }

        /// <summary>
        /// 当前自启项是否指向本程序当前的可执行文件。
        /// 程序换目录（例如从开发版切到正式安装版）后旧启动项会指向不存在的文件，需要据此重建。
        /// </summary>
        internal static bool IsEnabledForCurrentPath()
        {
            var exe = ExecutablePath;
            if (string.IsNullOrEmpty(exe)) return IsEnabled;

            var mode = CurrentMode();
            if (mode == "registry")
                return RegistryValue().IndexOf(exe, StringComparison.OrdinalIgnoreCase) >= 0;

            if (mode == "task")
            {
                // schtasks /XML 输出为 UTF-16，必须按 Unicode 读取才能比对路径
                var (ok, xml) = RunSchtasks(new[] { "/Query", "/TN", TaskName, "/XML" }, Encoding.Unicode);
                return ok && xml.IndexOf(exe, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            return false;
        }

        /// <summary>启用自启。优先创建最高权限计划任务，失败则回退注册表启动项。</summary>
        internal static bool Enable(out string error, out string mode)
        {
            error = null;
            mode = "none";

            // 1) 计划任务（需要管理员）
            if (IsAdministrator())
            {
                var (ok, msg) = RunSchtasks(new[]
                {
                    "/Create", "/TN", TaskName,
                    "/TR", "\"" + ExecutablePath + "\" --minimized",
                    "/SC", "ONLOGON", "/RL", "HIGHEST", "/F"
                });
                if (ok && TaskExists())
                {
                    RemoveRegistryValue();
                    mode = "task";
                    return true;
                }
                error = "计划任务创建失败: " + msg;
            }
            else
            {
                error = "当前不是管理员权限，无法创建最高权限计划任务";
            }

            // 2) 回退：注册表启动项
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, true);
                if (key == null) return false;
                key.SetValue(RegistryValueName, "\"" + ExecutablePath + "\" --minimized");
                if (string.IsNullOrEmpty(RegistryValue())) return false;
                mode = "registry";
                if (!string.IsNullOrEmpty(error))
                    error = error + "；已回退为注册表启动项（开机需要授权）";
                else
                    error = "已回退为注册表启动项（开机需要授权）";
                return true;
            }
            catch (Exception ex)
            {
                error = (error ?? "") + "；写入注册表启动项失败: " + ex.Message;
                return false;
            }
        }

        /// <summary>禁用自启（计划任务与注册表启动项一并清理）</summary>
        internal static bool Disable(out string error)
        {
            error = null;
            var ok = true;

            if (TaskExists())
            {
                var (taskOk, msg) = RunSchtasks(new[] { "/Delete", "/TN", TaskName, "/F" });
                if (!taskOk && TaskExists())
                {
                    ok = false;
                    error = "删除计划任务失败（可能需要管理员权限）: " + msg;
                }
            }

            try { RemoveRegistryValue(); }
            catch (Exception ex)
            {
                ok = false;
                error = (error ?? "") + "；删除注册表启动项失败: " + ex.Message;
            }

            return ok;
        }

        private static bool TaskExists()
        {
            var (ok, _) = RunSchtasks(new[] { "/Query", "/TN", TaskName });
            return ok;
        }

        private static string RegistryValue()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath);
                return key?.GetValue(RegistryValueName) as string ?? "";
            }
            catch { return ""; }
        }

        private static void RemoveRegistryValue()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, true);
                key?.DeleteValue(RegistryValueName, false);
            }
            catch { }
        }

        private static (bool ok, string message) RunSchtasks(string[] args, Encoding encoding = null)
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = encoding ?? Encoding.UTF8,
                    StandardErrorEncoding = encoding ?? Encoding.UTF8
                };
                foreach (var a in args) psi.ArgumentList.Add(a);

                using var process = Process.Start(psi);
                if (process == null) return (false, "无法启动 schtasks.exe");
                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                if (!process.WaitForExit(15000))
                {
                    try { process.Kill(); } catch { }
                    return (false, "schtasks 超时");
                }
                var ok = process.ExitCode == 0;
                var message = (ok ? stdout : stderr + " " + stdout).Trim();
                return (ok, message.Length > 300 ? message.Substring(0, 300) : message);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }
    }
}
