using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SeewoAutoLogin.Services
{
    /// <summary>希沃客户端版本检测结果</summary>
    internal sealed class SeewoVersionCheckResult
    {
        /// <summary>本次检测到的版本；未检测到（未安装 / 未运行 / 读取失败）时为 null</summary>
        public string Current { get; set; }

        /// <summary>上次记录的版本；首次运行时为 null</summary>
        public string Previous { get; set; }

        /// <summary>版本相比上次记录发生了变化</summary>
        public bool Changed { get; set; }

        /// <summary>首次记录（没有历史记录），此时只记录不提示</summary>
        public bool FirstSeen { get; set; }

        /// <summary>版本号来源（进程 / 注册表 / 安装目录），便于归因</summary>
        public string Source { get; set; }
    }

    /// <summary>落盘的版本记录</summary>
    internal sealed class SeewoVersionRecord
    {
        [JsonPropertyName("version")] public string Version { get; set; }
        [JsonPropertyName("source")] public string Source { get; set; }
        [JsonPropertyName("recordedAtUtc")] public DateTime RecordedAtUtc { get; set; }
        [JsonPropertyName("previousVersion")] public string PreviousVersion { get; set; }
    }

    /// <summary>
    /// 希沃客户端版本监控。
    ///
    /// 希沃客户端升级后接口/窗口结构可能变化，导致本程序失效。这里记录每次检测到的版本，
    /// 版本一旦变化就在日志里留痕、并在主界面首次显示时提示用户 —— 出问题时可以快速归因到「希沃更新了」，
    /// 而不是让用户以为账号或本程序坏了。
    ///
    /// 检测顺序：运行中的进程主模块 → 注册表卸载项 → 常见安装目录下的可执行文件。
    /// 全过程不抛异常，取不到一律返回 null。
    /// </summary>
    internal static class SeewoVersionMonitor
    {
        /// <summary>进程名包含该关键字即视为希沃客户端（EasiNote / EasiNote5 / EasiNote5C …）</summary>
        private const string ProcessKeyword = "EasiNote";

        /// <summary>注册表卸载项里匹配的产品名关键字（只认希沃白板本体，不能用泛化的「希沃」）</summary>
        private static readonly string[] ProductKeywords = { "希沃白板", "EasiNote" };

        /// <summary>必须排除的卸载项：本程序自己的名字里也带「希沃」，匹配错了会把自身版本当成希沃客户端版本</summary>
        private static readonly string[] ProductExcludes = { "自动登录", "SeewoAutoLogin" };

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        #region 版本获取

        /// <summary>
        /// 获取当前希沃客户端版本；取不到返回 null（不抛异常）。
        /// </summary>
        internal static string TryGetVersion(out string source)
        {
            source = "";

            var fromProcess = TryGetVersionFromProcess(out var processName);
            if (!string.IsNullOrEmpty(fromProcess))
            {
                source = "进程:" + processName;
                return fromProcess;
            }

            var fromRegistry = TryGetVersionFromRegistry(out var productName);
            if (!string.IsNullOrEmpty(fromRegistry))
            {
                source = "注册表:" + productName;
                return fromRegistry;
            }

            var fromFile = TryGetVersionFromInstallDir(out var filePath);
            if (!string.IsNullOrEmpty(fromFile))
            {
                source = "文件:" + filePath;
                return fromFile;
            }

            return null;
        }

        /// <summary>是否检测到希沃进程在运行（用于诊断包与状态展示）</summary>
        internal static string DescribeProcessState()
        {
            try
            {
                var names = new List<string>();
                foreach (var process in Process.GetProcesses())
                {
                    try
                    {
                        if (process.ProcessName.IndexOf(ProcessKeyword, StringComparison.OrdinalIgnoreCase) >= 0)
                            names.Add(process.ProcessName + "(" + process.Id + ")");
                    }
                    catch { }
                    finally { try { process.Dispose(); } catch { } }
                }
                return names.Count == 0 ? "未运行" : string.Join(", ", names);
            }
            catch (Exception ex)
            {
                return "检测失败：" + ex.GetType().Name;
            }
        }

        private static string TryGetVersionFromProcess(out string processName)
        {
            processName = "";
            try
            {
                foreach (var process in Process.GetProcesses())
                {
                    try
                    {
                        if (process.ProcessName.IndexOf(ProcessKeyword, StringComparison.OrdinalIgnoreCase) < 0) continue;

                        var info = process.MainModule?.FileVersionInfo;
                        var version = Normalize(info?.FileVersion) ?? Normalize(info?.ProductVersion);
                        if (!string.IsNullOrEmpty(version))
                        {
                            processName = process.ProcessName;
                            return version;
                        }
                    }
                    catch
                    {
                        // 提权进程/受保护进程取 MainModule 会抛 AccessDenied：跳过，交给下一个来源
                    }
                    finally
                    {
                        try { process.Dispose(); } catch { }
                    }
                }
            }
            catch { }
            return null;
        }

        private static string TryGetVersionFromRegistry(out string productName)
        {
            productName = "";
            var roots = new[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };

            foreach (var rootPath in roots)
            {
                try
                {
                    using var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(rootPath);
                    if (root == null) continue;

                    foreach (var subName in root.GetSubKeyNames())
                    {
                        try
                        {
                            using var sub = root.OpenSubKey(subName);
                            if (sub == null) continue;

                            var display = sub.GetValue("DisplayName")?.ToString();
                            if (string.IsNullOrWhiteSpace(display)) continue;
                            if (!ProductKeywords.Any(k => display.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                            if (ProductExcludes.Any(k => display.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)) continue;

                            var version = Normalize(sub.GetValue("DisplayVersion")?.ToString());
                            if (string.IsNullOrEmpty(version)) continue;

                            productName = display;
                            return version;
                        }
                        catch { }
                    }
                }
                catch { }
            }

            return null;
        }

        private static string TryGetVersionFromInstallDir(out string filePath)
        {
            filePath = "";
            try
            {
                var roots = new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Seewo"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Seewo")
                };

                foreach (var root in roots)
                {
                    if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;

                    // 只扫两层、最多 60 个候选文件，避免在磁盘上乱翻
                    var candidates = new List<string>();
                    try
                    {
                        foreach (var dir in Directory.GetDirectories(root))
                        {
                            candidates.AddRange(SafeEnumerateExe(dir));
                            if (candidates.Count > 60) break;
                        }
                        candidates.AddRange(SafeEnumerateExe(root));
                    }
                    catch { }

                    foreach (var exe in candidates)
                    {
                        if (Path.GetFileName(exe).IndexOf(ProcessKeyword, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var version = Normalize(FileVersionInfo.GetVersionInfo(exe).FileVersion);
                        if (string.IsNullOrEmpty(version)) continue;
                        filePath = exe;
                        return version;
                    }
                }
            }
            catch { }
            return null;
        }

        private static IEnumerable<string> SafeEnumerateExe(string dir)
        {
            try { return Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly); }
            catch { return Array.Empty<string>(); }
        }

        /// <summary>去掉空白并剔除明显的空值</summary>
        private static string Normalize(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return null;
            version = version.Trim();
            return version.Length == 0 ? null : version;
        }

        #endregion

        #region 比对与记录

        /// <summary>
        /// 检测一次并与上次记录比对。状态文件默认放在 <paramref name="stateFilePath"/>。
        ///
        /// - 首次运行（无历史记录）：只记录，FirstSeen = true，Changed = false，不提示；
        /// - 版本变化：更新记录，Changed = true 并写 [SeewoVersion] 日志；
        /// - 版本未变 / 本次取不到版本：不动记录，Changed = false。
        /// </summary>
        internal static SeewoVersionCheckResult CheckAndRecord(string stateFilePath, Action<string> log)
        {
            var result = new SeewoVersionCheckResult();

            string source = "";
            string current = null;
            try { current = TryGetVersion(out source); }
            catch { current = null; }

            result.Current = current;
            result.Source = source;

            if (string.IsNullOrEmpty(current))
            {
                Log(log, "[SeewoVersion] 未检测到希沃客户端版本（可能未安装或未运行），本次跳过比对");
                return result;
            }

            SeewoVersionRecord previous = null;
            try { previous = ReadRecord(stateFilePath); }
            catch { previous = null; }

            if (previous == null || string.IsNullOrWhiteSpace(previous.Version))
            {
                result.FirstSeen = true;
                result.Previous = null;
                result.Changed = false;
                WriteRecord(stateFilePath, current, source, null);
                Log(log, $"[SeewoVersion] 首次记录希沃客户端版本 {current}（来源 {source}），不提示用户");
                return result;
            }

            result.Previous = previous.Version;

            if (string.Equals(previous.Version, current, StringComparison.OrdinalIgnoreCase))
            {
                result.Changed = false;
                Log(log, $"[SeewoVersion] 版本未变化：{current}（来源 {source}）");
                return result;
            }

            // 版本来源可能在两次启动间切换（希沃运行时读进程主模块、未运行时读注册表/安装目录），
            // 不同来源的版本号精度并不一致（注册表常见 5.2.4，文件版本常见 5.2.4.9855）。
            // 此时只把「主版本段」不同才算真的升级，否则会出现莫名其妙的「希沃已更新」提示。
            var sameSource = string.Equals(previous.Source ?? "", source ?? "", StringComparison.OrdinalIgnoreCase);
            if (!sameSource && string.Equals(CoreVersion(previous.Version), CoreVersion(current), StringComparison.OrdinalIgnoreCase))
            {
                result.Changed = false;
                Log(log, $"[SeewoVersion] 版本号来源切换（{previous.Version} → {current}；{previous.Source} → {source}），视为同一版本，不提示");
                WriteRecord(stateFilePath, current, source, previous.Version);
                return result;
            }

            result.Changed = true;
            WriteRecord(stateFilePath, current, source, previous.Version);
            Log(log, $"[SeewoVersion] 从 {previous.Version} 变为 {current}（来源 {source}），将在主界面提示用户");
            return result;
        }

        /// <summary>取版本号的主版本段（前 3 段），用于跨来源比较：5.2.4 与 5.2.4.9855 视为同一版本</summary>
        private static string CoreVersion(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return "";
            var parts = version.Split('.');
            if (parts.Length <= 3) return version.Trim();
            return string.Join(".", parts.Take(3)).Trim();
        }

        private static SeewoVersionRecord ReadRecord(string stateFilePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(stateFilePath) || !File.Exists(stateFilePath)) return null;
                using var stream = new FileStream(stateFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
                var json = reader.ReadToEnd();
                if (string.IsNullOrWhiteSpace(json)) return null;
                return JsonSerializer.Deserialize<SeewoVersionRecord>(json);
            }
            catch
            {
                // 状态文件损坏等价于「没有历史记录」：当作首次记录，不影响启动
                return null;
            }
        }

        private static void WriteRecord(string stateFilePath, string version, string source, string previousVersion)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(stateFilePath)) return;
                var dir = Path.GetDirectoryName(stateFilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var record = new SeewoVersionRecord
                {
                    Version = version,
                    Source = source,
                    RecordedAtUtc = DateTime.UtcNow,
                    PreviousVersion = previousVersion
                };

                var temp = stateFilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(record, JsonOptions), new System.Text.UTF8Encoding(false));
                if (File.Exists(stateFilePath))
                {
                    try { File.Replace(temp, stateFilePath, null, ignoreMetadataErrors: true); }
                    catch { File.Copy(temp, stateFilePath, overwrite: true); try { File.Delete(temp); } catch { } }
                }
                else
                {
                    File.Move(temp, stateFilePath);
                }
            }
            catch
            {
                // 记录写不进去只影响下一次的比对，绝不影响启动流程
            }
        }

        private static void Log(Action<string> log, string message)
        {
            try { log?.Invoke(message); } catch { }
        }

        #endregion
    }
}
