using SeewoAutoLogin.Services;
using Xunit;

namespace SeewoAutoLogin.Tests
{
    public class SilentUpdatePolicyTests
    {
        private const double Mb = 1024.0 * 1024.0;

        [Theory]
        [InlineData(1.0)]   // 刚好到阈值
        [InlineData(1.2)]
        [InlineData(8.0)]
        public void ShouldAdopt_WhenFastEnough_AnyProgress(double mbPerSecond)
        {
            Assert.True(SilentUpdatePolicy.ShouldAdopt((long)(mbPerSecond * Mb), 3.0));
            Assert.True(SilentUpdatePolicy.ShouldAdopt((long)(mbPerSecond * Mb), null));
        }

        [Fact]
        public void ShouldAdopt_WhenBelowFastThreshold_AndEarly_IsFalse()
        {
            Assert.False(SilentUpdatePolicy.ShouldAdopt((long)(0.9 * Mb), 10.0));
            Assert.False(SilentUpdatePolicy.ShouldAdopt((long)(0.2 * Mb), 49.9));
        }

        [Fact]
        public void ShouldAdopt_WhenPastHalfway_AndModerate_IsTrue()
        {
            Assert.True(SilentUpdatePolicy.ShouldAdopt((long)(0.6 * Mb), 60.0));
            Assert.True(SilentUpdatePolicy.ShouldAdopt((long)(0.5 * Mb), 51.0)); // 刚好到阈值
        }

        [Fact]
        public void ShouldAdopt_WhenPastHalfway_ButSlow_IsFalse()
        {
            Assert.False(SilentUpdatePolicy.ShouldAdopt((long)(0.4 * Mb), 90.0));
        }

        [Fact]
        public void ShouldAdopt_ExactlyHalfway_IsFalse()
        {
            // 「过半」是严格大于：50% 时按第一条判据走
            Assert.False(SilentUpdatePolicy.ShouldAdopt((long)(0.6 * Mb), 50.0));
            Assert.True(SilentUpdatePolicy.ShouldAdopt((long)(1.0 * Mb), 50.0));
        }

        [Fact]
        public void Describe_ReportsRateAndPercent()
        {
            Assert.Equal("2.0 MB/s，45%", SilentUpdatePolicy.Describe(2 * Mb, 45));
            Assert.Equal("800 KB/s，进度未知", SilentUpdatePolicy.Describe(800 * 1024, null));
        }

        [Fact]
        public void FormatRate_SwitchesUnitAtThresholds()
        {
            Assert.Equal("1.0 MB/s", SilentUpdatePolicy.FormatRate(Mb));
            Assert.Equal("512 KB/s", SilentUpdatePolicy.FormatRate(512 * 1024));
            Assert.Equal("400 B/s", SilentUpdatePolicy.FormatRate(400));
        }
    }
}
