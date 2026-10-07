using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace SeewoAutoLogin.Services
{
    internal enum InstallKind
    {
        /// <summary>安装包安装的版本（注册表里有卸载项，且 InstallLocation 指向当前目录）</summary>
        Installed,
        /// <summary>解压即用的便携版</summary>
        Portable
    }

    /// <summary>
    /// 判断当前这份程序是「安装版」还是「便携版」。
    ///
    /// 判据不是看路径（用户完全可以把安装版装到 D:\tools），而是**问安装器自己**：
    /// Inno Setup 每次安装都会写一个卸载项（AppId 默认取 AppName，我们这里是 SeewoAutoLogin_is1），
    /// 里面带 InstallLocation。当前 exe 目录和它一致 → 安装版；对不上 → 便携版。
    ///
    /// 这个区分很关键：便携版没有安装器接管文件，走静默安装只会把新版装进 Program Files，
    /// 而用户继续运行解压目录里的旧 exe —— 表现就是「一直提示更新，版本却永远是老的」。
    /// </summary>
    internal static class UpdateChannel
    {
        private const string AppId = "SeewoAutoLogin";

        private static readonly string[] UninstallRoots =
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };

        /// <summary>当前进程可执行文件所在目录。</summary>
        public static string CurrentDirectory
        {
            get
            {
                try
                {
                    var exe = Process.GetCurrentProcess().MainModule?.FileName;
                    return string.IsNullOrWhiteSpace(exe) ? "" : Path.GetDirectoryName(exe) ?? "";
                }
                catch { return ""; }
            }
        }

        /// <summary>判断当前这份程序是安装版还是便携版。</summary>
        public static InstallKind Detect()
        {
            try
            {
                var exeDir = CurrentDirectory;
                if (string.IsNullOrWhiteSpace(exeDir)) return InstallKind.Portable;

                foreach (var root in UninstallRoots)
                {
                    foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
                    {
                        using (var key = hive.OpenSubKey($@"{root}\{AppId}_is1"))
                        {
                            var location = key?.GetValue("InstallLocation") as string;
                            if (string.IsNullOrWhiteSpace(location)) continue;

                            // 注意去掉尾部反斜杠后再比，安装器写的是带尾斜杠的目录
                            if (string.Equals(
                                    Path.GetFullPath(location).TrimEnd('\\'),
                                    Path.GetFullPath(exeDir).TrimEnd('\\'),
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                return InstallKind.Installed;
                            }
                        }
                    }
                }
            }
            catch { }
            return InstallKind.Portable;
        }

        public static string Describe(InstallKind kind)
            => kind == InstallKind.Installed ? "安装版" : "便携版";
    }
}
