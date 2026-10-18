using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SeewoAutoLogin.Services
{
    /// <summary>诊断包导出结果</summary>
    internal sealed class DiagnosticBundleResult
    {
        public bool Ok { get; set; }

        /// <summary>用户在保存对话框里点了取消</summary>
        public bool Cancelled { get; set; }

        /// <summary>生成的 zip 路径</summary>
        public string Path { get; set; } = "";

        /// <summary>面向用户的提示文案</summary>
        public string Message { get; set; } = "";

        /// <summary>收集过程中被跳过的项目（不影响整体成功）</summary>
        public List<string> Warnings { get; } = new List<string>();
    }
}
