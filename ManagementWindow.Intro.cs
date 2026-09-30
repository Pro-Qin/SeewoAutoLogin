using Microsoft.Web.WebView2.Core;
using SeewoAutoLogin.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SeewoAutoLogin
{
    public partial class ManagementWindow
    {
        #region 开场动画

        /// <summary>
        /// 每个进程只在第一次打开主界面时播放：
        /// 标题+副标题渐显并自大缩小 → 向左渐隐 → 遮罩渐隐露出主界面。点击可跳过。
        /// </summary>
        private async Task PlayIntroIfNeededAsync()
        {
            if (_app == null || _app.IntroPlayed)
            {
                IntroOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            _app.IntroPlayed = true;
            IntroOverlay.Opacity = 1;
            IntroBox.Opacity = 0;
            IntroScale.ScaleX = 1.7;
            IntroScale.ScaleY = 1.7;
            IntroTranslate.X = 0;
            IntroOverlay.Visibility = Visibility.Visible;
            IntroOverlay.MouseLeftButtonDown += (s, e) => _skipIntro = true;

            var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
            var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };
            try
            {
                // 1. 渐显 + 从大缩小
                await Task.WhenAll(
                    AnimateAsync(IntroBox, UIElement.OpacityProperty, 0, 1, 650, easeOut),
                    AnimateAsync(IntroScale, ScaleTransform.ScaleXProperty, 1.7, 1, 650, easeOut),
                    AnimateAsync(IntroScale, ScaleTransform.ScaleYProperty, 1.7, 1, 650, easeOut));

                if (!_skipIntro) await Task.Delay(220);
                if (_skipIntro) { await EndIntroAsync(); return; }

                // 2. 向左渐隐
                await Task.WhenAll(
                    AnimateAsync(IntroBox, UIElement.OpacityProperty, 1, 0, 450, easeIn),
                    AnimateAsync(IntroTranslate, TranslateTransform.XProperty, 0, -180, 450, easeIn));

                // 3. 等界面就绪（最多再等 2.5s）后遮罩渐隐
                await Task.WhenAny(_contentReady.Task, Task.Delay(2500));
                await EndIntroAsync();
            }
            catch (Exception ex)
            {
                _app?.WriteDiagnosticLog($"[Intro] 开场动画异常: {ex.Message}");
                IntroOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private async Task EndIntroAsync()
        {
            try
            {
                await AnimateAsync(IntroOverlay, UIElement.OpacityProperty, 1, 0, 380,
                    new CubicEase { EasingMode = EasingMode.EaseOut });
            }
            catch
            {
            }
            IntroOverlay.Visibility = Visibility.Collapsed;
        }

        private static Task AnimateAsync(UIElement target, DependencyProperty property, double from, double to,
            double milliseconds, IEasingFunction easing)
            => AnimateCore(property, from, to, milliseconds, easing,
                (p, a) => target.BeginAnimation(p, a, HandoffBehavior.SnapshotAndReplace));

        private static Task AnimateAsync(Animatable target, DependencyProperty property, double from, double to,
            double milliseconds, IEasingFunction easing)
            => AnimateCore(property, from, to, milliseconds, easing,
                (p, a) => target.BeginAnimation(p, a, HandoffBehavior.SnapshotAndReplace));

        private static Task AnimateCore(DependencyProperty property, double from, double to,
            double milliseconds, IEasingFunction easing, Action<DependencyProperty, DoubleAnimation> apply)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var animation = new DoubleAnimation(from, to, new Duration(TimeSpan.FromMilliseconds(milliseconds)))
            {
                EasingFunction = easing ?? new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.HoldEnd
            };
            animation.Completed += (s, e) => completion.TrySetResult(true);
            apply(property, animation);
            return completion.Task;
        }

        #endregion
    }
}
