using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SeewoAutoLogin.Services
{
    /// <summary>
    /// 诊断包内容来源。由 App / 主界面填充，服务本身不反向依赖 App 的内部成员，
    /// 便于单独调用与测试。
    /// </summary>
    internal sealed class DiagnosticBundleContext
    {
        /// <summary>%LOCALAPPDATA%\SeewoAutoLogin</summary>
        public string AppDataDir { get; set; }

        /// <summary>日志目录（默认 AppDataDir\Logs）</summary>
        public string LogsDir { get; set; }

        /// <summary>config.json 路径</summary>
        public string ConfigPath { get; set; }

        /// <summary>程序版本号，例如 1.11.3</summary>
        public string AppVersion { get; set; }

        /// <summary>自检状态快照（App.BuildSelfCheckStatus 的返回值），可为 null</summary>
        public Func<object> SelfCheckSnapshot { get; set; }

        /// <summary>写日志（App.WriteDiagnosticLog），可为 null</summary>
        public Action<string> Log { get; set; }
    }
}
