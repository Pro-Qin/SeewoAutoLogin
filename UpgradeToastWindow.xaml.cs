using System;
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
    /// 滑入 / 滑出**只做位移**（不做透明度渐变），停留期间轻微浮动，10 秒后自动滑出。
    /// 窗口不抢焦点、不进任务栏，避免打断用户正在做的事。
    /// </summary>
    public partial class UpgradeToastWindow : Window
    {
        private const int SlideInMs = 620;
        private const int SlideOutMs = 420;
        private const int AutoCloseMs = 10000;
        private const double SlideDistance = 392;
        private const double EdgeMargin = 24;

        /// <summary>当前屏幕上的提醒窗口：同一时刻只留一个，反复触发时不会叠成一片。</summary>
        private static UpgradeToastWindow _current;

        private readonly App.UpgradeNoticeData _data;
        private readonly DispatcherTimer _autoCloseTimer;
        private bool _leaving;
        private bool _changelogOpen;

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

            _autoCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AutoCloseMs) };
            _autoCloseTimer.Tick += (s, e) => Leave();

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
            StartCountdown();
            _autoCloseTimer.Start();
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

        /// <summary>性能模式：不播任何动效，窗口直接出现、直接消失。</summary>
        private static bool LiteMode
        {
            get
            {
                try { return (Application.Current as App)?.Config?.PerformanceMode == true; }
                catch { return false; }
            }
        }

        private void StartCountdown()
        {
            TimerScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation
            {
                From = 1,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(AutoCloseMs)
            });
        }

        private void OnOkClick(object sender, RoutedEventArgs e) => Leave();

        private void OnCloseClick(object sender, RoutedEventArgs e) => Leave();

        private void OnChangelogClick(object sender, RoutedEventArgs e)
        {
            _changelogOpen = !_changelogOpen;
            ChangelogHost.Visibility = _changelogOpen ? Visibility.Visible : Visibility.Collapsed;
            ChangelogButton.Content = _changelogOpen ? "收起" : "更新内容";
            // 展开后窗口高度变了，SizeChanged 会把卡片重新贴回右下角
        }

        /// <summary>滑出后关闭。只做位移，不做透明度渐变。</summary>
        private void Leave()
        {
            if (_leaving) return;
            _leaving = true;

            try { _autoCloseTimer.Stop(); } catch { }
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
    }
}
