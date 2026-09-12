using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SeewoAutoLogin.Services
{
    /// <summary>
    /// hosts 文件读写（local.id.seewo.com → 127.0.0.1）。
    /// 安全要点：写入前备份、只删除本程序写入的行、按原编码写回（Latin1 往返保证其它行字节不变）、原子替换。
    /// </summary>
    internal static class HostsFileService
    {
        internal const string HostName = "local.id.seewo.com";
        internal const string LoopbackIp = "127.0.0.1";
        /// <summary>本程序写入行的标记：卸载时只删除带该标记的行，绝不误删用户/其它工具的条目</summary>
        internal const string Marker = "# SeewoAutoLogin";

        private static readonly object Gate = new object();

        internal enum MappingState { Active, Missing, Commented, WrongIp, Unreadable }

        internal static string HostsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

        internal static string BackupPath => HostsPath + ".seewoautologin.bak";

        internal static bool HasLoopbackMapping()
        {
            var state = Inspect();
            return state == MappingState.Active;
        }

        /// <summary>检查当前映射状态（不修改文件）</summary>
        internal static MappingState Inspect()
        {
            try
            {
                if (!File.Exists(HostsPath)) return MappingState.Unreadable;
                foreach (var raw in ReadLines())
                {
                    var line = raw.Trim();
                    if (line.Length == 0) continue;
                    var isComment = line.StartsWith("#");
                    var effective = isComment ? line : StripComment(line);
                    var tokens = effective.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    var hasHost = tokens.Any(t => string.Equals(t, HostName, StringComparison.OrdinalIgnoreCase));
                    if (!hasHost)
                    {
                        if (isComment && line.IndexOf(HostName, StringComparison.OrdinalIgnoreCase) >= 0)
                            return MappingState.Commented;
                        continue;
                    }
                    if (isComment) return MappingState.Commented;
                    if (tokens.Length >= 2 && tokens[0] == LoopbackIp) return MappingState.Active;
                    return MappingState.WrongIp;
                }
                return MappingState.Missing;
            }
            catch { return MappingState.Unreadable; }
        }

        internal static string DescribeState()
        {
            switch (Inspect())
            {
                case MappingState.Active: return "已映射到 127.0.0.1";
                case MappingState.Missing: return "缺少映射";
                case MappingState.Commented: return "映射被注释掉";
                case MappingState.WrongIp: return "指向了其它 IP";
                default: return "hosts 无法读取";
            }
        }

        /// <summary>
        /// 确保 hosts 存在 127.0.0.1 local.id.seewo.com 的有效映射（带本程序标记）。
        /// 会自动备份原文件，并尽量保持原编码。
        /// </summary>
        internal static bool EnsureLoopbackMapping(out string error)
        {
            error = null;
            lock (Gate)
            {
                try
                {
                    if (!File.Exists(HostsPath))
                    {
                        error = "hosts 文件不存在";
                        return false;
                    }

                    var (text, encoding) = Decode(File.ReadAllBytes(HostsPath));
                    var lines = SplitLines(text).ToList();
                    var changed = false;

                    for (int i = 0; i < lines.Count; i++)
                    {
                        var line = lines[i].Trim();
                        if (line.Length == 0 || line.StartsWith("#")) continue;
                        var tokens = StripComment(line).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (tokens.Length >= 2 && tokens[0] == LoopbackIp &&
                            tokens.Skip(1).Any(t => string.Equals(t, HostName, StringComparison.OrdinalIgnoreCase)))
                        {
                            // 已有有效映射：补上标记，便于卸载时精确清理
                            if (lines[i].IndexOf(Marker, StringComparison.Ordinal) < 0)
                            {
                                lines[i] = LoopbackIp + " " + HostName + " " + Marker;
                                changed = true;
                            }
                            if (changed) WriteAtomic(lines, encoding);
                            return true;
                        }
                    }

                    BackupOnce();
                    if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length > 0) lines.Add("");
                    lines.Add(LoopbackIp + " " + HostName + " " + Marker);
                    WriteAtomic(lines, encoding);

                    if (!HasLoopbackMapping())
                    {
                        error = "写入后校验失败";
                        return false;
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
            }
        }

        /// <summary>
        /// 移除本程序写入的映射行：只删除带标记的行，或内容恰好是 127.0.0.1 + 本程序主机名的旧版本遗留行。
        /// </summary>
        internal static bool RemoveLoopbackMapping(out string error)
        {
            error = null;
            lock (Gate)
            {
                try
                {
                    if (!File.Exists(HostsPath)) return true;
                    var (text, encoding) = Decode(File.ReadAllBytes(HostsPath));
                    var lines = SplitLines(text).ToList();
                    var removed = lines.RemoveAll(raw =>
                    {
                        var line = raw.Trim();
                        if (line.Length == 0) return false;
                        if (line.IndexOf(Marker, StringComparison.Ordinal) >= 0 &&
                            line.IndexOf(HostName, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                        if (line.StartsWith("#")) return false;
                        var tokens = StripComment(line).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        return tokens.Length == 2 && tokens[0] == LoopbackIp &&
                               string.Equals(tokens[1], HostName, StringComparison.OrdinalIgnoreCase);
                    });
                    if (removed == 0) return true;
                    WriteAtomic(lines, encoding);
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
            }
        }

        private static IEnumerable<string> ReadLines()
        {
            var (text, _) = Decode(File.ReadAllBytes(HostsPath));
            return SplitLines(text);
        }

        private static IEnumerable<string> SplitLines(string text)
            => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        private static string StripComment(string line)
        {
            var idx = line.IndexOf('#');
            return idx >= 0 ? line.Substring(0, idx) : line;
        }

        /// <summary>写入前保留一份原始备份（只保留最早的一份，避免把已污染的内容覆盖掉）</summary>
        private static void BackupOnce()
        {
            try
            {
                if (File.Exists(BackupPath)) return;
                File.Copy(HostsPath, BackupPath, overwrite: false);
            }
            catch { }
        }

        private static void WriteAtomic(List<string> lines, Encoding encoding)
        {
            var content = string.Join(Environment.NewLine, lines);
            if (!content.EndsWith(Environment.NewLine, StringComparison.Ordinal)) content += Environment.NewLine;

            var dir = Path.GetDirectoryName(HostsPath);
            var temp = Path.Combine(dir, "hosts.seewoautologin.tmp");
            File.WriteAllText(temp, content, encoding);
            try
            {
                if (File.Exists(HostsPath))
                    File.Replace(temp, HostsPath, null, ignoreMetadataErrors: true);
                else
                    File.Move(temp, HostsPath);
            }
            catch
            {
                // File.Replace 在个别环境（权限/文件系统）会失败，回退为直接写入
                File.Copy(temp, HostsPath, overwrite: true);
                try { File.Delete(temp); } catch { }
            }
        }

        /// <summary>按原编码解码；无法判定时用 Latin1（字节往返无损，保证其它行不被改写）</summary>
        private static (string text, Encoding encoding) Decode(byte[] bytes)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), new UTF8Encoding(true));
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), new UnicodeEncoding(false, true));
            try
            {
                var strict = new UTF8Encoding(false, true);
                return (strict.GetString(bytes), new UTF8Encoding(false));
            }
            catch
            {
                return (Encoding.Latin1.GetString(bytes), Encoding.Latin1);
            }
        }
    }
}
