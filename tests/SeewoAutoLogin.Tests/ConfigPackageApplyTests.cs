using System;
using System.Collections.Generic;
using System.IO;
using SeewoAutoLogin.Services;
using Xunit;

namespace SeewoAutoLogin.Tests
{
    /// <summary>
    /// 「导入配置后所有账号都登录不了」的回归：某个账号的密码存不进去时，
    /// 只能把它单独标成「待重新录入」，绝不能连坐掉整批导入。
    /// </summary>
    [Collection("SecureStore")] // 见 SecureStoreTests 上的说明：共享全局密钥路径，不能并行
    public class ConfigPackageApplyTests : IDisposable
    {
        private readonly string _dir;

        public ConfigPackageApplyTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "SeewoAutoLogin.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);

            // 制造一个必然失败的主密钥路径：父路径是一个普通文件，CreateDirectory 一定抛 IOException。
            // 效果等同于「本机写不了 credential.key」——旧实现就是在这里让整次导入抛出去的。
            var blocker = Path.Combine(_dir, "blocker");
            File.WriteAllText(blocker, "x");
            SecureStore.KeyPathOverride = Path.Combine(blocker, "credential.key");
            SecureStore.ResetCacheForTests();
        }

        public void Dispose()
        {
            SecureStore.KeyPathOverride = null;
            SecureStore.ResetCacheForTests();
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        }

        [Fact]
        public void Apply_Keeps_Importing_Accounts_When_Password_Cannot_Be_Stored()
        {
            var config = new PluginConfig();
            var payload = new ConfigPackageService.PackagePayload
            {
                Accounts = new List<ConfigPackageService.PackageAccount>
                {
                    new ConfigPackageService.PackageAccount { Username = "user_a", Password = "pw-a", DisplayName = "甲" },
                    new ConfigPackageService.PackageAccount { Username = "user_b", Password = "pw-b", DisplayName = "乙" },
                    new ConfigPackageService.PackageAccount { Username = "user_c", Password = "", DisplayName = "丙" }
                }
            };

            var preview = ConfigPackageService.BuildPreview(payload, config);
            var result = ConfigPackageService.Apply(preview, config,
                new[] { "user_a", "user_b", "user_c" }, Array.Empty<string>());

            // 账号本身照样导入，导入不会整体失败
            Assert.Equal(3, result.Added);
            Assert.Equal(3, config.Accounts.Count);

            // 两个有密码但存不进去的，需要重录；一个本来就没密码的，需要扫码或补密码
            Assert.Equal(2, result.NeedPassword.Count);
            Assert.Single(result.NeedLogin);

            // 宁可留空，也不能把明文密码写进配置
            Assert.All(config.Accounts, a => Assert.Equal("", a.Password));

            // 首个账号要设为当前，否则希沃侧看到的仍是空列表
            Assert.False(string.IsNullOrWhiteSpace(config.ActiveAccountId));
        }
    }
}
