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
        #region C# → JS

        private Task SendToJs(object msg)
        {
            if (!_webViewReady) return Task.CompletedTask;
            try
            {
                WebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(msg));
            }
            catch { }
            return Task.CompletedTask;
        }

        #endregion
    }
}
