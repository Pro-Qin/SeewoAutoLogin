using Microsoft.Web.WebView2.Core;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SeewoAutoLogin
{
    public partial class ManagementWindow
    {
        #region Gateway Status

        private void UpdateGatewayStatus()
        {
            if (_app?.Gateway == null) return;
            _ = SendToJs(new { type = "gateway-status", running = _app.Gateway.IsRunning });
        }

        #endregion
    }
}
