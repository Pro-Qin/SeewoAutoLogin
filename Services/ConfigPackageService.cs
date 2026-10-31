using System.Security.Cryptography;

namespace SeewoAutoLogin.Services
{
    /// <summary>
    /// 可迁移的配置包（.swalcfg）。
    ///
    /// 与旧的「导出配置（明文 JSON）/ 导出账号（JSON 信封）」不同，这里只有一种形态：
    ///   · **整个文件是二进制密文**，记事本打开只有乱码；
    ///   · 账号（含手机号）与设置一起加密，文件名与文件头都不出现任何明文；
    ///   · AES-256-GCM（带认证标签，改一个字节就解不开）+ PBKDF2-SHA256 20 万次派生；
    ///   · 没有「不加密」的选项 —— 想导出就必须有口令。
    ///
    /// 文件结构（小端）：
    ///   0..7    魔数 "SWALCFG1"
    ///   8..11   版本 uint32
    ///   12..27  salt (16B)
    ///   28..39  nonce (12B)
    ///   40..55  tag (16B)
    ///   56..    密文（UTF-8 JSON 载荷）
    /// </summary>
    internal static class ConfigPackageService
    {
        public const string FileExtension = ".swalcfg";
        public const string FileFilter = "SeewoAutoLogin 配置文件 (*.swalcfg)|*.swalcfg";

        private const string MagicText = "SWALCFG1";
        private const int Version = 1;
        private const int SaltBytes = 16;
        private const int NonceBytes = 12;
        private const int TagBytes = 16;
        private const int Iterations = 200_000;
        private const int MinPasswordLength = 6;

        private static readonly byte[] Magic = Encoding.ASCII.GetBytes(MagicText);

        // ---------- 载荷 ----------

        internal sealed class PackageAccount
        {
            public string DisplayName { get; set; } = "";
            public string Username { get; set; } = "";
            public string Password { get; set; } = "";
            public string Tags { get; set; } = "";
        }

        internal sealed class PackagePayload
        {
            public string Kind { get; set; } = "SeewoAutoLogin.ConfigPackage";
            public int Version { get; set; } = 1;
            public string CreatedAtUtc { get; set; } = "";
            public string AppVersion { get; set; } = "";
            public List<PackageAccount> Accounts { get; set; } = new List<PackageAccount>();
            /// <summary>设置项：键 → 值（bool 存 true/false，整数存十进制字符串）。</summary>
            public Dictionary<string, string> Settings { get; set; } = new Dictionary<string, string>();
        }

        // ---------- 设置项目录 ----------

        internal sealed class SettingDescriptor
        {
            public string Key { get; init; } = "";
            public string Label { get; init; } = "";
        }

        /// <summary>可随配置包迁移的设置项。凭据、路径、更新状态一律不进包。</summary>
        internal static readonly SettingDescriptor[] SettingCatalog =
        {
            new() { Key = "startMinimized", Label = "启动时不显示主界面" },
            new() { Key = "minimizeToTray", Label = "关闭时最小化到托盘" },
            new() { Key = "autoStartEnabled", Label = "开机自启" },
            new() { Key = "restoreHostsOnExit", Label = "退出时恢复 hosts 映射" },
            new() { Key = "autoRepairEnabled", Label = "自动修复" },
            new() { Key = "autoShowOverlay", Label = "希沃未登录时自动显示切换遮罩" },
            new() { Key = "particleBackground", Label = "粒子背景" },
            new() { Key = "performanceMode", Label = "性能模式" },
            new() { Key = "autoCheckUpdate", Label = "启动时自动检查更新" },
            new() { Key = "autoInstallAfterDownload", Label = "后台静默更新" },
            new() { Key = "userListRotationEnabled", Label = "用户列表轮换" },
            new() { Key = "userListRotationGroupSize", Label = "每组用户数" }
        };

