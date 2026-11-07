using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;

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
        ///
        /// 判定必须基于启动项里「实际写着的命令行」，不能基于 schtasks 的文本输出。
        /// 1.14.26 的故障就是这样来的：按 Encoding.Unicode 去解 schtasks /Query /XML 的输出
        /// （在中文系统上其实是 UTF-8 字节），解出来是乱码，IndexOf 永远失败，于是每 3 分钟
        /// 重建一次开机自启，日志里刷成一片「已重建开机自启」。
        /// COM 与任务目录里的 XML 都是结构化数据，没有这个编码陷阱。
        /// </summary>
        internal static bool IsEnabledForCurrentPath()
        {
            var exe = ExecutablePath;
            if (string.IsNullOrEmpty(exe)) return IsEnabled;

            var mode = CurrentMode();
            if (mode == "registry")
                return CommandTargetsExe(RegistryValue(), exe);

            if (mode == "task")
            {
                var commandLine = ReadTaskCommandLine();
                // 两条探测路都断了（任务计划服务不可用、任务目录读不到）：退回「任务存在即认为已启用」。
                // 宁可漏修一次，也不要因为读不出来就每轮重建。
                if (commandLine == null) return TaskExists();
                return CommandTargetsExe(commandLine, exe);
            }
            return false;
        }

        /// <summary>
        /// 读计划任务里实际执行的命令行；两条路都读不到时返回 null（与「读到但没有 Exec 动作」的 "" 区分开）。
        /// 首选 COM，回退到任务计划服务落盘的 XML 文件。
        /// </summary>
        internal static string ReadTaskCommandLine()
        {
            var fromCom = ReadTaskCommandLineViaCom();
            if (fromCom != null) return fromCom;
            return ReadTaskCommandLineFromTaskFile();
        }

        /// <summary>结构化读取：Task Scheduler COM 的 GetTask().Definition.Actions。</summary>
        private static string ReadTaskCommandLineViaCom()
        {
            object root = null;
            try
            {
                var type = Type.GetTypeFromProgID("Schedule.Service");
                if (type == null) return null;
                root = Activator.CreateInstance(type);
                if (root == null) return null;

                dynamic service = root;
                service.Connect();
                dynamic actions = service.GetFolder("\\").GetTask(TaskName).Definition.Actions;
                for (var i = 1; i <= actions.Count; i++)   // COM 集合从 1 开始
                {
                    dynamic action = actions.Item(i);
                    if ((int)action.Type != 0) continue;   // 0 = TASK_ACTION_EXEC
                    var path = (string)action.Path ?? "";
                    var arguments = "";
                    try { arguments = (string)action.Arguments ?? ""; } catch { }
                    return (path + " " + arguments).Trim();
                }
                return "";
            }
            catch { return null; }
            finally
            {
                if (root != null && Marshal.IsComObject(root))
                {
                    try { Marshal.ReleaseComObject(root); } catch { }
                }
            }
        }

        /// <summary>回退读取 %SystemRoot%System32\Tasks\SeewoAutoLogin（任务计划服务的落盘定义）。</summary>
        private static string ReadTaskCommandLineFromTaskFile()
        {
            try
            {
                var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (string.IsNullOrEmpty(windows)) return null;
                var path = Path.Combine(windows, "System32", "Tasks", TaskName);
                if (!File.Exists(path)) return null;

                var text = DecodeTaskXml(File.ReadAllBytes(path));
                var command = MatchTaskElement(text, "Command");
                if (string.IsNullOrEmpty(command)) return null;
                return (command + " " + MatchTaskElement(text, "Arguments")).Trim();
            }
            catch { return null; }
        }

        private static string MatchTaskElement(string xml, string element)
        {
            var match = Regex.Match(xml, "<" + element + ">(.*?)</" + element + ">",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            return match.Success ? System.Net.WebUtility.HtmlDecode(match.Groups[1].Value).Trim() : "";
        }

        /// <summary>
        /// 任务文件通常是 UTF-16LE 带 BOM，但手工导入或第三方工具写的可能没有 BOM，
        /// 所以不能直接假定编码（这就是 1.14.26 踩过的坑）。
        /// </summary>
        private static string DecodeTaskXml(byte[] bytes)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

            // 无 BOM：纯 ASCII 内容按 UTF-16 存时，奇数位几乎全是 0x00
            var limit = Math.Min(bytes.Length, 64);
            var zeros = 0;
            for (var i = 1; i < limit; i += 2) if (bytes[i] == 0) zeros++;
            var utf16 = zeros * 2 >= limit / 2;
            return utf16 ? Encoding.Unicode.GetString(bytes) : Encoding.UTF8.GetString(bytes);
        }

        /// <summary>
        /// 命令行是否指向本程序。两侧都去掉引号再比：计划任务里写的是
        /// "C:\...\SeewoAutoLogin.exe" --minimized，带引号时裸路径是找不到的。
        /// </summary>
        private static bool CommandTargetsExe(string commandLine, string exe)
        {
            if (string.IsNullOrWhiteSpace(commandLine) || string.IsNullOrWhiteSpace(exe)) return false;
            var target = exe.Replace("\"", "").Trim();
            if (target.Length == 0) return false;
            return commandLine.Replace("\"", "").IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0;
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

        /// <summary>
        /// 只用于「创建/删除/查询存在性」这类只看退出码的操作。
        /// 输出文本一律不解析：schtasks 的输出编码随系统区域设置变化，
        /// 按固定编码去解必然在别的语言环境下碎掉（1.14.26 的自启重建死循环就源于此）。
        /// </summary>
        private static (bool ok, string message) RunSchtasks(string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    // 只用于把错误提示原样带进日志：Encoding.Default 在 net8 上是 UTF-8。
                    StandardOutputEncoding = Encoding.Default,
                    StandardErrorEncoding = Encoding.Default
                };
                foreach (var a in args) psi.ArgumentList.Add(a);

                using var process = Process.Start(psi);
                if (process == null) return (false, "无法启动 schtasks.exe");

                // 先把读取挂成异步：同步的 ReadToEnd 会一直阻塞到子进程关掉 stdout，
                // 放在前面的话，只要 schtasks 挂住（被安全软件拦、任务计划服务无响应），
                // 下面的 15 秒超时与 Kill 就永远轮不到执行 —— 用户看到的是「双击图标什么都没发生」。
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(15000))
                {
                    try { process.Kill(); } catch { }
                    return (false, "schtasks 超时");
                }
                var stdout = stdoutTask.GetAwaiter().GetResult();
                var stderr = stderrTask.GetAwaiter().GetResult();
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
