namespace SeewoAutoLogin.Ui
{
    /// <summary>
    /// 前端消息参数的读取工具。
    ///
    /// 原先每个处理器都写 <c>root.GetProperty("x").GetString()</c>：键缺失抛
    /// <see cref="KeyNotFoundException"/>、类型不符抛 <see cref="InvalidOperationException"/>，
    /// 异常会被消息循环的 catch 吞掉转成一行日志 —— 用户看到的是「点了没反应」，
    /// 日志里只有一句「消息处理异常」，看不出到底是哪个字段。
    ///
    /// 这里统一成「缺失即取默认值」：前端少发一个字段最多让这次操作什么都不做，
    /// 而不是让整条消息炸掉。确实必须存在的字段由调用方自己判空后提前返回。
    /// </summary>
    internal static class JsonArgs
    {
        /// <summary>读字符串字段；缺失、null 或非字符串/数字一律返回 fallback。</summary>
        public static string Str(this JsonElement element, string property, string fallback = "")
        {
            if (element.ValueKind != JsonValueKind.Object) return fallback;
            if (!element.TryGetProperty(property, out var value)) return fallback;
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? fallback,
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => fallback
            };
        }

        /// <summary>读布尔字段；只认真正的 true，其余（含缺失）返回 fallback。</summary>
        public static bool Flag(this JsonElement element, string property, bool fallback = false)
        {
            if (element.ValueKind != JsonValueKind.Object) return fallback;
            if (!element.TryGetProperty(property, out var value)) return fallback;
            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => fallback
            };
        }

        /// <summary>读整数字段；缺失、非数字返回 fallback。</summary>
        public static int Int(this JsonElement element, string property, int fallback = 0)
        {
            if (element.ValueKind != JsonValueKind.Object) return fallback;
            if (!element.TryGetProperty(property, out var value)) return fallback;
            return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : fallback;
        }

        /// <summary>读对象/数组字段；缺失时返回 Undefined，调用方按 <see cref="JsonElement.ValueKind"/> 判断。</summary>
        public static JsonElement Node(this JsonElement element, string property)
            => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
                ? value
                : default;

        /// <summary>读字符串数组字段；缺失或类型不符返回空列表（不返回 null，调用方无需再判空）。</summary>
        public static List<string> Strings(this JsonElement element, string property)
        {
            var node = element.Node(property);
            if (node.ValueKind != JsonValueKind.Array) return new List<string>();

            var list = new List<string>();
            foreach (var item in node.EnumerateArray())
            {
                var text = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                if (!string.IsNullOrWhiteSpace(text)) list.Add(text);
            }
            return list;
        }
    }
}