        internal static Dictionary<string, string> CaptureSettings(PluginConfig config) => new(StringComparer.Ordinal)
        {
            ["startMinimized"] = config.StartMinimized ? "true" : "false",
            ["minimizeToTray"] = config.MinimizeToTray ? "true" : "false",
            ["autoStartEnabled"] = config.AutoStartEnabled ? "true" : "false",
            ["restoreHostsOnExit"] = config.RestoreHostsOnExit ? "true" : "false",
            ["autoRepairEnabled"] = config.AutoRepairEnabled ? "true" : "false",
            ["autoShowOverlay"] = config.AutoShowOverlay ? "true" : "false",
            ["particleBackground"] = config.ParticleBackground ? "true" : "false",
            ["performanceMode"] = config.PerformanceMode ? "true" : "false",
            ["autoCheckUpdate"] = config.AutoCheckUpdate ? "true" : "false",
            ["autoInstallAfterDownload"] = config.AutoInstallAfterDownload ? "true" : "false",
            ["userListRotationEnabled"] = config.UserListRotationEnabled ? "true" : "false",
            ["userListRotationGroupSize"] = config.UserListRotationGroupSize.ToString()
        };

        internal static void ApplySetting(PluginConfig config, string key, string value)
        {
            var flag = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
            switch (key)
            {
                case "startMinimized": config.StartMinimized = flag; break;
                case "minimizeToTray": config.MinimizeToTray = flag; break;
                case "autoStartEnabled": config.AutoStartEnabled = flag; break;
                case "restoreHostsOnExit": config.RestoreHostsOnExit = flag; break;
                case "autoRepairEnabled": config.AutoRepairEnabled = flag; break;
                case "autoShowOverlay": config.AutoShowOverlay = flag; break;
                case "particleBackground": config.ParticleBackground = flag; break;
                case "performanceMode": config.PerformanceMode = flag; break;
                case "autoCheckUpdate": config.AutoCheckUpdate = flag; break;
                case "autoInstallAfterDownload": config.AutoInstallAfterDownload = flag; break;
                case "userListRotationEnabled": config.UserListRotationEnabled = flag; break;
                case "userListRotationGroupSize":
                    if (int.TryParse(value, out var size))
                        config.UserListRotationGroupSize = SeewoUserListRotationService.NormalizeGroupSize(size);
                    break;
            }
        }

        // ---------- 导出 ----------

