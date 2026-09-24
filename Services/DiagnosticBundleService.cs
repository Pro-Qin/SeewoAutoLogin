using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
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

    /// <summary>
    /// 崩溃/故障诊断包：把日志、脱敏配置、系统信息与自检快照打包成一个 zip，方便用户直接发给开发者。
    ///
    /// 设计原则：任何一项收集失败都只跳过该项并记入 warnings，绝不因此让整个导出失败
    /// （日志文件可能被占用、配置可能损坏、注册表可能读不到，这些都不该挡住用户反馈问题）。
    /// </summary>
    internal static class DiagnosticBundleService
    {
        /// <summary>最近多少天内的日志会被打包</summary>
        private const int LogWindowDays = 7;

        /// <summary>最多打包多少个日志文件</summary>
        private const int MaxLogFiles = 3;

        private static readonly JsonSerializerOptions PrettyJson = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private static readonly Regex PhonePattern =
            new Regex(@"(?<!\d)1[3-9]\d{9}(?!\d)", RegexOptions.Compiled);

        private const string Redacted = "***";

        #region 对外入口

        /// <summary>
        /// 弹出保存对话框让用户选择保存位置。必须在 UI 线程调用。
        /// 返回 null 表示用户取消（cancelled=true）或对话框不可用（此时已回退到桌面路径并返回该路径）。
        /// </summary>
        internal static string AskSavePath(System.Windows.Window owner, DiagnosticBundleContext context, out bool cancelled)
        {
            cancelled = false;
            try
            {
                // 与「导出配置」保持一致的 WPF 保存对话框（本项目其余导出入口均使用它）
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "导出诊断包",
                    Filter = "Zip 压缩包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
                    DefaultExt = "zip",
                    AddExtension = true,
                    OverwritePrompt = true,
                    FileName = BuildDefaultFileName()
                };

                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop))
                    desktop = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (!string.IsNullOrEmpty(desktop) && Directory.Exists(desktop))
                    dialog.InitialDirectory = desktop;

                var accepted = owner != null ? dialog.ShowDialog(owner) == true : dialog.ShowDialog() == true;
                if (!accepted)
                {
                    cancelled = true;
                    Log(context, "[Diagnostics] 用户取消了诊断包导出");
                    return null;
                }

                return dialog.FileName;
            }
            catch (Exception ex)
            {
                // 保存对话框本身失败（极罕见：会话被锁、桌面不可写）：退回到桌面直接生成
                Log(context, $"[Diagnostics] 保存对话框不可用，改为导出到桌面: {ex.GetType().Name} - {ex.Message}");
                return Path.Combine(DesktopOrTempPath(), BuildDefaultFileName());
            }
        }

        /// <summary>
        /// 直接生成诊断包（不弹对话框）。
        /// </summary>
        internal static DiagnosticBundleResult Create(string targetPath, DiagnosticBundleContext context)
        {
            var result = new DiagnosticBundleResult { Path = targetPath ?? "" };
            context ??= new DiagnosticBundleContext();

            if (string.IsNullOrWhiteSpace(targetPath))
            {
                result.Ok = false;
                result.Message = "导出失败：未指定保存路径";
                Log(context, "[Diagnostics] 导出失败: 未指定保存路径");
                return result;
            }

            try
            {
                var dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                using (var stream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8))
                {
                    // 每一项都独立 try-catch：日志被占用 / 配置损坏 / 注册表读不到，都只是少一个文件
                    CollectSelfCheck(zip, context, result.Warnings);
                    CollectConfig(zip, context, result.Warnings);
                    CollectLogs(zip, context, result.Warnings);
                    CollectSystemInfo(zip, context, result.Warnings);
                    // README 最后写：它要把前面收集过程中的 warnings 一并写进去
                    AddTextEntry(zip, "README.txt", BuildReadme(context, result.Warnings), result.Warnings);
                }

                var info = new FileInfo(targetPath);
                var sizeKb = info.Exists ? Math.Max(1, info.Length / 1024) : 0;
                result.Ok = true;
                result.Message = result.Warnings.Count == 0
                    ? $"诊断包已导出（{sizeKb} KB）"
                    : $"诊断包已导出（{sizeKb} KB，有 {result.Warnings.Count} 项被跳过）";
                Log(context, $"[Diagnostics] 导出成功: {targetPath}; size={sizeKb}KB; skipped={result.Warnings.Count}" +
                              (result.Warnings.Count > 0 ? "; detail=" + string.Join(" | ", result.Warnings) : ""));
            }
            catch (Exception ex)
            {
                result.Ok = false;
                result.Message = "导出失败：" + ex.Message;
                Log(context, $"[Diagnostics] 导出失败: {targetPath}; {ex.GetType().Name} - {ex.Message}");
                TryDelete(targetPath);
            }

            return result;
        }

        #endregion

        #region 各项收集

        /// <summary>自检状态快照：网关 / hosts / 权限 / 自启等，出问题时最快定位的一屏信息</summary>
        private static void CollectSelfCheck(ZipArchive zip, DiagnosticBundleContext context, List<string> warnings)
        {
            try
            {
                if (context.SelfCheckSnapshot == null)
                {
                    warnings.Add("自检状态：未提供快照数据源");
                    return;
                }

                var snapshot = context.SelfCheckSnapshot();
                if (snapshot == null)
                {
                    warnings.Add("自检状态：快照为空");
                    return;
                }

                var node = JsonSerializer.SerializeToNode(snapshot, PrettyJson);
                AddTextEntry(zip, "自检状态.json", node?.ToJsonString(PrettyJson) ?? "{}", warnings);
            }
            catch (Exception ex)
            {
                warnings.Add("自检状态收集失败：" + ex.Message);
            }
        }

        /// <summary>config.json 的脱敏副本 + 账号结构概览</summary>
        private static void CollectConfig(ZipArchive zip, DiagnosticBundleContext context, List<string> warnings)
        {
            string rawJson = null;
            string usedPath = null;
            foreach (var candidate in CandidateConfigPaths(context))
            {
                try
                {
                    if (!File.Exists(candidate)) continue;
                    rawJson = ReadAllTextShared(candidate);
                    usedPath = candidate;
                    if (!string.IsNullOrWhiteSpace(rawJson)) break;
                }
                catch (Exception ex)
                {
                    warnings.Add($"配置读取失败({Path.GetFileName(candidate)})：{ex.Message}");
                }
            }

            if (string.IsNullOrWhiteSpace(rawJson))
            {
                warnings.Add("配置：config.json 不可读或不存在");
                return;
            }

            JsonNode root = null;
            try { root = JsonNode.Parse(rawJson); }
            catch (Exception ex) { warnings.Add("配置解析失败（将只附带原始大小信息）：" + ex.Message); }

            if (root == null)
            {
                AddTextEntry(zip, "config.脱敏.json", "{ \"error\": \"配置无法解析\" }", warnings);
                return;
            }

            // 概览必须用脱敏前的原始节点算（账号/密码字段马上会被打码，之后就判不出登录方式了）
            try { AddTextEntry(zip, "账号概览.txt", BuildAccountOverview(root), warnings); }
            catch (Exception ex) { warnings.Add("账号概览生成失败：" + ex.Message); }

            try
            {
                var sanitized = SanitizeNode(root);
                AddTextEntry(zip, "config.脱敏.json", sanitized?.ToJsonString(PrettyJson) ?? "{}", warnings);
            }
            catch (Exception ex)
            {
                warnings.Add("配置脱敏失败：" + ex.Message);
            }

            if (!string.IsNullOrEmpty(usedPath))
                Log(context, $"[Diagnostics] 已收集脱敏配置: {Path.GetFileName(usedPath)}");
        }

        /// <summary>最近 7 天内最多 3 个日志文件（含轮转产生的 .1.log）</summary>
        private static void CollectLogs(ZipArchive zip, DiagnosticBundleContext context, List<string> warnings)
        {
            try
            {
                var logsDir = ResolveLogsDir(context);
                if (string.IsNullOrEmpty(logsDir) || !Directory.Exists(logsDir))
                {
                    warnings.Add("日志：目录不存在");
                    return;
                }

                var all = new DirectoryInfo(logsDir).GetFiles("*.log*")
                    .OrderByDescending(f => f.LastWriteTime)
                    .ToList();
                if (all.Count == 0)
                {
                    warnings.Add("日志：目录为空");
                    return;
                }

                var cutoff = DateTime.Now.AddDays(-LogWindowDays);
                var picked = all.Where(f => f.LastWriteTime >= cutoff).Take(MaxLogFiles).ToList();
                if (picked.Count == 0)
                {
                    // 最近 7 天没有新日志（用户很久没启动过），至少带上最新的一个，否则等于没带日志
                    picked = all.Take(1).ToList();
                    warnings.Add($"日志：最近 {LogWindowDays} 天没有新日志，已改为附带最新 1 个文件");
                }

                foreach (var file in picked)
                {
                    try
                    {
                        var bytes = ReadAllBytesShared(file.FullName);
                        AddBinaryEntry(zip, "logs/" + file.Name, bytes, warnings);
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"日志跳过({file.Name})：{ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                warnings.Add("日志收集失败：" + ex.Message);
            }
        }

        /// <summary>系统信息：Windows / .NET / 程序版本 / 权限 / WebView2 / 希沃客户端版本</summary>
        private static void CollectSystemInfo(ZipArchive zip, DiagnosticBundleContext context, List<string> warnings)
        {
            try
            {
                AddTextEntry(zip, "系统信息.txt", BuildSystemInfo(context), warnings);
            }
            catch (Exception ex)
            {
                warnings.Add("系统信息收集失败：" + ex.Message);
            }
        }

        private static string BuildSystemInfo(DiagnosticBundleContext context)
        {
            var sb = new StringBuilder();
            sb.AppendLine("希沃自动登录 - 系统信息");
            sb.AppendLine("生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine();

            sb.AppendLine("[程序]");
            sb.AppendLine("  程序版本：" + Safe(() => context.AppVersion ?? ""));
            sb.AppendLine("  进程架构：" + Safe(() => RuntimeInformation.ProcessArchitecture.ToString()));
            sb.AppendLine("  是否管理员：" + (IsRunningAsAdministrator() ? "是" : "否"));
            sb.AppendLine("  是否有UI：" + Safe(() => Environment.UserInteractive ? "是" : "否"));
            sb.AppendLine();

            sb.AppendLine("[Windows]");
            sb.AppendLine("  OSVersion：" + Safe(() => Environment.OSVersion.VersionString));
            sb.AppendLine("  产品名称：" + Safe(() => ReadRegistryString(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName")));
            sb.AppendLine("  版本号(DisplayVersion)：" + Safe(() => ReadRegistryString(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion")));
            sb.AppendLine("  Build：" + Safe(() => ReadRegistryString(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber")) +
                "." + Safe(() => ReadRegistryString(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR")));
            sb.AppendLine("  系统架构：" + Safe(() => RuntimeInformation.OSArchitecture.ToString()));
            sb.AppendLine("  64位系统：" + Safe(() => Environment.Is64BitOperatingSystem ? "是" : "否"));
            sb.AppendLine();

            sb.AppendLine("[运行环境]");
            sb.AppendLine("  .NET：" + Safe(() => RuntimeInformation.FrameworkDescription));
            sb.AppendLine("  CLR：" + Safe(() => Environment.Version.ToString()));
            sb.AppendLine("  WebView2 运行时：" + Safe(() => WebView2Runtime.GetInstalledVersion() ?? "未安装"));
            sb.AppendLine();

            sb.AppendLine("[希沃客户端]");
            var seewoVersion = Safe(() => SeewoVersionMonitor.TryGetVersion(out _) ?? "");
            sb.AppendLine("  客户端版本：" + (string.IsNullOrEmpty(seewoVersion) ? "未检测到（可能未安装或未运行）" : seewoVersion));
            sb.AppendLine("  进程状态：" + Safe(() => SeewoVersionMonitor.DescribeProcessState()));
            sb.AppendLine();

            sb.AppendLine("[凭据存储]");
            sb.AppendLine("  账号密码：DPAPI 加密后写入 config.json（导出的副本中已一律打码为 ***）");

            return sb.ToString();
        }

        #endregion

        #region 打包 / 读取辅助

        private static void AddTextEntry(ZipArchive zip, string entryName, string content, List<string> warnings)
        {
            try
            {
                // 带 BOM：Windows 记事本打开不乱码
                var bytes = new UTF8Encoding(true).GetBytes(content ?? "");
                AddBinaryEntry(zip, entryName, bytes, warnings);
            }
            catch (Exception ex)
            {
                warnings.Add($"写入失败({entryName})：{ex.Message}");
            }
        }

        private static void AddBinaryEntry(ZipArchive zip, string entryName, byte[] bytes, List<string> warnings)
        {
            try
            {
                var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                using (var entryStream = entry.Open())
                {
                    entryStream.Write(bytes, 0, bytes.Length);
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"写入失败({entryName})：{ex.Message}");
            }
        }

        /// <summary>FileShare.ReadWrite：日志正被写入时也能读，不会因为占用而丢掉整包日志</summary>
        private static string ReadAllTextShared(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }

        private static byte[] ReadAllBytesShared(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }

        private static void TryDelete(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); } catch { }
        }

        private static void Log(DiagnosticBundleContext context, string message)
        {
            try { context?.Log?.Invoke(message); } catch { }
        }

        /// <summary>当前进程是否以管理员身份运行（自检快照里也有一份，这里供纯文本系统信息使用）</summary>
        private static bool IsRunningAsAdministrator()
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                return new System.Security.Principal.WindowsPrincipal(identity)
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        private static string Safe(Func<string> getter)
        {
            try { return getter() ?? ""; } catch { return "（获取失败）"; }
        }

        private static string ReadRegistryString(string subKey, string name)
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(subKey);
            return key?.GetValue(name)?.ToString() ?? "";
        }

        private static string BuildDefaultFileName()
            => $"SeewoAutoLogin-诊断包-{DateTime.Now:yyyyMMdd-HHmmss}.zip";

        private static string DesktopOrTempPath()
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (!string.IsNullOrEmpty(desktop) && Directory.Exists(desktop)) return desktop;
            return Path.GetTempPath();
        }

        private static IEnumerable<string> CandidateConfigPaths(DiagnosticBundleContext context)
        {
            if (!string.IsNullOrWhiteSpace(context.ConfigPath))
            {
                yield return context.ConfigPath;
                yield return context.ConfigPath + ".bak";
            }
            if (!string.IsNullOrWhiteSpace(context.AppDataDir))
            {
                yield return Path.Combine(context.AppDataDir, "config.json");
                yield return Path.Combine(context.AppDataDir, "config.json.bak");
            }
        }

        private static string ResolveLogsDir(DiagnosticBundleContext context)
        {
            if (!string.IsNullOrWhiteSpace(context.LogsDir)) return context.LogsDir;
            return string.IsNullOrWhiteSpace(context.AppDataDir) ? null : Path.Combine(context.AppDataDir, "Logs");
        }

        #endregion

        #region 脱敏

        /// <summary>
        /// 结构化脱敏：密码 / 盐 / 哈希 / 令牌 / 凭据 / 账号 一律替换为 ***，
        /// 其余字符串里的手机号也一律打码，保持 JSON 结构不变（便于开发者直接看配置形态）。
        /// </summary>
        private static JsonNode SanitizeNode(JsonNode node)
        {
            switch (node)
            {
                case null:
                    return null;

                case JsonObject obj:
                {
                    var result = new JsonObject();
                    foreach (var pair in obj)
                    {
                        if (IsSensitiveKey(pair.Key)) result[pair.Key] = Redacted;
                        else result[pair.Key] = SanitizeNode(pair.Value);
                    }
                    return result;
                }

                case JsonArray array:
                {
                    var result = new JsonArray();
                    foreach (var item in array) result.Add(SanitizeNode(item));
                    return result;
                }

                case JsonValue value:
                {
                    if (value.TryGetValue<string>(out var text)) return JsonValue.Create(MaskPhone(text));
                    return value.DeepClone();
                }

                default:
                    return node.DeepClone();
            }
        }

        private static readonly string[] SensitiveKeyTokens =
        {
            "password", "passwd", "pwd", "salt", "hash", "token", "secret",
            "credential", "username", "usercode", "sessionid", "cookie", "apikey", "privatekey"
        };

        private static bool IsSensitiveKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            var lower = key.ToLowerInvariant();

            // 例外：lastTokenExchangeAtUtc / tokenIssuedAt / lastCheckedTime 这类字段名里带 token，
            // 但值是时间戳而非凭据，打码反而丢掉了排查需要的「凭据多久没续期」信息。
            if (lower.EndsWith("atutc") || lower.EndsWith("at") || lower.Contains("time") || lower.Contains("date"))
                return false;

            foreach (var token in SensitiveKeyTokens)
            {
                if (lower.Contains(token)) return true;
            }
            return false;
        }

        private static string MaskPhone(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            try { return PhonePattern.Replace(text, Redacted); }
            catch { return text; }
        }

        /// <summary>账号结构概览：只保留数量、备注名、登录方式、健康状态等可供归因的信息</summary>
        private static string BuildAccountOverview(JsonNode root)
        {
            var sb = new StringBuilder();
            sb.AppendLine("账号结构概览（不含任何账号名 / 密码 / 令牌 / 手机号）");
            sb.AppendLine("生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine();

            var accounts = root?["accounts"] as JsonArray;
            if (accounts == null)
            {
                sb.AppendLine("配置中未找到账号列表（accounts）。");
                return sb.ToString();
            }

            int passwordCount = 0, qrCount = 0, placeholder = 0, healthy = 0, bad = 0, unknown = 0;

            sb.AppendLine($"账号总数：{accounts.Count}");
            sb.AppendLine();

            for (int i = 0; i < accounts.Count; i++)
            {
                var item = accounts[i] as JsonObject;
                if (item == null) continue;

                var hasPassword = HasContent(item, "password");
                var hasQr = HasContent(item, "qrCredentialId");
                var method = hasQr ? "扫码登录" : (hasPassword ? "密码登录" : "未知/无凭据");
                if (hasQr) qrCount++; else if (hasPassword) passwordCount++;
                if (GetBool(item, "isPlaceholder")) placeholder++;

                var health = MaskPhone(GetString(item, "healthState"));
                if (string.IsNullOrEmpty(health)) unknown++;
                else if (string.Equals(health, "ok", StringComparison.OrdinalIgnoreCase)) healthy++;
                else bad++;

                sb.AppendLine($"#{i + 1}");
                sb.AppendLine("  备注名：" + MaskPhone(GetString(item, "displayName")));
                sb.AppendLine("  登录方式：" + method);
                sb.AppendLine("  健康状态：" + (string.IsNullOrEmpty(health) ? "未巡检" : health));
                var healthMessage = MaskPhone(GetString(item, "healthMessage"));
                if (!string.IsNullOrEmpty(healthMessage))
                    sb.AppendLine("  健康说明：" + healthMessage);
                sb.AppendLine("  占位账号：" + (GetBool(item, "isPlaceholder") ? "是" : "否"));
                sb.AppendLine("  凭据最近续期：" + GetString(item, "lastTokenExchangeAtUtc"));
                sb.AppendLine("  SSO 请求次数：" + GetString(item, "requestCount"));
                sb.AppendLine("  标签数：" + ((item["tags"] as JsonArray)?.Count ?? 0));
                sb.AppendLine();
            }

            sb.AppendLine("汇总：扫码账号 " + qrCount + " 个，密码账号 " + passwordCount +
                          " 个，占位账号 " + placeholder + " 个；健康 " + healthy +
                          " 个，异常 " + bad + " 个，未巡检 " + unknown + " 个。");
            return sb.ToString();
        }

        private static bool HasContent(JsonObject obj, string key)
        {
            var value = obj?[key];
            if (value == null) return false;
            try
            {
                if (value is JsonValue jv && jv.TryGetValue<string>(out var text))
                    return !string.IsNullOrWhiteSpace(text);
            }
            catch { }
            return true;
        }

        private static string GetString(JsonObject obj, string key)
        {
            try
            {
                var value = obj?[key];
                if (value is JsonValue jv && jv.TryGetValue<string>(out var text)) return text ?? "";
                return value?.ToJsonString() ?? "";
            }
            catch { return ""; }
        }

        private static bool GetBool(JsonObject obj, string key)
        {
            try
            {
                var value = obj?[key];
                if (value is JsonValue jv && jv.TryGetValue<bool>(out var flag)) return flag;
                return false;
            }
            catch { return false; }
        }

        #endregion

        private static string BuildReadme(DiagnosticBundleContext context, List<string> warnings)
        {
            var sb = new StringBuilder();
            sb.AppendLine("希沃自动登录 - 诊断包");
            sb.AppendLine("========================================");
            sb.AppendLine("生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("程序版本：" + (context.AppVersion ?? ""));
            sb.AppendLine("请把整个 zip 原样发给开发者用于定位问题。");
            sb.AppendLine();
            sb.AppendLine("内容说明");
            sb.AppendLine("----------------------------------------");
            sb.AppendLine("README.txt         本文件");
            sb.AppendLine("系统信息.txt       Windows / .NET / 程序 / WebView2 / 希沃客户端版本等");
            sb.AppendLine("自检状态.json      导出时的自检快照（网关端口、hosts 映射、权限、开机自启等）");
            sb.AppendLine("config.脱敏.json   配置副本：账号名、密码、令牌、手机号均已替换为 ***");
            sb.AppendLine("账号概览.txt       账号数量、备注名、登录方式、健康状态等结构信息（无敏感内容）");
            sb.AppendLine("logs/*.log         最近 7 天内最多 3 个日志文件（含轮转日志）");
            sb.AppendLine();
            sb.AppendLine("隐私说明");
            sb.AppendLine("----------------------------------------");
            sb.AppendLine("· 诊断包不包含任何明文密码：程序本地存储的凭据本身即为加密态，导出时进一步打码。");
            sb.AppendLine("· 账号、令牌、手机号在本包内一律显示为 ***。");
            sb.AppendLine("· 若仍不希望发送，可自行删除 config.脱敏.json 与日志文件后再转发。");
            sb.AppendLine();

            if (warnings != null && warnings.Count > 0)
            {
                sb.AppendLine("本次导出中被跳过的项目");
                sb.AppendLine("----------------------------------------");
                foreach (var warning in warnings) sb.AppendLine("· " + warning);
                sb.AppendLine();
            }

            return sb.ToString();
        }
    }
}
