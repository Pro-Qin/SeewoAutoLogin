using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SeewoAutoLogin
{
    /// <summary>
    /// 桌面级「升级完成」提醒：托盘常驻、主界面没有打开的机器上，用它把结果说出来。
    ///
    /// 与主界面内的提醒同源（同一份数据）、同一套视觉与动效约定：
    /// 滑入 / 滑出**只做位移**（不做透明度渐变），停留期间轻微浮动。
    /// 窗口不抢焦点、不进任务栏，避免打断用户正在做的事。
    ///
    /// 停留时长不是固定的：弹出后先一直留着，检测到用户有键鼠输入之后才开始 15 秒倒计时。
    /// 升级多半发生在人不在电脑前的时候，定时收走等于白提醒。
    /// </summary>
    public partial class UpgradeToastWindow : Window
    {
        private const int SlideInMs = 620;
        private const int SlideOutMs = 420;

        /// <summary>检测到用户输入之后，再留这么久让他读完，然后自动滑出。</summary>
        private const int AutoCloseAfterInputMs = 15000;

        /// <summary>输入检测的轮询间隔。用系统级「最后输入时间」而不是窗口的键鼠事件：
        /// 这个窗口不抢焦点，收不到全局键盘，鼠标也可能根本不在它上面。</summary>
        private const int InputPollMs = 400;

        /// <summary>弹窗那一刻系统空闲不到这么久，就认为「人本来就在电脑前」，直接进倒计时。</summary>
        private const uint PresentUserIdleMs = 1500;

        private const double SlideDistance = 392;
        private const double EdgeMargin = 24;

        /// <summary>当前屏幕上的提醒窗口：同一时刻只留一个，反复触发时不会叠成一片。</summary>
        private static UpgradeToastWindow _current;

        private readonly App.UpgradeNoticeData _data;
        private readonly DispatcherTimer _autoCloseTimer;
        private readonly DispatcherTimer _inputWatchTimer;
        private bool _leaving;
        private bool _changelogOpen;

        /// <summary>弹出瞬间的系统空闲时长，用来判断之后用户是否又动过键鼠。</summary>
        private uint _baselineIdleMs;
        private LastInputInfo _lastInput;

        [StructLayout(LayoutKind.Sequential)]
        private struct LastInputInfo
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LastInputInfo plii);

        internal UpgradeToastWindow(App.UpgradeNoticeData data)
        {
            InitializeComponent();
            _data = data ?? new App.UpgradeNoticeData();

            var from = UpdateChecker_Normalize(_data.From);
            var to = UpdateChecker_Normalize(_data.To);

            // 没有起始版本记录时不编造对比：只显示当前版本
            if (string.IsNullOrEmpty(from))
            {
                FromChip.Visibility = Visibility.Collapsed;
                ArrowText.Visibility = Visibility.Collapsed;
            }
            else
            {
                FromText.Text = "v" + from;
            }
            ToText.Text = "v" + to;

            var at = _data.AtUtc;
            if (at.Year < 2000) at = DateTimeOffset.Now;
            TimeText.Text = at.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + " 更新";

            BuildChangelog();

            _autoCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AutoCloseAfterInputMs) };
            _autoCloseTimer.Tick += (s, e) => Leave();

            _inputWatchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(InputPollMs) };
            _inputWatchTimer.Tick += OnInputWatchTick;

            _lastInput = new LastInputInfo { cbSize = (uint)Marshal.SizeOf(typeof(LastInputInfo)) };

            // 展开更新内容会改变窗口高度：保持底边贴住工作区底部，别让卡片“往上跳”
            SizeChanged += (s, e) => { if (IsLoaded) RepositionBottom(); };
            Loaded += OnLoaded;
        }

        /// <summary>弹出提醒（替换掉已在屏幕上的那个）。</summary>
        internal static void ShowNotice(App.UpgradeNoticeData data)
        {
            try
            {
                _current?.CloseNow();

                var window = new UpgradeToastWindow(data);
                _current = window;
                window.Closed += (s, e) =>
                {
                    if (ReferenceEquals(_current, window)) _current = null;
                };
                window.Show();
            }
            catch
            {
                // 提醒失败不该影响程序本身
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;
            RepositionBottom();
            AnimateIn();
            WaitForUserInput();
        }

        private void AnimateIn()
        {
            if (LiteMode)
            {
                // 性能模式：不做滑入，直接出现在最终位置
                SlideX.BeginAnimation(TranslateTransform.XProperty, null);
                SlideX.X = 0;
                return;
            }

            SlideX.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation
            {
                From = SlideDistance,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(SlideInMs),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

            // 停留期间的轻微浮动：作用在另一层 transform 上，不和滑入抢
            FloatY.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
            {
                From = 0,
                To = -4,
                Duration = TimeSpan.FromMilliseconds(2200),
                BeginTime = TimeSpan.FromMilliseconds(900),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });
        }

        /// <summary>
        /// 弹出后先一直留着，直到用户有键鼠输入再开始 15 秒倒计时。
        ///
        /// 两种情形分开处理：弹窗那一刻人本来就在电脑前（刚有输入）→ 直接倒计时；
        /// 人不在（系统已空闲一会儿）→ 等他回来动一下键鼠再倒计时。
        /// </summary>
        private void WaitForUserInput()
        {
            if (_leaving) return;

            // 倒计时没开始，进度条先不显示，免得让人以为马上就要走了
            try { _autoCloseTimer.Stop(); } catch { }
            TimerScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            TimerScale.ScaleX = 1;
            TimerBarHost.Visibility = Visibility.Collapsed;

            _baselineIdleMs = GetIdleMilliseconds();

            if (_baselineIdleMs < PresentUserIdleMs)
            {
                Log("[Update] 桌面提醒：弹窗时用户就在电脑前，立即开始 15 秒倒计时");
                BeginAutoClose();
                return;
            }

            Log($"[Update] 桌面提醒已显示（系统已空闲 {_baselineIdleMs / 1000.0:F1} 秒），等待用户输入后再开始倒计时");
            _inputWatchTimer.Start();
        }

        private void OnInputWatchTick(object sender, EventArgs e)
        {
            try
            {
                if (_leaving || _changelogOpen)
                {
                    _inputWatchTimer.Stop();
                    return;
                }

                var idle = GetIdleMilliseconds();

                // 空闲时长比弹窗那一刻更短 = 用户又动过键鼠（键盘、鼠标、触摸都算）
                if (idle >= _baselineIdleMs) return;

                _inputWatchTimer.Stop();
                Log($"[Update] 检测到用户输入（系统空闲回到 {idle} 毫秒），开始 15 秒倒计时关闭桌面提醒");
                BeginAutoClose();
            }
            catch
            {
                // 检测失败就退回「不自动关闭」，宁可多留一会儿
                try { _inputWatchTimer.Stop(); } catch { }
            }
        }

        /// <summary>距系统最后一次键鼠输入的毫秒数。</summary>
        private uint GetIdleMilliseconds()
        {
            try
            {
                if (!GetLastInputInfo(ref _lastInput)) return uint.MaxValue;
                // GetLastInputInfo 的 dwTime 与 Environment.TickCount 同源（GetTickCount），
                // 相减用无符号语义，回绕也能得到正确差值。
                return unchecked((uint)(Environment.TickCount - (int)_lastInput.dwTime));
            }
            catch
            {
                return uint.MaxValue;
            }
        }

        /// <summary>开始 15 秒倒计时：进度条走满 + 到点滑出。</summary>
        private void BeginAutoClose()
        {
            try { _inputWatchTimer.Stop(); } catch { }

            TimerBarHost.Visibility = Visibility.Visible;
            TimerScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation
            {
                From = 1,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(AutoCloseAfterInputMs)
            });

            _autoCloseTimer.Stop();
            _autoCloseTimer.Start();
        }

        /// <summary>性能模式：不播任何动效，窗口直接出现、直接消失。</summary>
        private static bool LiteMode
        {
            get
            {
                try { return (Application.Current as App)?.Config?.PerformanceMode == true; }
                catch { return false; }
            }
        }

        private void OnOkClick(object sender, RoutedEventArgs e) => Leave();

        private void OnCloseClick(object sender, RoutedEventArgs e) => Leave();

        private void OnChangelogClick(object sender, RoutedEventArgs e)
        {
            _changelogOpen = !_changelogOpen;
            ChangelogHost.Visibility = _changelogOpen ? Visibility.Visible : Visibility.Collapsed;
            ChangelogButton.Content = _changelogOpen ? "收起" : "更新内容";
            // 展开后窗口高度变了，SizeChanged 会把卡片重新贴回右下角

            if (_changelogOpen)
            {
                // 用户正在读更新内容：既不自动关闭，也不再检测输入
                StopAutoClose();
            }
            else
            {
                // 收起本身就是一次输入，直接进倒计时
                BeginAutoClose();
            }
        }

        /// <summary>停掉一切自动关闭（倒计时与输入检测），窗口留给用户自己关。</summary>
        private void StopAutoClose()
        {
            try { _autoCloseTimer.Stop(); } catch { }
            try { _inputWatchTimer.Stop(); } catch { }
            TimerScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            TimerScale.ScaleX = 1;
            TimerBarHost.Visibility = Visibility.Collapsed;
        }

        /// <summary>滑出后关闭。只做位移，不做透明度渐变。</summary>
        private void Leave()
        {
            if (_leaving) return;
            _leaving = true;

            try { _autoCloseTimer.Stop(); } catch { }
            try { _inputWatchTimer.Stop(); } catch { }
            // 停掉循环浮动，避免和滑出动画同时改 transform
            FloatY.BeginAnimation(TranslateTransform.YProperty, null);

            // 性能模式：直接消失，不播滑出
            if (LiteMode)
            {
                CloseNow();
                return;
            }

            // 起点取「当前正在显示的位置」，否则会从基准值突然跳一下再滑出
            var from = (double)SlideX.GetValue(TranslateTransform.XProperty);
            var animation = new DoubleAnimation
            {
                From = from,
                To = SlideDistance,
                Duration = TimeSpan.FromMilliseconds(SlideOutMs),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            animation.Completed += (s, e) => CloseNow();
            SlideX.BeginAnimation(TranslateTransform.XProperty, animation);
        }

        private void CloseNow()
        {
            try { _autoCloseTimer?.Stop(); } catch { }
            try { _inputWatchTimer?.Stop(); } catch { }
            try { Close(); } catch { }
        }

        /// <summary>贴住工作区右下角。用工作区而不是整屏，避免被任务栏压住。</summary>
        private void RepositionBottom()
        {
            try
            {
                UpdateLayout();
                var work = SystemParameters.WorkArea;
                Left = work.Right - ActualWidth - EdgeMargin;
                Top = work.Bottom - ActualHeight - EdgeMargin;
            }
            catch
            {
            }
        }

        /// <summary>把更新内容渲染成条目列表（发布说明是「- xxx」形式的多行文本）。</summary>
        private void BuildChangelog()
        {
            ChangelogItems.Children.Clear();

            var notes = (_data.Notes ?? "").Trim();
            if (notes.Length == 0)
            {
                ChangelogItems.Children.Add(NewText("这一版没有记录更新内容。", "#66FFFFFF", 12));
                return;
            }

            foreach (var raw in notes.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;

                var isBullet = line.StartsWith("- ", StringComparison.Ordinal)
                               || line.StartsWith("* ", StringComparison.Ordinal)
                               || line.StartsWith("· ", StringComparison.Ordinal);

                if (!isBullet)
                {
                    ChangelogItems.Children.Add(NewText(line, "#FFFFFF", 12.5, bold: true, topMargin: 8));
                    continue;
                }

                var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var dot = new System.Windows.Shapes.Ellipse
                {
                    Width = 5,
                    Height = 5,
                    Fill = BrushOf("#00C880"),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(3, 7, 0, 0)
                };
                Grid.SetColumn(dot, 0);
                row.Children.Add(dot);

                var label = NewText(line.Substring(2).Trim(), "#C9C9CE", 12);
                Grid.SetColumn(label, 1);
                row.Children.Add(label);

                ChangelogItems.Children.Add(row);
            }
        }

        private static TextBlock NewText(string text, string color, double size, bool bold = false, double topMargin = 0)
            => new TextBlock
            {
                Text = text,
                Foreground = BrushOf(color),
                FontSize = size,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = size * 1.75,
                Margin = new Thickness(0, topMargin, 0, 0)
            };

        private static Brush BrushOf(string hex)
            => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

        private static string UpdateChecker_Normalize(string version)
        {
            try { return Services.UpdateChecker.NormalizeVersion(version); }
            catch { return version ?? ""; }
        }

        private static void Log(string message)
        {
            try { (Application.Current as App)?.WriteDiagnosticLog(message); } catch { }
        }
    }
}
