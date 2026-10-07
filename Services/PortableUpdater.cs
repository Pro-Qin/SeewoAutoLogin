using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace SeewoAutoLogin.Services
{
    /// <summary>
    /// 便携版自更新：解压即用的那份程序没有安装器接管文件，只能自己替换自己。
    ///
    /// Windows 不允许运行中的 exe 覆盖自己，所以流程是：
    ///   1. 把新版整理到一个临时 staging 目录（单文件直接复制，zip 先解压）；
    ///   2. 备份当前的整个程序目录到 %LOCALAPPDATA%\SeewoAutoLogin\backup\&lt;版本&gt;；
    ///   3. 生成一个 cmd 脚本：等本进程退出 → 备份 → 用 xcopy 覆盖 → 启动新版 → 自删；
    ///   4. 本程序退出，脚本接手。
    ///
    /// 全程隐藏窗口，失败时日志与备份都留在磁盘上，不会把程序弄丢。
    /// </summary>
    internal static class PortableUpdater
    {
        private static string UpdateRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SeewoAutoLogin", "portable-update");

        /// <summary>备份根目录：%LOCALAPPDATA%\SeewoAutoLogin\backup</summary>
        public static string BackupRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SeewoAutoLogin", "backup");

        /// <summary>
        /// 把下载到的更新包整理成「可以直接覆盖到程序目录」的一组文件，返回 staging 目录。
        /// 下载到的是单文件 exe 就复制过去；是 zip 就先解压。失败返回 null。
        /// </summary>
        public static string PrepareStaging(string downloadedPath, Action<string> log)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(downloadedPath) || !File.Exists(downloadedPath))
                    return null;

                var staging = Path.Combine(UpdateRoot, "staging-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
                Directory.CreateDirectory(staging);

                if (downloadedPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    ZipFile.ExtractToDirectory(downloadedPath, staging);

                    // zip 里可能套了一层目录，往下找真正的 exe
                    var exe = FindFile(staging, "SeewoAutoLogin.exe");
                    if (exe == null)
                    {
                        log?.Invoke("[Portable] 更新包内没有找到 SeewoAutoLogin.exe，放弃自更新");
                        return null;
                    }
                    var exeDir = Path.GetDirectoryName(exe);
                    if (!string.Equals(exeDir, staging, StringComparison.OrdinalIgnoreCase))
                        staging = exeDir;
                }
                else
                {
                    var name = Path.GetFileName(downloadedPath);
                    // 下载文件名是 SeewoAutoLogin_Setup_vX.exe 之类，统一改成程序本身的文件名
                    if (name.StartsWith("SeewoAutoLogin", StringComparison.OrdinalIgnoreCase) &&
                        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                        !name.Equals("SeewoAutoLogin.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        name = "SeewoAutoLogin.exe";
                    }
                    File.Copy(downloadedPath, Path.Combine(staging, name), overwrite: true);
                }

                log?.Invoke($"[Portable] 新版本已准备到 {staging}");
                return staging;
            }
            catch (Exception ex)
            {
                log?.Invoke($"[Portable] 整理更新包失败: {ex.GetType().Name} - {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 生成并隐藏启动替换脚本。返回 true 表示脚本已拉起，调用方应随后主动退出程序。
        /// </summary>
        public static bool LaunchReplaceScript(string stagingDir, string targetVersion, Action<string> log)
        {
            try
            {
                var targetDir = UpdateChannel.CurrentDirectory;
                if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir))
                {
                    log?.Invoke("[Portable] 拿不到程序目录，放弃自更新");
                    return false;
                }
                if (string.IsNullOrWhiteSpace(stagingDir) || !Directory.Exists(stagingDir))
                    return false;

                var appExe = Path.Combine(targetDir, "SeewoAutoLogin.exe");
                if (!File.Exists(appExe))
                {
                    log?.Invoke($"[Portable] 当前目录里没有 SeewoAutoLogin.exe（{targetDir}），放弃自更新");
                    return false;
                }

                var backupDir = Path.Combine(BackupRoot, string.IsNullOrWhiteSpace(targetVersion) ? "unknown" : targetVersion);
                var scriptPath = Path.Combine(UpdateRoot, $"apply-{Guid.NewGuid():N}.cmd");
                var applyLog = Path.Combine(UpdateRoot, "apply.log");
                var pid = Process.GetCurrentProcess().Id;

                Directory.CreateDirectory(UpdateRoot);

                var sb = new StringBuilder();
                sb.AppendLine("@echo off");
                sb.AppendLine("setlocal");
                sb.AppendLine($"set \"PID={pid}\"");
                sb.AppendLine($"set \"SRC={stagingDir.TrimEnd('\\')}\"");
                sb.AppendLine($"set \"DST={targetDir.TrimEnd('\\')}\"");
                sb.AppendLine($"set \"EXE={appExe}\"");
                sb.AppendLine($"set \"BAK={backupDir.TrimEnd('\\')}\"");
                sb.AppendLine($"set \"LOG={applyLog}\"");
                sb.AppendLine("echo [%date% %time%] 等待主程序退出 >> \"%LOG%\"");
                // 最多等 60 秒（tasklist 两次轮询之间隔 1 秒）
                sb.AppendLine("for /l %%i in (1,1,60) do (");
                sb.AppendLine("  tasklist /fi \"PID eq %PID%\" 2>nul | find \"%PID%\" >nul || goto ready");
                sb.AppendLine("  ping -n 2 127.0.0.1 >nul");
                sb.AppendLine(")");
                sb.AppendLine(":ready");
                sb.AppendLine("echo [%date% %time%] 备份当前版本到 %BAK% >> \"%LOG%\"");
                sb.AppendLine("if not exist \"%BAK%\" mkdir \"%BAK%\"");
                sb.AppendLine("xcopy /E /Y /I \"%DST%\\*\" \"%BAK%\\\" >> \"%LOG%\" 2>&1");
                sb.AppendLine("echo [%date% %time%] 覆盖新版本 >> \"%LOG%\"");
                // 覆盖可能撞上残留的文件占用，重试若干次
                sb.AppendLine("for /l %%i in (1,1,20) do (");
                sb.AppendLine("  xcopy /E /Y /I \"%SRC%\\*\" \"%DST%\\\" >> \"%LOG%\" 2>&1");
                sb.AppendLine("  if not errorlevel 1 goto done");
                sb.AppendLine("  ping -n 2 127.0.0.1 >nul");
                sb.AppendLine(")");
                sb.AppendLine("echo [%date% %time%] 覆盖失败，保留备份与原程序 >> \"%LOG%\"");
                sb.AppendLine("exit /b 1");
                sb.AppendLine(":done");
                sb.AppendLine("echo [%date% %time%] 完成，启动新版本 >> \"%LOG%\"");
                sb.AppendLine("start \"\" \"%EXE%\" --updated");
                sb.AppendLine("del \"%~f0\"");
                sb.AppendLine("exit /b 0");

                File.WriteAllText(scriptPath, sb.ToString(), new UTF8Encoding(false));

                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    // /c 执行脚本；配合下面的隐藏窗口，用户看不到任何黑框
                    Arguments = $"/c \"{scriptPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = UpdateRoot
                };
                Process.Start(psi);

                log?.Invoke($"[Portable] 已拉起替换脚本（备份到 {backupDir}），程序即将退出并由脚本完成替换");
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke($"[Portable] 启动替换脚本失败: {ex.GetType().Name} - {ex.Message}");
                return false;
            }
        }

        private static string FindFile(string dir, string fileName)
        {
            try
            {
                foreach (var file in Directory.GetFiles(dir, fileName, SearchOption.AllDirectories))
                    return file;
            }
            catch { }
            return null;
        }
    }
}
