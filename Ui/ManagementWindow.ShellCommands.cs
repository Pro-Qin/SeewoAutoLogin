using System.Windows;

namespace SeewoAutoLogin
{
    public partial class ManagementWindow
    {
        /// </summary>
        private void HandleOpenExternal(JsonElement root)
        {
            var url = root.Str("url", null);
            if (string.IsNullOrWhiteSpace(url)) return;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps
                || !TrustedExternalHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            {
                _app.WriteDiagnosticLog($"[External] 已拦截不可信的外链请求: {url}");
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo { FileName = uri.ToString(), UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _app.WriteDiagnosticLog($"[External] 打开链接失败: {ex.Message}");
            }
        }

        /// <summary>把用户协议正文下发给前端（「关于」页点击《用户协议》时请求，与欢迎界面共用同一份资源）</summary>
        private async Task SendTerms()
        {
            var text = App.LoadTermsText();
            if (string.IsNullOrWhiteSpace(text))
                _app.WriteDiagnosticLog("[Terms] 协议正文载入失败（嵌入资源缺失？）");
            await SendToJs(new { type = "terms", text });
        }

        /// <summary>打开本地文件或受信任的网页；不可信地址一律回落到官方发布页</summary>
        private void OpenExternal(string target, bool alreadyValidated = false)
        {
            try
            {
                if (File.Exists(target))
                {
                    Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
                    return;
                }

                var url = (alreadyValidated || UpdateChecker.IsTrustedDownloadUrl(target))
                    ? target
                    : UpdateChecker.ReleasesPageUrl;
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _app.WriteDiagnosticLog($"[Update] 打开失败: {ex.Message}");
            }
        }
    }
}
