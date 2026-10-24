using System.Windows;

namespace SeewoAutoLogin
{
    /// <summary>
    /// 界面文案的语言包。
    ///
    /// 文案放在 Resources/i18n/&lt;locale&gt;.json：一个键一行，改文案只改 JSON，不用翻代码。
    /// 前端通过 init 消息拿到整份字典（zh-CN 之外的语言缺失的键自动回退到 zh-CN），
    /// C# 侧用 <see cref="T(string)"/> 取值。找不到键时返回键名本身 —— 漏配一处只会显示
    /// 一个奇怪的键名，不会让界面空掉，也不会抛异常。
    ///
    /// 日志与异常消息**不在**语言包里：它们是给排查问题的人看的技术描述，
    /// 跟着代码走比跟着翻译走更可靠（也避免翻译把错误码、字段名改坏）。
    /// </summary>
    internal static class I18n
    {
        private const string FallbackLocale = "zh-CN";

        private static Dictionary<string, string> _current = new(StringComparer.Ordinal);
        private static Dictionary<string, string> _fallback = new(StringComparer.Ordinal);
        private static bool _initialized;

        /// <summary>当前语言，例如 zh-CN / en-US。</summary>
        public static string Locale { get; private set; } = FallbackLocale;

        /// <summary>当前语言的完整字典（含回退补齐的键），供前端一次取走。</summary>
        public static IReadOnlyDictionary<string, string> Current => _current;

        /// <summary>加载语言包。默认跟随 <see cref="Locale"/>，也可显式指定。</summary>
        public static void Initialize(string locale = null)
        {
            var wanted = string.IsNullOrWhiteSpace(locale) ? FallbackLocale : locale.Trim();
            _fallback = Load(FallbackLocale);
            if (wanted == FallbackLocale)
            {
                _current = _fallback;
            }
            else
            {
                var merged = new Dictionary<string, string>(_fallback, StringComparer.Ordinal);
                foreach (var pair in Load(wanted)) merged[pair.Key] = pair.Value;
                _current = merged;
            }

            Locale = wanted;
            _initialized = true;
        }

        /// <summary>切换语言。返回 false 表示这个语言没有语言包（保持原状）。</summary>
        public static bool SetLocale(string locale)
        {
            if (string.IsNullOrWhiteSpace(locale)) return false;
            if (locale.Trim() == Locale && _initialized) return true;
            if (Load(locale.Trim()).Count == 0) return false;
            Initialize(locale.Trim());
            return true;
        }

        /// <summary>取一条文案；键不存在时返回键名本身（便于发现漏配，且不会崩）。</summary>
        public static string T(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            if (!_initialized) Initialize();
            if (_current.TryGetValue(key, out var value)) return value;
            if (_fallback.TryGetValue(key, out var fallback)) return fallback;
            return key;
        }

        /// <summary>取一条带占位符的文案（string.Format 语义）。</summary>
        public static string T(string key, params object[] args)
        {
            var format = T(key);
            if (args == null || args.Length == 0) return format;
            try { return string.Format(format, args); }
            catch (FormatException) { return format; }
        }

        private static Dictionary<string, string> Load(string locale)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var suffix = $"i18n.{locale}.json";
                var name = assembly.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
                if (name == null)
                {
                    WriteDiagnostic("missing language pack: " + locale);
                    return map;
                }

                using var stream = assembly.GetManifestResourceStream(name);
                if (stream == null) return map;
                using var document = JsonDocument.Parse(stream);
                if (document.RootElement.ValueKind != JsonValueKind.Object) return map;

                foreach (var property in document.RootElement.EnumerateObject())
                {
                    // _meta 是对象（locale / name），跳过；其余一律按字符串读
                    if (property.Value.ValueKind != JsonValueKind.String) continue;
                    map[property.Name] = property.Value.GetString() ?? "";
                }
            }
            catch (Exception ex)
            {
                WriteDiagnostic($"load language pack failed: {locale}; {ex.GetType().Name} - {ex.Message}");
            }
            return map;
        }

        private static void WriteDiagnostic(string message)
        {
            try { (Application.Current as App)?.WriteDiagnosticLog("[I18n] " + message); } catch { }
        }
    }
}
