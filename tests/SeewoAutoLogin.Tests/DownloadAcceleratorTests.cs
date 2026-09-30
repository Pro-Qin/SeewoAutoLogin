using System.Linq;
using SeewoAutoLogin.Services;
using Xunit;

namespace SeewoAutoLogin.Tests
{
    public class DownloadAcceleratorTests
    {
        [Fact]
        public void BuildCandidates_StartsWith_OfficialUrl()
        {
            var url = "https://github.com/Pro-Qin/SeewoAutoLogin/releases/download/v1.0.0/app.exe";
            var candidates = DownloadAccelerator.BuildCandidates(url);
            Assert.NotEmpty(candidates);
            Assert.Equal(url, candidates[0]);
            Assert.Contains(candidates, c => c.Contains("gh-proxy.com"));
        }

        [Theory]
        [InlineData("https://github.com/a/b", true)]
        [InlineData("http://github.com/a/b", false)]
        [InlineData("https://evil.example/a", false)]
        public void IsTrustedOfficialUrl_Works(string url, bool expected)
            => Assert.Equal(expected, DownloadAccelerator.IsTrustedOfficialUrl(url));
    }
}