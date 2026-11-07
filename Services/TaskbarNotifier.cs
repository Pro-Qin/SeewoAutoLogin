using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SeewoAutoLogin.Services
{
    /// <summary>
    /// 任务栏闪烁提醒。
    ///
    /// 用在「更新下载失败」这类需要人回来处理、又不该打断当前操作的地方：
    /// 老师授课时白板占着整个屏幕，弹出浏览器会把课件直接盖掉，
    /// 但什么都不提示又等于这事没发生。折中办法就是让任务栏图标闪，
    /// 由用户在自己方便的时机点开。FlashWindowEx 不发声、也不抢焦点。
    /// </summary>
    internal static class TaskbarNotifier
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct FLASHWINFO
        {
            public uint cbSize;
            public IntPtr hwnd;
            public uint dwFlags;
            public uint uCount;
            public uint dwTimeout;
        }

        private const uint FLASHW_ALL = 0x00000003;       // 标题栏 + 任务栏按钮一起闪
        private const uint FLASHW_TIMERNOFG = 0x0000000C; // 一直闪到窗口回到前台

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        /// <summary>闪烁任务栏按钮，直到窗口被激活。窗口本来就在前台时直接跳过。</summary>
        internal static void Flash(Window window)
        {
            if (window == null) return;
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;
                // 前台窗口闪了也看不见，这里只处理「人在别处」的情况
                if (GetForegroundWindow() == hwnd) return;

                var info = new FLASHWINFO
                {
                    cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                    hwnd = hwnd,
                    dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
                    uCount = 0,
                    dwTimeout = 0
                };
                FlashWindowEx(ref info);
            }
            catch
            {
                // 提醒手段失效不该把更新流程一起拖垮
            }
        }
    }
}
