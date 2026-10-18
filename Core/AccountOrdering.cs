namespace SeewoAutoLogin
{
    /// <summary>
    /// 账号排序与「生效区」规则的纯逻辑。
    ///
    /// 生效区 = 列表前 <see cref="PluginConfig.MaxVisibleAccounts"/> 个，希沃快捷登录窗口只展示这些。
    /// 这些规则原先散在 ManagementWindow 的四个命令处理器里，靠 UI 才能验证；
    /// 抽出来后可以直接对边界（第 1 个 / 第 N 个 / 已满 / 空列表）断言。
    /// </summary>
    internal static class AccountOrdering
    {
        /// <summary>
        /// 解析标签输入：兼容中英文逗号与分号，去空白、去重复（忽略大小写）。
        /// 永远返回非 null 的列表，调用方不需要再判空。
        /// </summary>
        public static List<string> ParseTags(string text) => (text ?? "")
            .Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        /// <summary>
        /// 把账号收进生效区（放在生效区最后一个位置）。
        /// 返回 false 表示无需变更（找不到该账号，或它已经在生效区里）。
        /// </summary>
        public static bool MoveToActive(List<SeewoAccount> accounts, string id)
        {
            if (accounts == null || string.IsNullOrEmpty(id)) return false;

            var index = accounts.FindIndex(a => a.Id == id);
            if (index < 0 || index < PluginConfig.MaxVisibleAccounts) return false;

            var account = accounts[index];
            accounts.RemoveAt(index);
            accounts.Insert(PluginConfig.MaxVisibleAccounts - 1, account);
            return true;
        }

        /// <summary>
        /// 把账号移出生效区（插到生效区边界之后）。
        /// 返回 false 表示无需变更（找不到该账号，或它本来就不在生效区）。
        /// </summary>
        public static bool MoveToInactive(List<SeewoAccount> accounts, string id)
        {
            if (accounts == null || string.IsNullOrEmpty(id)) return false;

            var index = accounts.FindIndex(a => a.Id == id);
            if (index < 0 || index >= PluginConfig.MaxVisibleAccounts) return false;

            var account = accounts[index];
            accounts.RemoveAt(index);
            // 插入到生效区边界，生效区减一，不自动补位
            accounts.Insert(Math.Min(PluginConfig.MaxVisibleAccounts, accounts.Count), account);
            return true;
        }

        /// <summary>
        /// 按拖动后的完整顺序重排：列表里出现过的 id 按给定顺序排，
        /// 没出现在这次拖动里的账号（被标签筛选隐藏的）保持原相对顺序接在后面。
        /// </summary>
        public static void ApplyOrder(List<SeewoAccount> accounts, IReadOnlyList<string> orderedIds)
        {
            if (accounts == null || accounts.Count == 0) return;
            if (orderedIds == null || orderedIds.Count == 0) return;

            var reordered = new List<SeewoAccount>(accounts.Count);
            foreach (var id in orderedIds)
            {
                if (string.IsNullOrEmpty(id)) continue;
                var account = accounts.FirstOrDefault(a => a.Id == id);
                if (account != null && !reordered.Contains(account)) reordered.Add(account);
            }

            foreach (var account in accounts)
                if (!reordered.Contains(account)) reordered.Add(account);

            accounts.Clear();
            accounts.AddRange(reordered);
        }
    }
}
