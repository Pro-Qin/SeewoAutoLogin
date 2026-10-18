namespace SeewoAutoLogin
{
    /// <summary>
    /// CSV 导入的纯解析层：编码探测、RFC4180 解析、表头识别、明细截断。
    ///
    /// 这些逻辑原先埋在 ManagementWindow 的界面消息处理器里，既没法单独测，
    /// 也没法被「欢迎界面导入」这类其它入口复用。规则本身与界面无关，因此抽到这里。
    /// 解析过程中的任何异常都由调用方决定如何呈现，本类不写日志、不碰 UI。
    /// </summary>
    internal static class CsvParser
    {
        /// <summary>失败明细最多回传这么多条，避免上千行的文件把消息体撑爆（前端只展示前 5 条）。</summary>
        public const int MaxMessages = 20;

        /// <summary>
        /// 读 CSV 文本：按 BOM 判定 UTF-8 / UTF-16，无 BOM 时先按 UTF-8 严格解码，
        /// 失败再回退 GBK —— Excel「另存为 CSV」在中文 Windows 上默认就是 GBK。
        /// </summary>
        public static string ReadText(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0) return "";

            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

            try
            {
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                try
                {
                    // GBK(936) 属于代码页编码，.NET Core 需要先注册提供程序
                    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                    return Encoding.GetEncoding(936).GetString(bytes);
                }
                catch
                {
                    return Encoding.UTF8.GetString(bytes);
                }
            }
        }

        /// <summary>
        /// RFC4180 风格 CSV 解析：支持引号包裹的字段、字段内逗号与换行、双写引号（""）转义，
        /// 兼容 CRLF / LF / CR 换行。不丢空行，由调用方决定跳过。
        /// </summary>
        public static List<List<string>> Parse(string text)
        {
            var rows = new List<List<string>>();
            if (string.IsNullOrEmpty(text)) return rows;
            if (text[0] == '\uFEFF') text = text.Substring(1);

            var row = new List<string>();
            var field = new StringBuilder();
            var inQuotes = false;

            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];

                if (inQuotes)
                {
                    if (ch == '"')
                    {
                        // 引号内的 "" 表示一个字面引号
                        if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                        else inQuotes = false;
                    }
                    else field.Append(ch);
                    continue;
                }

                if (ch == '"' && field.Length == 0) { inQuotes = true; continue; }

                if (ch == ',')
                {
                    row.Add(field.ToString());
                    field.Clear();
                    continue;
                }

                if (ch == '\r' || ch == '\n')
                {
                    if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add(row);
                    row = new List<string>();
                    continue;
                }

                field.Append(ch);
            }

            // 文件末尾没有换行时补上最后一行；末尾正好有换行则不会多出一行空记录
            if (field.Length > 0 || row.Count > 0)
            {
                row.Add(field.ToString());
                rows.Add(row);
            }
            return rows;
        }

        /// <summary>首列可能出现的表头字样（账号,密码,备注 这类列名）。</summary>
        private static readonly string[] HeaderUserColumns =
        {
            "账号", "帐号", "用户名", "用户", "登录名", "手机号", "手机", "姓名",
            "user", "username", "account", "login", "mobile", "phone"
        };

        /// <summary>首列是「序号/no/id」这类占位列时，再看第二列是不是账号列。</summary>
        private static readonly string[] HeaderIndexColumns = { "序号", "编号", "no", "id", "#", "index" };

        /// <summary>
        /// 首行是否为表头。只做精确匹配（而不是 Contains），否则「13800138000」这种
        /// 真实账号会被当成表头整行丢掉。
        /// </summary>
        public static bool IsHeaderRow(IReadOnlyList<string> cells)
        {
            if (cells == null || cells.Count == 0) return false;
            var first = cells[0].Trim().ToLowerInvariant();
            if (first.Length == 0) return false;
            if (HeaderUserColumns.Contains(first)) return true;
            if (HeaderIndexColumns.Contains(first) && cells.Count > 1)
                return HeaderUserColumns.Contains(cells[1].Trim().ToLowerInvariant());
            return false;
        }

        /// <summary>往明细列表里追加一条，超过上限后只留一条汇总提示。</summary>
        public static void AddMessage(List<string> messages, string message)
        {
            if (messages.Count < MaxMessages) messages.Add(message);
            else if (messages.Count == MaxMessages) messages.Add("（失败明细过多，其余条目已省略）");
        }
    }
}
