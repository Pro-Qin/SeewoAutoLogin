using SeewoAutoLogin.Services;
using Xunit;

namespace SeewoAutoLogin.Tests
{
    public class VersionTests
    {
        [Theory]
        [InlineData("v1.2.3", "1.2.3")]
        [InlineData("1.2.3", "1.2.3")]
        [InlineData("v1.2", "1.2")]
        [InlineData("1.8.0-beta", "1.8.0")]
        [InlineData("", "")]
        public void NormalizeVersion_Works(string input, string expected)
            => Assert.Equal(expected, UpdateChecker.NormalizeVersion(input));

        [Theory]
        [InlineData("1.10.0", "1.9.9")]
        [InlineData("2.0", "1.9.9")]
        [InlineData("1.2.3", "1.2.2")]
        public void CompareVersions_LeftGreater(string left, string right)
            => Assert.True(UpdateChecker.CompareVersions(left, right) > 0);

        [Theory]
        [InlineData("1.9.9", "1.10.0")]
        [InlineData("1.0.0", "2.0")]
        public void CompareVersions_LeftSmaller(string left, string right)
            => Assert.True(UpdateChecker.CompareVersions(left, right) < 0);

        [Fact]
        public void CompareVersions_Equal()
            => Assert.Equal(0, UpdateChecker.CompareVersions("1.2.3", "1.2.3"));
    }
}