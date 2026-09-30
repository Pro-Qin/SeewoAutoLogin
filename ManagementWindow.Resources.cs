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
        #region Embedded Resource

        private string GetEmbedded(string name)
        {
            var asm = Assembly.GetExecutingAssembly();
            var full = asm.GetManifestResourceNames().FirstOrDefault(n => n == name || n.EndsWith(name));
            if (full == null) throw new InvalidOperationException($"Embedded resource not found: {name}");
            using var s = asm.GetManifestResourceStream(full);
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }

        /// <summary>兜底：本地资源释放失败时，仍用 NavigateToString 内联 HTML/CSS/JS。</summary>
        private string BuildInlineHtml()
        {
            var html = GetEmbedded("SeewoAutoLogin.frontend.index.html");
            var css = GetEmbedded("SeewoAutoLogin.frontend.styles.css");
            var js = GetEmbedded("SeewoAutoLogin.frontend.app.js");
            var full = html.Replace("</head>", "<style>" + css + "</style></head>");
            return full.Replace("</body>", "<script>" + js + "</script></body>");
        }

        #endregion
    }
}
