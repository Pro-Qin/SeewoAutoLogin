using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace SeewoAutoLogin
{
    public partial class SeewoOverlay : Window
    {
        private readonly App _app;
        private System.Windows.Threading.DispatcherTimer _trackTimer;
        private System.Windows.Threading.DispatcherTimer _autoCloseTimer;
        private bool _closing;
        private IntPtr _seewoHwnd;

        public SeewoOverlay()
        {
            InitializeComponent();
            _app = (App)Application.Current;
            DpiChanged += (s, e) => TrackSeewo();
            RefreshList();
            Loaded += (s, e) => StartTracking();
        }

        private void StartTracking()
        {
            var overlayHwnd = new WindowInteropHelper(this).Handle;

            // 无焦点抢夺：加 WS_EX_NOACTIVATE
            var exStyle = User32.GetWindowLong(overlayHwnd, User32.GWL_EXSTYLE);
            User32.SetWindowLong(overlayHwnd, User32.GWL_EXSTYLE, exStyle | User32.WS_EX_NOACTIVATE);

            _trackTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _trackTimer.Tick += (s, e) => TrackSeewo();
            _trackTimer.Start();
            TrackSeewo();

            // 20s 自动关闭（托盘显示遮罩后）
            _autoCloseTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            _autoCloseTimer.Tick += (s, e) => { _autoCloseTimer.Stop(); Close(); };
            _autoCloseTimer.Start();
        }

        private void TrackSeewo()
        {
            if (_closing) return;
            var hwnd = FindEasiNote();
            if (hwnd == IntPtr.Zero) { Hide(); return; }
            _seewoHwnd = hwnd;
            if (User32.IsIconic(hwnd)) { Hide(); return; }
            if (!IsVisible) Show();
            RepositionOverlay();
        }

        private void RepositionOverlay()
        {
            var ovHwnd = new WindowInteropHelper(this).Handle;
            double dpiScale = GetDpi() / 96.0;

            User32.GetWindowRect(_seewoHwnd, out RECT win);

            int px24 = (int)Math.Round(24 * dpiScale);
            int px16 = (int)Math.Round(16 * dpiScale);

            int x = win.Left + px24;
            int y = win.Top + px16;
            int overlayW = Math.Max(180, (win.Right - win.Left) / 2 - px24);
            int overlayH = Math.Min((int)Math.Round(540 * dpiScale), (win.Bottom - win.Top) - px16);

            // 屏幕坐标 + hWndInsertAfter=希沃句柄（Z 序紧跟希沃，其他窗口遮挡时一起被挡）
            // 不再使用 SetParent 子窗口方案——子窗口 + WPF 屏幕坐标状态会互相打架导致偏移随希沃位置漂移
            User32.SetWindowPos(ovHwnd, _seewoHwnd, x, y, overlayW, overlayH,
                User32.SWP_NOACTIVATE | User32.SWP_SHOWWINDOW);
        }

        private double GetDpi()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var hdc = User32.GetDC(hwnd);
            if (hdc == IntPtr.Zero) return 96;
            int dpiX = User32.GetDeviceCaps(hdc, 88);
            User32.ReleaseDC(hwnd, hdc);
            return dpiX > 0 ? dpiX : 96;
        }

        private void RefreshList()
        {
            var all = _app.Config.Accounts.ToList();
            var unlisted = all.Skip(6).ToList();
            AccountList.ItemsSource = unlisted.Select(a => new OverlayAccount
            {
                Id = a.Id,
                DisplayName = a.DisplayName ?? a.Username ?? "未命名",
                MaskedContact = MaskPhone(a.Username ?? "")
            }).ToList();
            if (unlisted.Count == 0 && !_closing) Close();
        }

        private static string MaskPhone(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            var digits = Regex.Replace(raw, @"\D", "");
            if (digits.Length >= 7) return digits[..3] + "****" + digits[^4..];
            if (digits.Length >= 4) return digits[..1] + "***" + digits[^1..];
            return raw;
        }

        private void AccountItem_Click(object sender, MouseButtonEventArgs e)
        {
            if (_closing) return;
            if (sender is FrameworkElement fe && fe.DataContext is OverlayAccount acct)
            {
                var list = _app.Config.Accounts;
                var idx = list.FindIndex(a => a.Id == acct.Id);
                if (idx < 0) return;
                var item = list[idx];
                list.RemoveAt(idx);
                list.Insert(0, item);
                _app.SaveConfig();

                _closing = true;
                _trackTimer?.Stop();

                // 1) 关闭希沃窗口
                if (_seewoHwnd != IntPtr.Zero)
                    User32.SendMessage(_seewoHwnd, 0x0010, IntPtr.Zero, IntPtr.Zero);

                // 2) 刷新遮罩（基于希沃窗口矩形，不用遮罩自身坐标）
                double scale = GetDpi() / 96.0;
                User32.GetWindowRect(_seewoHwnd, out RECT winRect);
                int winW = winRect.Right - winRect.Left;
                int winH = winRect.Bottom - winRect.Top;
                var waitWnd = new Window
                {
                    WindowStyle = WindowStyle.None, AllowsTransparency = true,
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(200, 0, 0, 0)),
                    Topmost = true, ShowInTaskbar = false,
                    Width = (winW - 48) / scale, Height = (winH - 32) / scale,
                    Left = (winRect.Left + 24) / scale, Top = (winRect.Top + 16) / scale,
                    WindowStartupLocation = WindowStartupLocation.Manual
                };
                waitWnd.Content = new System.Windows.Controls.TextBlock
                {
                    Text = "正在刷新中···", FontSize = 16, Foreground = System.Windows.Media.Brushes.White,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center
                };
                waitWnd.Show();

                // 3) 关闭切换遮罩（已在 _closing=true + timer 中 base.Close）
                base.Close();

                // 4) 2s 后重新打开希沃（直接用 exe 启动，ShowWindow 对旧句柄不可靠）
                var tmr = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                tmr.Tick += (_, _) =>
                {
                    tmr.Stop();
                    waitWnd.Close();
                    try
                    {
                        string exePath = null;
                        foreach (var p in Process.GetProcessesByName("EasiNote"))
                        {
                            try { exePath = p.MainModule?.FileName; break; } catch { }
                        }
                        if (string.IsNullOrEmpty(exePath))
                        {
                            exePath = @"C:\Program Files\Seewo\EasiNote5\EasiNote.exe";
                        }
                        if (!System.IO.File.Exists(exePath))
                        {
                            // 尝试从注册表/常见路径找
                            var candidates = new[]
                            {
                                @"C:\Program Files\Seewo\EasiNote5\EasiNote.exe",
                                @"C:\Program Files (x86)\Seewo\EasiNote5\EasiNote.exe",
                                @"C:\Program Files\Seewo\EasiNote\EasiNote.exe"
                            };
                            exePath = candidates.FirstOrDefault(System.IO.File.Exists);
                        }
                        if (!string.IsNullOrEmpty(exePath) && System.IO.File.Exists(exePath))
                        {
                            // 直接启动；登录态检测走 SSO 网关请求，不再开放固定 CEF 调试端口
                            Process.Start(new ProcessStartInfo(exePath)
                            {
                                UseShellExecute = true
                            });
                        }
                    }
                    catch { }
                };
                tmr.Start();
            }
        }

        private static IntPtr FindEasiNote()
        {
            foreach (var p in Process.GetProcessesByName("EasiNote"))
                if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle;
            return IntPtr.Zero;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            _closing = true;
            _trackTimer?.Stop();
            _autoCloseTimer?.Stop();
            base.Close();
        }
        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { }
        private void OnClosedHandler(object sender, EventArgs e)
        {
            _closing = true;
            _trackTimer?.Stop();
            _autoCloseTimer?.Stop();
            // 不在关闭时 SetParent(IntPtr.Zero)，避免窗口跳跃
        }

        public class OverlayAccount
        {
            public string Id { get; set; }
            public string DisplayName { get; set; }
            public string MaskedContact { get; set; }
        }
    }

    internal static class User32
    {
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("gdi32.dll")] public static extern int GetDeviceCaps(IntPtr hdc, int nIndex);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;
    }
    internal struct RECT { public int Left, Top, Right, Bottom; }
}
