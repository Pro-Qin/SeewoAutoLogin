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

            // 调用方可能是后台保活的 Timer 线程（账号状态变化后刷新列表）。
            // PostWebMessageAsJson 只能在创建 WebView2 的那条 UI 线程上调用，
            // 跨线程会直接抛异常，而异常被吞掉之后的表现是「界面静默不刷新」。
            if (!Dispatcher.CheckAccess())
            {
                try { return Dispatcher.InvokeAsync(() => PostToWebView(msg)).Task; }
                catch { return Task.CompletedTask; }
            }

            PostToWebView(msg);
            return Task.CompletedTask;
        }

        private void PostToWebView(object msg)
        {
            try
            {
                WebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(msg));
            }
            catch { }
        }

        #endregion
    }
}
