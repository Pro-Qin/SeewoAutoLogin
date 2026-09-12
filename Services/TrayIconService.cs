using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SeewoAutoLogin.Services
{
    public sealed class TrayIconService : IDisposable
    {
        private NotifyIcon _notifyIcon;
        private readonly Action _showWindowAction;
        private Icon _cachedIcon;
        private bool _disposed;
        private ContextMenuStrip _menu;
        private readonly Func<List<AccountMenuItem>> _getAccounts;
        private readonly Action<string> _onSwitchAccount;
        private List<string> _visibleIds = new List<string>();

        /// <param name="showWindowAction">打开管理窗口</param>
        /// <param name="getAccounts">获取所有账号菜单项</param>
        /// <param name="onSwitchAccount">切换账号（参数为账号 ID）</param>
        public TrayIconService(Action showWindowAction,
                               Func<List<AccountMenuItem>> getAccounts = null,
                               Action<string> onSwitchAccount = null)
        {
            _showWindowAction = showWindowAction;
            _getAccounts = getAccounts ?? (() => new List<AccountMenuItem>());
            _onSwitchAccount = onSwitchAccount;
        }

        public void Initialize()
        {
            _cachedIcon = LoadAppIcon() ?? CreateFallbackIcon();

            _menu = new ContextMenuStrip();
            _menu.Font = new Font("Segoe UI", 9);

            // 打开主界面
            var openItem = _menu.Items.Add("打开管理窗口");
            openItem.Click += (s, e) => _showWindowAction?.Invoke();

            _notifyIcon = new NotifyIcon
            {
                Icon = _cachedIcon,
                Text = Strings.AppTitle,
                Visible = true,
                ContextMenuStrip = _menu
            };

            _notifyIcon.Click += (s, e) =>
            {
                // 左键打开
                if (e is MouseEventArgs me && me.Button == MouseButtons.Left)
                    _showWindowAction?.Invoke();
            };
            _notifyIcon.DoubleClick += (s, e) => _showWindowAction?.Invoke();

            // 初始构建菜单
            RebuildMenu();
        }

        public void UpdateVisibleAccounts(List<string> visibleIds)
        {
            _visibleIds = visibleIds ?? new List<string>();
            RebuildMenu();
        }

        /// <summary>
        /// 托盘菜单是 WinForms 控件：SSO 网关在线程池线程上触发账号刷新时，
        /// 必须先切回创建菜单的 UI 线程，否则会出现随机异常或菜单句柄损坏。
        /// 返回 false 表示已排队到 UI 线程（调用方应直接返回）。
        /// </summary>
        private bool EnsureUiThread()
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) return true;
            try { dispatcher.BeginInvoke(new Action(RebuildMenu)); } catch { }
            return false;
        }

        private void RebuildMenu()
        {
            if (_notifyIcon == null || _menu == null) return;
            if (!EnsureUiThread()) return;

            // RemoveAt 只从集合移除、不释放控件：每次 SSO 请求都会重建菜单，不 Dispose 会持续泄漏 GDI/事件委托
            while (_menu.Items.Count > 1)
            {
                var removed = _menu.Items[1];
                _menu.Items.RemoveAt(1);
                try { removed?.Dispose(); } catch { }
            }

            var accounts = _getAccounts() ?? new List<AccountMenuItem>();

            // 只显示未生效账号（生效区内的不显示）
            var unlisted = accounts.Skip(PluginConfig.MaxVisibleAccounts).ToList();

            if (unlisted.Count > 0)
            {
                _menu.Items.Add(new ToolStripSeparator());
                var h = _menu.Items.Add("— 未加入希沃列表（点击切换） —");
                h.Enabled = false;
                foreach (var a in unlisted)
                {
                    var cnt = a.RequestCount > 0 ? $" ({a.RequestCount}次)" : "";
                    var item = _menu.Items.Add($"{a.DisplayName}{cnt}");
                    var cid = a.Id;
                    item.Click += (s, e) => _onSwitchAccount?.Invoke(cid);
                }
            }

            // 下面这几个入口与有没有账号无关，必须始终存在（否则空账号时连退出都点不到）
            _menu.Items.Add(new ToolStripSeparator());
            var overlay = _menu.Items.Add("显示遮罩");
            overlay.Click += (s, e) => _onSwitchAccount?.Invoke("__OVERLAY__");
            var restart = _menu.Items.Add("重启程序");
            restart.Click += (s, e) => _onSwitchAccount?.Invoke("__RESTART__");
            var exit = _menu.Items.Add("退出程序");
            exit.Click += (s, e) => _onSwitchAccount?.Invoke("__EXIT__");
        }

        public void UpdateTrayText(string text)
        {
            if (_notifyIcon != null)
                _notifyIcon.Text = text;
        }

        public void SetStatusText(string statusMessage)
        {
            if (_notifyIcon != null)
            {
                _notifyIcon.BalloonTipTitle = Strings.AppTitle;
                _notifyIcon.BalloonTipText = statusMessage;
                _notifyIcon.ShowBalloonTip(3000);
            }
        }

        /// <summary>
        /// 从嵌入的应用图标（多尺寸 ico）中按系统 DPI 选出最合适的一帧，托盘显示更清晰。
        /// 取不到时回退到运行时绘制。
        /// </summary>
        private static Icon LoadAppIcon()
        {
            try
            {
                var assembly = typeof(TrayIconService).Assembly;
                var name = assembly.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("app.ico", StringComparison.OrdinalIgnoreCase));
                if (name == null) return null;
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream == null) return null;

                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                buffer.Position = 0;

                var size = SystemInformation.SmallIconSize;
                return new Icon(buffer, new Size(size.Width, size.Height));
            }
            catch
            {
                return null;
            }
        }

        private static Icon CreateFallbackIcon()
        {
            using var image = new Bitmap(16, 16);
            using var g = Graphics.FromImage(image);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

            // 蓝色渐变背景圆角方形
            var rect = new Rectangle(0, 0, 16, 16);
            using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
                rect, Color.FromArgb(0, 122, 255), Color.FromArgb(0, 60, 180),
                System.Drawing.Drawing2D.LinearGradientMode.ForwardDiagonal))
            {
                g.FillEllipse(brush, new Rectangle(1, 1, 14, 14));
            }

            // 白色 "S" 字母
            using (var font = new Font("Segoe UI", 7, FontStyle.Bold))
            {
                var size = g.MeasureString("S", font);
                g.DrawString("S", font, Brushes.White,
                    (16 - size.Width) / 2f + 0.5f,
                    (16 - size.Height) / 2f - 0.5f);
            }

            var hicon = image.GetHicon();
            try
            {
                using (var tempIcon = Icon.FromHandle(hicon))
                using (var ms = new MemoryStream())
                {
                    tempIcon.Save(ms);
                    ms.Position = 0;
                    return new Icon(ms);
                }
            }
            finally
            {
                if (hicon != IntPtr.Zero)
                    DestroyIcon(hicon);
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }
            _cachedIcon?.Dispose();
            _menu?.Dispose();
        }
    }

    public class AccountMenuItem
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
        public int RequestCount { get; set; }
        public DateTime? LastRequestAtUtc { get; set; }
    }
}
