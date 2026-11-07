using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SeewoAutoLogin.Services
{
    /// <summary>
    /// 希沃客户端的定位与启动。
    ///
    /// 侧栏「启动希沃」、疑难解答页、悬浮窗重新登录都要「把希沃叫起来」。
    /// 原来的启动代码埋在 SeewoOverlay 的切换定时器里、只认三条硬编码路径，
    /// 换台机器装在别处就找不到 —— 这里收成一份可复用的定位逻辑。
    ///
    /// 定位顺序：运行中进程的主模块 → App Paths 注册项 → 卸载项 → 安装目录扫描 → 历史硬编码路径。
    /// 不从 PATH 找：希沃安装器不保证写 App Paths，但几乎一定写卸载项（卸载入口是它的门面）。
    /// </summary>
    internal static class SeewoLauncher
    {
        /// <summary>希沃主进程名的固定片段：EasiNote / EasiNote5 / EasiNote5C 都含它</summary>
        private const string ProcessKeyword = "EasiNote";

        /// <summary>ShowWindow 的 SW_RESTORE：从最小化恢复，否则 SetForegroundWindow 只会让任务栏闪一下</summary>
        private const int SwRestore = 9;

        /// <summary>卸载项里只认希沃白板本体</summary>
        private static readonly string[] ProductKeywords = { "希沃白板", "EasiNote" };

        /// <summary>必须排除：本程序自己的名字里也带「希沃」，认错了会把自身 exe 当成希沃启动</summary>
        private static readonly string[] ProductExcludes = { "自动登录", "SeewoAutoLogin", "SSO" };

        /// <summary>启动结果。按钮点下去必须有反馈，所以这里不抛异常，成败都走 Message。</summary>
        internal sealed class LaunchOutcome
        {
            internal bool Ok { get; set; }
            internal bool AlreadyRunning { get; set; }
            internal string Path { get; set; } = "";
            internal string Message { get; set; } = "";
        }

        /// <summary>希沃是否已经在跑（前端侧栏的状态点、疑难解答页的自查项都用它）</summary>
        internal static bool IsRunning()
        {
            var running = SnapshotProcesses();
            try { return running.Count > 0; }
            finally { DisposeAll(running); }
        }

        /// <summary>启动希沃；已经在跑时切到前台而不是再拉一个进程</summary>
        internal static LaunchOutcome Launch()
        {
            var running = SnapshotProcesses();
            try
            {
                if (running.Count > 0)
                {
                    var activated = false;
                    foreach (var process in running)
                    {
                        try
                        {
                            var hwnd = process.MainWindowHandle;
                            if (hwnd == IntPtr.Zero) continue;
                            ShowWindow(hwnd, SwRestore);
                            SetForegroundWindow(hwnd);
                            activated = true;
                            break;
                        }
                        catch
                        {
                            // 提权进程取句柄会抛：换下一个
                        }
                    }

                    return new LaunchOutcome
                    {
                        Ok = true,
                        AlreadyRunning = true,
                        Message = activated ? "希沃已经在运行，已切到前台" : "希沃已经在运行，窗口还在加载"
                    };
                }
            }
            finally { DisposeAll(running); }

            var exe = FindExecutable();
            if (string.IsNullOrEmpty(exe))
            {
                return new LaunchOutcome
                {
                    Ok = false,
                    Message = "没找到希沃客户端，请先安装希沃白板"
                };
            }

            try
            {
                var directory = Path.GetDirectoryName(exe);
                Process.Start(new ProcessStartInfo(exe)
                {
                    UseShellExecute = true,
                    // 工作目录设在安装目录：希沃的部分组件按相对路径找同目录的 dll / 资源
                    WorkingDirectory = string.IsNullOrEmpty(directory) ? string.Empty : directory
                });

                return new LaunchOutcome { Ok = true, Path = exe, Message = "正在启动希沃…" };
            }
            catch (Exception ex)
            {
                return new LaunchOutcome
                {
                    Ok = false,
                    Path = exe,
                    Message = "启动希沃失败：" + ex.Message
                };
            }
        }

        /// <summary>找出希沃主程序的完整路径；找不到返回 null（不抛异常）</summary>
        internal static string FindExecutable()
        {
            var running = SnapshotProcesses();
            try
            {
                foreach (var process in running)
                {
                    try
                    {
                        var file = process.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(file) && File.Exists(file)) return file;
                    }
                    catch { }
                }
            }
            finally { DisposeAll(running); }

            var fromAppPaths = TryAppPaths();
            if (fromAppPaths != null) return fromAppPaths;

            var fromUninstall = TryUninstallEntries();
            if (fromUninstall != null) return fromUninstall;

            var fromScan = TryInstallDirectories();
            if (fromScan != null) return fromScan;

            foreach (var known in LegacyPaths())
            {
                if (File.Exists(known)) return known;
            }

            return null;
        }

        #region 进程

        private static List<Process> SnapshotProcesses()
        {
            var matched = new List<Process>();
            try
            {
                foreach (var process in Process.GetProcesses())
                {
                    var keep = false;
                    try
                    {
                        keep = process.ProcessName.IndexOf(ProcessKeyword, StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                    catch { }

                    if (keep) matched.Add(process);
                    else { try { process.Dispose(); } catch { } }
                }
            }
            catch { }

            return matched;
        }

        private static void DisposeAll(List<Process> processes)
        {
            foreach (var process in processes)
            {
                try { process.Dispose(); } catch { }
            }
        }

        #endregion

        #region 注册表

        /// <summary>
        /// App Paths：文件关联/开始菜单启动时系统查的就是这里。
        /// 部分希沃版本会写，命中就是最准的（用户可能自定义了安装目录）。
        /// </summary>
        private static string TryAppPaths()
        {
            var keys = new[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\EasiNote.exe",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\EasiNote.exe",
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\EasiNote5.exe",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\EasiNote5.exe"
            };

            foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                foreach (var key in keys)
                {
                    try
                    {
                        using var sub = root.OpenSubKey(key);
                        var exe = NormalizeExecutablePath(sub?.GetValue(null) as string);
                        if (exe != null) return exe;
                    }
                    catch { }
                }
            }

            return null;
        }

        /// <summary>
        /// 卸载项：希沃装到哪都会在这里留一条 InstallLocation / DisplayIcon。
        /// InstallLocation 优先（目录里一定有主程序），DisplayIcon 兜底（只认文件名像 EasiNote 的）。
        /// </summary>
        private static string TryUninstallEntries()
        {
            var roots = new[] { Registry.LocalMachine, Registry.CurrentUser };
            var paths = new[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };

            foreach (var root in roots)
            {
                foreach (var path in paths)
                {
                    try
                    {
                        using var uninstall = root.OpenSubKey(path);
                        if (uninstall == null) continue;

                        foreach (var name in uninstall.GetSubKeyNames())
                        {
                            try
                            {
                                using var sub = uninstall.OpenSubKey(name);
                                if (sub == null) continue;

                                var display = sub.GetValue("DisplayName") as string;
                                if (string.IsNullOrWhiteSpace(display)) continue;
                                if (!ProductKeywords.Any(k => display.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                                if (ProductExcludes.Any(k => display.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)) continue;

                                var icon = sub.GetValue("DisplayIcon") as string;
                                var location = sub.GetValue("InstallLocation") as string;

                                if (!string.IsNullOrWhiteSpace(location))
                                {
                                    var hit = PickBest(EnumerateExecutables(location.Trim().Trim('"'), 2));
                                    if (hit != null) return hit;
                                }

                                var iconPath = NormalizeExecutablePath(icon);
                                if (iconPath != null
                                    && Path.GetFileName(iconPath).IndexOf(ProcessKeyword, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    return iconPath;
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }

            return null;
        }

        #endregion

        #region 目录扫描

        /// <summary>
        /// 安装目录扫描。希沃官方安装器的布局是 &lt;Program Files&gt;\Seewo\&lt;产品&gt;\EasiNote.exe，
        /// 也有少数定制安装直接建在 Program Files 顶层的 EasiNote* 目录里。
        /// 只扫两层、只认 Seewo / EasiNote 开头的目录，不在磁盘上乱翻。
        /// </summary>
        private static string TryInstallDirectories()
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            var roots = new List<string>();
            void AddRoot(string parent, string leaf)
            {
                if (string.IsNullOrWhiteSpace(parent)) return;
                roots.Add(Path.Combine(parent, leaf));
            }

            AddRoot(programFiles, "Seewo");
            AddRoot(programFilesX86, "Seewo");
            AddRoot(localAppData, "Seewo");
            AddRoot(Path.Combine(localAppData, "Programs"), "Seewo");

            var candidates = new List<string>();
            foreach (var root in roots) candidates.AddRange(EnumerateExecutables(root, 2));

            foreach (var baseDir in new[] { programFiles, programFilesX86 })
            {
                if (string.IsNullOrWhiteSpace(baseDir) || !Directory.Exists(baseDir)) continue;

                List<string> children;
                try { children = new List<string>(Directory.EnumerateDirectories(baseDir)); }
                catch { continue; }

                foreach (var child in children)
                {
                    var name = Path.GetFileName(child);
                    if (name == null) continue;
                    if (name.IndexOf("Seewo", StringComparison.OrdinalIgnoreCase) < 0
                        && name.IndexOf(ProcessKeyword, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    candidates.AddRange(EnumerateExecutables(child, 1));
                }
            }

            return PickBest(candidates);
        }

        /// <summary>在目录树里找 EasiNote*.exe，depth 为往下钻的层数</summary>
        private static List<string> EnumerateExecutables(string root, int depth)
        {
            var found = new List<string>();
            if (depth < 0 || string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return found;

            var direct = Path.Combine(root, "EasiNote.exe");
            if (File.Exists(direct))
            {
                found.Add(direct);
                return found;
            }

            try
            {
                foreach (var exe in Directory.EnumerateFiles(root, "EasiNote*.exe", SearchOption.TopDirectoryOnly))
                    found.Add(exe);
            }
            catch { }

            if (depth == 0) return found;

            List<string> children;
            try { children = new List<string>(Directory.EnumerateDirectories(root)); }
            catch { return found; }

            foreach (var child in children) found.AddRange(EnumerateExecutables(child, depth - 1));
            return found;
        }

        /// <summary>等多个候选时怎么选：EasiNote5 优先（当前主线），其次路径更短的（更像安装根而不是备份副本）</summary>
        private static string PickBest(IEnumerable<string> candidates)
        {
            return candidates
                .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p.IndexOf("EasiNote5", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1)
                .ThenBy(p => p.Length)
                .FirstOrDefault();
        }

        /// <summary>历史上一直写死的那几条路径，留作最后兜底</summary>
        private static IEnumerable<string> LegacyPaths()
        {
            yield return @"C:\Program Files\Seewo\EasiNote5\EasiNote.exe";
            yield return @"C:\Program Files (x86)\Seewo\EasiNote5\EasiNote.exe";
            yield return @"C:\Program Files\Seewo\EasiNote\EasiNote.exe";
            yield return @"C:\Program Files (x86)\Seewo\EasiNote\EasiNote.exe";
        }

        #endregion

        /// <summary>
        /// 注册表里的可执行路径三种写法都要认：&#34;C:\a\EasiNote.exe&#34;、带引号加 ,0 索引、
        /// 不带引号的裸路径。统一剥成文件路径，存在才返回。
        /// </summary>
        private static string NormalizeExecutablePath(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            var text = raw.Trim();
            if (text.StartsWith("\"", StringComparison.Ordinal))
            {
                var close = text.IndexOf('"', 1);
                if (close > 1) text = text.Substring(1, close - 1);
            }
            else
            {
                var exeIndex = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (exeIndex > 0) text = text.Substring(0, exeIndex + 4);
            }

            return File.Exists(text) ? text : null;
        }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    }
}
