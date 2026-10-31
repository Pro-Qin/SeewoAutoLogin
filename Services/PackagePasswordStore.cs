namespace SeewoAutoLogin.Services
{
    /// <summary>
    /// 配置包口令的本机保存。
    ///
    /// 配置包本身不存口令（那等于把锁和钥匙放在一起），但让老师每次导出/导入都手打一遍也很烦。
    /// 折中：口令用 DPAPI 加密后存在本机数据目录，只对当前 Windows 账户可解；
    /// 换台电脑导入时解不开，自然会提示重新输入 —— 这正是迁移场景该有的行为。
    /// </summary>
    internal static class PackagePasswordStore
    {
        private static string StorePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SeewoAutoLogin", "package.key");

        public static string Get()
        {
            try
            {
                if (!File.Exists(StorePath)) return "";
                var encrypted = File.ReadAllText(StorePath).Trim();
                if (encrypted.Length == 0) return "";
                return SecureStore.Decrypt(encrypted) ?? "";
            }
            catch
            {
                return "";
            }
        }

        public static void Set(string password)
        {
            try
            {
                var dir = Path.GetDirectoryName(StorePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                if (string.IsNullOrWhiteSpace(password))
                {
                    if (File.Exists(StorePath)) File.Delete(StorePath);
                    return;
                }
                File.WriteAllText(StorePath, SecureStore.Encrypt(password));
            }
            catch
            {
                // 存不下就算了：下次导出会再问一次，不影响功能
            }
        }
    }
}
