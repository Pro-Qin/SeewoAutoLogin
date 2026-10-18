using System.Text.Json;
using SeewoAutoLogin.Ui;
using Xunit;

namespace SeewoAutoLogin.Tests
{
    public class JsonArgsTests
    {
        private static JsonElement Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        [Fact]
        public void Str_ReturnsValue()
            => Assert.Equal("alice", Parse("{\"name\":\"alice\"}").Str("name"));

        [Fact]
        public void Str_MissingKeyReturnsFallback()
        {
            Assert.Equal("", Parse("{}").Str("name"));
            Assert.Null(Parse("{}").Str("name", null));
        }

        [Fact]
        public void Str_NonStringTypesAreNotGuessed()
        {
            var root = Parse("{\"n\":42,\"b\":true,\"o\":{}}");
            Assert.Equal("42", root.Str("n"));
            Assert.Equal("true", root.Str("b"));
            Assert.Equal("", root.Str("o"));
        }

        [Fact]
        public void Flag_ReadsBooleansOnly()
        {
            var root = Parse("{\"on\":true,\"off\":false,\"text\":\"true\"}");
            Assert.True(root.Flag("on"));
            Assert.False(root.Flag("off"));
            Assert.False(root.Flag("text"));
            Assert.False(root.Flag("missing"));
            Assert.True(root.Flag("missing", true));
        }

        [Fact]
        public void Int_ReadsNumbersOnly()
        {
            var root = Parse("{\"limit\":200,\"text\":\"200\"}");
            Assert.Equal(200, root.Int("limit"));
            Assert.Equal(0, root.Int("text"));
            Assert.Equal(7, root.Int("missing", 7));
        }

        [Fact]
        public void Strings_ReturnsOnlyStringItems()
        {
            var root = Parse("{\"ids\":[\"a\",\"\",\"b\",3]}");
            Assert.Equal(new[] { "a", "b" }, root.Strings("ids"));
            Assert.Empty(root.Strings("missing"));
            Assert.Empty(root.Strings("ids2"));
        }

        [Fact]
        public void Node_MissingReturnsUndefined()
        {
            Assert.Equal(JsonValueKind.Undefined, Parse("{}").Node("value").ValueKind);
            Assert.Equal(JsonValueKind.Object, Parse("{\"value\":{}}").Node("value").ValueKind);
        }

        [Fact]
        public void Helpers_TolerateNonObjectRoot()
        {
            var root = Parse("[1,2,3]");
            Assert.Equal("", root.Str("x"));
            Assert.False(root.Flag("x"));
            Assert.Equal(0, root.Int("x"));
            Assert.Empty(root.Strings("x"));
        }
    }
}
