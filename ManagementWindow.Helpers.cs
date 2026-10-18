namespace SeewoAutoLogin
{
    public partial class ManagementWindow
    {
        /// <summary>
        /// 统一下发轻提示（右下角 toast）。
        ///
        /// 前端只认 level（ok / warn / error / info）来决定配色，文案全部由 C# 给。
        /// 各命令处理器原先各自写 <c>SendToJs(new { type = "toast", ... })</c>，
        /// 字段名写错一处就变成「什么都不弹」，收到这里只剩一个出口。
        /// </summary>
        private Task SendToast(string text, string level)
            => SendToJs(new { type = "toast", text, level });

        /// <summary>
        /// 执行一条前端命令。
        ///
        /// 界面消息是「发完就不管」的：调用方（WebView2 消息循环）不会 await。
        /// 以前这类命令写成 <c>async void</c>，方法体里抛出的异常直接跳到
        /// DispatcherUnhandledException —— 轻则一条日志看不出是哪个操作，重则整个进程被带走。
        /// 这里把「不 await」收成一个出口：异常按命令名记进诊断日志，取消当作正常结束。
        /// </summary>
        private void RunCommand(Func<Task> operation, string command)
        {
            _ = RunCommandAsync(operation, command);
        }

        private async Task RunCommandAsync(Func<Task> operation, string command)
        {
            try
            {
                await operation();
            }
            catch (OperationCanceledException)
            {
                // 用户连点、切页导致的操作取消：不是故障
            }
            catch (Exception ex)
            {
                _app?.WriteDiagnosticLog($"[UI] 命令 {command} 执行失败: {ex.GetType().Name} - {ex.Message}");
                await SendToast("操作失败：" + ex.Message, "error");
            }
        }
    }
}
