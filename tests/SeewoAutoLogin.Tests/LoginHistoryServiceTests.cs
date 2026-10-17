using System;
using System.IO;
using System.Linq;
using SeewoAutoLogin.Services;
using Xunit;

namespace SeewoAutoLogin.Tests
{
    /// <summary>
    /// 登录记录的读写与节流。测试全程写在临时目录，走完把路径还原，
    /// 绝不碰 %LOCALAPPDATA% 下的真实记录。
    /// </summary>
    public class LoginHistoryServiceTests : IDisposable
    {
        private readonly string _dir;

        public LoginHistoryServiceTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "SeewoAutoLoginTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            LoginHistoryService.FilePathOverride = Path.Combine(_dir, "login-history.jsonl");
            LoginHistoryService.Clear();
        }

        public void Dispose()
        {
            LoginHistoryService.Clear();
            LoginHistoryService.FilePathOverride = null;
        }

        [Fact]
        public void Record_ThenQuery_ReturnsEntry()
        {
            LoginHistoryService.Record("acc-1", "张老师", "13800000000", "qr", true, "界面扫码登录");

            var records = LoginHistoryService.Query(null, 10);

            var record = Assert.Single(records);
            Assert.Equal("acc-1", record.AccountId);
            Assert.Equal("张老师", record.Name);
            Assert.Equal("qr", record.Source);
            Assert.True(record.Success);
            Assert.False(string.IsNullOrWhiteSpace(record.AtUtc));
        }

        [Fact]
        public void Record_SameAccountAndSource_IsThrottled()
        {
            LoginHistoryService.Record("acc-1", "张老师", "u", "seewo", true, "第一次");
            LoginHistoryService.Record("acc-1", "张老师", "u", "seewo", true, "第二次");

            // 希沃两秒一次地请求 SSOLOGIN，不节流的话一天能写几万条
            Assert.Single(LoginHistoryService.Query("acc-1", 10));
        }

        [Fact]
        public void Record_Failure_IsNeverThrottled()
        {
            LoginHistoryService.Record("acc-1", "张老师", "u", "keepalive", false, "密码不正确");
            LoginHistoryService.Record("acc-1", "张老师", "u", "keepalive", false, "密码不正确");

            // 失败是用户最需要看到的，不能被节流吃掉
            Assert.Equal(2, LoginHistoryService.Query("acc-1", 10).Count);
        }

        [Fact]
        public void Query_FiltersByAccount()
        {
            LoginHistoryService.Record("acc-1", "A", "u1", "qr", true, "");
            LoginHistoryService.Record("acc-2", "B", "u2", "qr", true, "");

            var onlySecond = LoginHistoryService.Query("acc-2", 10);

            Assert.Single(onlySecond);
            Assert.Equal("acc-2", onlySecond[0].AccountId);
        }

        [Fact]
        public void Query_ReturnsNewestFirst()
        {
            LoginHistoryService.Record("acc-1", "A", "u1", "qr", true, "旧的");
            System.Threading.Thread.Sleep(5);
            LoginHistoryService.Record("acc-1", "A", "u1", "password", true, "新的");

            var records = LoginHistoryService.Query("acc-1", 10);

            Assert.Equal(2, records.Count);
            Assert.Equal("password", records[0].Source);
        }

        [Fact]
        public void Clear_EmptiesHistory()
        {
            LoginHistoryService.Record("acc-1", "A", "u", "qr", true, "");
            LoginHistoryService.Clear();

            Assert.Empty(LoginHistoryService.Query(null, 10));
        }

        [Fact]
        public void Query_SkipsBrokenLines()
        {
            LoginHistoryService.Record("acc-1", "A", "u", "qr", true, "");
            File.AppendAllText(LoginHistoryService.FilePath, "{ 这不是合法 json\n");

            var records = LoginHistoryService.Query(null, 10);

            Assert.Single(records);
        }

        [Theory]
        [InlineData("seewo", "希沃请求")]
        [InlineData("keepalive", "自动续期")]
        [InlineData("qr", "扫码登录")]
        [InlineData("password", "密码登录")]
        public void DescribeSource_MapsKnownSources(string source, string expected)
            => Assert.Equal(expected, LoginHistoryService.DescribeSource(source));
    }
}
