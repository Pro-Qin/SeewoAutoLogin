using System.Windows;

namespace SeewoAutoLogin.Ui
{
    /// <summary>
    /// 主界面里「要用户点头」的对话框。
    ///
    /// 原先每个处理器直接调 <c>MessageBox.Show(...)</c>，同一个语义（确认 / 告知）在四个文件里
    /// 各写一遍，按钮与图标组合还各不相同。收到这里之后：
    ///   · 有主界面时一律以主界面为 owner（不会飘到屏幕角落、也不会被主界面遮住）；
    ///   · 「危险操作」（删除账号、覆盖配置、恢复出厂）统一走 Warn 配色；
    ///   · 后台路径仍然不用这些方法 —— 按 AGENT.md，课堂场景不许弹模态框打断老师。
    /// </summary>
    internal static class Dialogs
    {
        /// <summary>普通确认。返回 true 表示用户点了「是」。</summary>
        public static bool Confirm(Window owner, string title, string message)
            => Ask(owner, title, message, MessageBoxImage.Question);

        /// <summary>危险操作确认（删除 / 覆盖 / 恢复出厂）：用警告图标，默认焦点不在「是」上。</summary>
        public static bool ConfirmDanger(Window owner, string title, string message)
            => Ask(owner, title, message, MessageBoxImage.Warning);

        /// <summary>只告知，不需要选择。</summary>
        public static void Inform(Window owner, string title, string message)
        {
            if (owner != null) MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
            else MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private static bool Ask(Window owner, string title, string message, MessageBoxImage icon)
        {
            var result = owner != null
                ? MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, icon, MessageBoxResult.No)
                : MessageBox.Show(message, title, MessageBoxButton.YesNo, icon, MessageBoxResult.No);
            return result == MessageBoxResult.Yes;
        }
    }
}
