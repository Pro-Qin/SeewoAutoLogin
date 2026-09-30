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
        #region Elevation

        /// <summary>当前是否具有管理员权限</summary>
        private static bool IsAdministrator()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 以管理员身份（runas）重启当前程序，参数追加 --elevated。
        /// 返回 true 表示已成功拉起提权进程（当前进程应随后退出）。
        /// </summary>
        private bool TryElevate()
        {
            try
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exePath))
                {
                    WriteDiagnosticLog("[Elevate] 无法获取可执行文件路径，跳过提权");
                    return false;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "--elevated",
                    UseShellExecute = true,
                    Verb = "runas"
                };
                Process.Start(psi);
                return true;
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"[Elevate] 提权失败（用户取消或系统限制）: {ex.Message}");
                return false;
            }
        }

        #endregion
    }
}
