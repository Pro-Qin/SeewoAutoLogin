namespace SeewoAutoLogin.Services
{
    /// <summary>
    /// 待更新状态（<see cref="PluginConfig.PendingUpdateStage"/>）的取值。
    ///
    /// 用常量而不是各处裸写字符串，是因为这两条路径的语义**故意不同**，写错就会退化成
    /// 「安装—重启」死循环：
    ///   • <see cref="Downloaded"/> —— 包已下载并通过校验，但还没交给安装器，下次启动可以续装；
    ///   • <see cref="Installing"/> —— 安装器 / 替换脚本已经拉起，下次启动**不再自动重试**，
    ///     只提示用户手动完成（安装被安全软件拦下、安装目录被占用时，重试只会不断重启程序）。
    /// </summary>
    internal static class UpdateStages
    {
        public const string Downloaded = "downloaded";
        public const string Installing = "installing";
    }
}
