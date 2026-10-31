using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SeewoAutoLogin.Services
{
    /// <summary>
    /// 把 .swalcfg 关联到本程序：双击配置包直接进入导入向导。
    ///
    /// 全部写在 HKCU\Software\Classes 下 —— 只影响当前用户，不需要管理员，
    /// 卸载时也能干净删掉；不碰 HKLM，避免在一体机上留下「装了又卸不干净」的痕迹。
    /// </summary>
    internal static class FileAssociationService
    {
        public const string ProgId = "SeewoAutoLogin.ConfigPackage";
        private const string Extension = ".swalcfg";

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

        /// <summary>注册（或修复）关联。图标用释放到数据目录的 ico，避免依赖 exe 内的资源索引。</summary>
        public static bool EnsureRegistered(string exePath, string iconPath, Action<string> log)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return false;

                using (var ext = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + Extension))
                {
                    ext?.SetValue("", ProgId);
                    ext?.SetValue("Content Type", "application/x-seewoautologin-config");
                }

                using (var prog = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId))
                {
                    prog?.SetValue("", "SeewoAutoLogin 配置文件");
                    using (var icon = prog!.CreateSubKey("DefaultIcon"))
                    {
                        var value = string.IsNullOrWhiteSpace(iconPath) || !File.Exists(iconPath)
                            ? "\"" + exePath + "\",0"
                            : "\"" + iconPath + "\"";
                        icon.SetValue("", value);
                    }
                    using (var command = prog.CreateSubKey(@"shell\open\command"))
                        command?.SetValue("", "\"" + exePath + "\" --import-config \"%1\"");
                }

                // 通知资源管理器：图标与关联立刻生效，不用等重启
                SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0x0000, IntPtr.Zero, IntPtr.Zero);
                log?.Invoke("[Assoc] .swalcfg 已关联到本程序");
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke($"[Assoc] 注册 .swalcfg 关联失败: {ex.GetType().Name} - {ex.Message}");
                return false;
            }
        }

        public static void Unregister(Action<string> log)
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + ProgId, throwOnMissingSubKey: false);
                using var ext = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + Extension, writable: true);
                if (ext != null && string.Equals(ext.GetValue("") as string, ProgId, StringComparison.Ordinal))
                    ext.DeleteValue("", throwOnMissingValue: false);
                SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);
                log?.Invoke("[Assoc] 已移除 .swalcfg 关联");
            }
            catch (Exception ex)
            {
                log?.Invoke($"[Assoc] 移除关联失败: {ex.GetType().Name}");
            }
        }

        /// <summary>把内嵌的 .swalcfg 图标释放到数据目录，返回路径（失败返回空）。</summary>
        public static string PublishIcon()
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SeewoAutoLogin");
                Directory.CreateDirectory(dir);
                var target = Path.Combine(dir, "swalcfg.ico");
                var assembly = typeof(FileAssociationService).Assembly;
                using var stream = assembly.GetManifestResourceStream("SeewoAutoLogin.Resources.swalcfg.ico");
                if (stream == null) return File.Exists(target) ? target : "";
                using var file = File.Create(target);
                stream.CopyTo(file);
                return target;
            }
            catch
            {
                return "";
            }
        }
    }
}
