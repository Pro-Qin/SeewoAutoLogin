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
        #region Gateway Status

        private void UpdateGatewayStatus()
        {
            if (_app?.Gateway == null) return;
            _ = SendToJs(new { type = "gateway-status", running = _app.Gateway.IsRunning });
        }

        #endregion
    }
}