        public static bool Export(string path, PluginConfig config, string appVersion, string password, out string error)
        {
            error = null;
            try
            {
                if (string.IsNullOrWhiteSpace(password) || password.Length < MinPasswordLength)
                {
                    error = $"配置包口令至少 {MinPasswordLength} 位";
                    return false;
                }

                var payload = new PackagePayload
                {
                    CreatedAtUtc = DateTimeOffset.UtcNow.ToString("u"),
                    AppVersion = appVersion ?? "",
                    Settings = CaptureSettings(config)
                };

                foreach (var account in config.Accounts)
                {
                    if (account == null || account.IsPlaceholder) continue;
                    var plainPassword = account.DecryptedPassword;
                    if (!string.IsNullOrEmpty(account.Password) && string.IsNullOrEmpty(plainPassword))
                    {
                        error = $"账号「{account.DisplayName ?? account.Username}」的密码无法解密，已中止导出";
                        return false;
                    }
                    payload.Accounts.Add(new PackageAccount
                    {
                        DisplayName = account.DisplayName ?? "",
                        Username = account.Username ?? "",
                        Password = plainPassword ?? "",
                        Tags = account.Tags == null || account.Tags.Count == 0 ? "" : string.Join(",", account.Tags)
                    });
                }

                var plain = JsonSerializer.SerializeToUtf8Bytes(payload);
                var salt = RandomNumberGenerator.GetBytes(SaltBytes);
                var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
                var key = DeriveKey(password, salt);
                var cipher = new byte[plain.Length];
                var tag = new byte[TagBytes];
                using (var gcm = new AesGcm(key, TagBytes))
                    gcm.Encrypt(nonce, plain, cipher, tag);

                using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(salt);
                writer.Write(nonce);
                writer.Write(tag);
                writer.Write(cipher);
                writer.Flush();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // ---------- 读取 ----------

        public static bool TryRead(string path, string password, out PackagePayload payload, out string error)
        {
            payload = null;
            error = null;
            try
            {
                if (!File.Exists(path))
                {
                    error = "文件不存在";
                    return false;
                }

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
                if (stream.Length < Magic.Length + 4 + SaltBytes + NonceBytes + TagBytes)
                {
                    error = "这不是有效的 SeewoAutoLogin 配置文件";
                    return false;
                }

                var magic = reader.ReadBytes(Magic.Length);
                if (!magic.SequenceEqual(Magic))
                {
                    error = "这不是有效的 SeewoAutoLogin 配置文件";
                    return false;
                }

                var version = reader.ReadInt32();
                if (version != Version)
                {
                    error = $"配置包版本 {version} 不受支持（当前程序支持 {Version}）";
                    return false;
                }

                var salt = reader.ReadBytes(SaltBytes);
                var nonce = reader.ReadBytes(NonceBytes);
                var tag = reader.ReadBytes(TagBytes);
                var cipher = reader.ReadBytes((int)(stream.Length - stream.Position));
                if (cipher.Length == 0)
                {
                    error = "配置包内容为空";
                    return false;
                }

                var key = DeriveKey(password ?? "", salt);
                var plain = new byte[cipher.Length];
                using (var gcm = new AesGcm(key, TagBytes))
                    gcm.Decrypt(nonce, cipher, tag, plain);

                payload = JsonSerializer.Deserialize<PackagePayload>(plain);
                if (payload == null || payload.Accounts == null)
                {
                    error = "配置包内容无法解析";
                    return false;
                }
                return true;
            }
            catch (CryptographicException)
            {
                error = "口令不正确，或文件已被改动";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // ---------- 差异比对 ----------

        internal sealed class AccountPreview
        {
            public string Kind { get; set; } = "added";  // added / updated / unchanged
            public string Username { get; set; } = "";
            public string DisplayName { get; set; } = "";
            public string Summary { get; set; } = "";
            public PackageAccount Source { get; set; }
        }

        internal sealed class SettingPreview
        {
            public string Key { get; set; } = "";
            public string Label { get; set; } = "";
            public string Current { get; set; } = "";
            public string Incoming { get; set; } = "";
        }

        internal sealed class ImportPreview
        {
            public List<AccountPreview> Accounts { get; set; } = new();
            public List<SettingPreview> Settings { get; set; } = new();
            public string PackageVersion { get; set; } = "";
            public string CreatedAt { get; set; } = "";
            public PackagePayload Payload { get; set; }
        }

        public static ImportPreview BuildPreview(PackagePayload payload, PluginConfig config)
        {
            var preview = new ImportPreview
            {
                Payload = payload,
                PackageVersion = payload.AppVersion ?? "",
                CreatedAt = FormatLocalTime(payload.CreatedAtUtc)
            };

            foreach (var incoming in payload.Accounts)
            {
                var existing = config.Accounts.FirstOrDefault(a =>
                    !string.IsNullOrWhiteSpace(a.Username) &&
                    string.Equals(a.Username, incoming.Username, StringComparison.OrdinalIgnoreCase));

                if (existing == null)
                {
                    preview.Accounts.Add(new AccountPreview
                    {
                        Kind = "added",
                        Username = incoming.Username,
                        DisplayName = string.IsNullOrWhiteSpace(incoming.DisplayName) ? incoming.Username : incoming.DisplayName,
                        Summary = "新增账号",
                        Source = incoming
                    });
                }
                else
                {
                    var passwordChanged = !string.Equals(existing.DecryptedPassword ?? "", incoming.Password ?? "", StringComparison.Ordinal);
                    var nameChanged = !string.Equals(existing.DisplayName ?? "", incoming.DisplayName ?? "", StringComparison.Ordinal);
                    var tags = incoming.Tags ?? "";
                    var currentTags = existing.Tags == null ? "" : string.Join(",", existing.Tags);
                    var tagsChanged = !string.Equals(currentTags, tags, StringComparison.Ordinal);

                    var changes = new List<string>();
                    if (passwordChanged) changes.Add("密码");
                    if (nameChanged) changes.Add("备注");
                    if (tagsChanged) changes.Add("标签");

                    preview.Accounts.Add(new AccountPreview
                    {
                        Kind = changes.Count == 0 ? "unchanged" : "updated",
                        Username = incoming.Username,
                        DisplayName = string.IsNullOrWhiteSpace(incoming.DisplayName) ? incoming.Username : incoming.DisplayName,
                        Summary = changes.Count == 0 ? "已存在且内容一致" : "更新：" + string.Join("、", changes),
                        Source = incoming
                    });
                }
            }

            var current = CaptureSettings(config);
            foreach (var descriptor in SettingCatalog)
            {
                if (!payload.Settings.TryGetValue(descriptor.Key, out var incomingValue)) continue;
                current.TryGetValue(descriptor.Key, out var currentValue);
                if (string.Equals(currentValue ?? "", incomingValue ?? "", StringComparison.Ordinal)) continue;
                preview.Settings.Add(new SettingPreview
                {
                    Key = descriptor.Key,
                    Label = descriptor.Label,
                    Current = DescribeValue(descriptor.Key, currentValue),
                    Incoming = DescribeValue(descriptor.Key, incomingValue)
                });
            }

            return preview;
        }

        /// <summary>
        /// 应用选中的账号与设置。返回 (新增数, 更新数, 设置数)。
        /// 只动调用方勾选的部分：没勾的账号与设置保持本机原样。
        /// </summary>
        public static (int added, int updated, int settings) Apply(
            ImportPreview preview, PluginConfig config,
            IEnumerable<string> selectedUsernames, IEnumerable<string> selectedSettingKeys)
        {
            var wantedAccounts = new HashSet<string>(selectedUsernames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var wantedSettings = new HashSet<string>(selectedSettingKeys ?? Array.Empty<string>(), StringComparer.Ordinal);

            var added = 0;
            var updated = 0;
            foreach (var item in preview.Accounts)
            {
                if (item.Kind == "unchanged") continue;
                if (!wantedAccounts.Contains(item.Username)) continue;

                var incoming = item.Source;
                var existing = config.Accounts.FirstOrDefault(a =>
                    !string.IsNullOrWhiteSpace(a.Username) &&
                    string.Equals(a.Username, incoming.Username, StringComparison.OrdinalIgnoreCase));

                if (existing == null)
                {
                    var account = new SeewoAccount
                    {
                        DisplayName = string.IsNullOrWhiteSpace(incoming.DisplayName) ? incoming.Username : incoming.DisplayName,
                        Username = incoming.Username,
                        // 落盘前统一加密：与手动添加的账号走同一条路径
                        Password = string.IsNullOrEmpty(incoming.Password) ? "" : SecureStore.Encrypt(incoming.Password),
                        Tags = SplitTags(incoming.Tags)
                    };
                    config.Accounts.Add(account);
                    added++;
                }
                else
                {
                    if (!string.Equals(existing.DecryptedPassword ?? "", incoming.Password ?? "", StringComparison.Ordinal))
                        existing.Password = string.IsNullOrEmpty(incoming.Password) ? "" : SecureStore.Encrypt(incoming.Password);
                    if (!string.Equals(existing.DisplayName ?? "", incoming.DisplayName ?? "", StringComparison.Ordinal))
                        existing.DisplayName = incoming.DisplayName;
                    var incomingTags = SplitTags(incoming.Tags);
                    var currentTags = existing.Tags ?? new List<string>();
                    if (!currentTags.SequenceEqual(incomingTags, StringComparer.Ordinal))
                        existing.Tags = incomingTags;
                    updated++;
                }
            }

            var settings = 0;
            foreach (var key in wantedSettings)
            {
                if (!preview.Payload.Settings.TryGetValue(key, out var value)) continue;
                ApplySetting(config, key, value);
                settings++;
            }

            // 首个账号设为当前账号：否则导入后希沃侧看到的仍是空列表
            if (added > 0 && string.IsNullOrWhiteSpace(config.ActiveAccountId))
                config.ActiveAccountId = config.Accounts.FirstOrDefault()?.Id ?? "";

            return (added, updated, settings);
        }

        // ---------- 内部 ----------

        private static byte[] DeriveKey(string password, byte[] salt)
            => Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);

        private static List<string> SplitTags(string text)
            => string.IsNullOrWhiteSpace(text)
                ? new List<string>()
                : text.Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
                      .Select(t => t.Trim())
                      .Where(t => t.Length > 0)
                      .Distinct(StringComparer.OrdinalIgnoreCase)
                      .ToList();

        private static string DescribeValue(string key, string value)
        {
            if (key == "userListRotationGroupSize") return value ?? "";
            return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ? "开启" : "关闭";
        }

        private static string FormatLocalTime(string utc)
        {
            if (DateTimeOffset.TryParse(utc, out var parsed))
                return parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            return utc ?? "";
        }
    }
}
