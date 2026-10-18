using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;

namespace SeewoAutoLogin.Tests
{
    public class CsvParserTests
    {
        [Fact]
        public void Parse_SplitsRowsAndCells()
        {
            var rows = CsvParser.Parse("a,b,c\n1,2,3");

            Assert.Equal(2, rows.Count);
            Assert.Equal(new[] { "a", "b", "c" }, rows[0]);
            Assert.Equal(new[] { "1", "2", "3" }, rows[1]);
        }

        [Fact]
        public void Parse_HandlesCrLfAndCr()
        {
            // CRLF / 单独的 CR / 单独的 LF 三种换行都算一行分隔
            var rows = CsvParser.Parse("a\r\nb\rc\nd");
            Assert.Equal(4, rows.Count);
            Assert.Equal(new[] { "a", "b", "c", "d" }, rows.ConvertAll(r => r[0]));
        }

        [Fact]
        public void Parse_KeepsEmptyTrailingLineSemantics()
        {
            // 末尾没有换行：最后一行不能丢
            Assert.Single(CsvParser.Parse("only,row"));
            // 末尾正好有换行：不能多出一行空记录
            Assert.Single(CsvParser.Parse("only,row\n"));
        }

        [Fact]
        public void Parse_QuotedFieldCanContainSeparatorsAndQuotes()
        {
            var rows = CsvParser.Parse("\"a,b\",\"say \"\"hi\"\"\",\"multi\nline\"");

            Assert.Single(rows);
            Assert.Equal(new[] { "a,b", "say \"hi\"", "multi\nline" }, rows[0]);
        }

        [Fact]
        public void Parse_StripsBom()
        {
            var rows = CsvParser.Parse("\uFEFFuser,pass");
            Assert.Equal("user", rows[0][0]);
        }

        [Fact]
        public void Parse_EmptyInputReturnsNoRows()
        {
            Assert.Empty(CsvParser.Parse(""));
            Assert.Empty(CsvParser.Parse(null));
        }

        [Theory]
        [InlineData("账号,密码,备注")]
        [InlineData("用户名,密码")]
        [InlineData("USERNAME,password")]
        [InlineData("序号,账号,密码")]
        [InlineData("no,username,password")]
        public void IsHeaderRow_RecognisesHeaderLines(string line)
        {
            var cells = new List<string>(line.Split(','));
            Assert.True(CsvParser.IsHeaderRow(cells));
        }

        [Theory]
        [InlineData("13800138000,pass")]   // 真实手机号不能被当成表头
        [InlineData("zhangsan,pwd")]
        [InlineData(",,")]
        public void IsHeaderRow_KeepsRealDataRows(string line)
        {
            var cells = new List<string>(line.Split(','));
            Assert.False(CsvParser.IsHeaderRow(cells));
        }

        [Fact]
        public void IsHeaderRow_HandlesEmptyInput()
        {
            Assert.False(CsvParser.IsHeaderRow(null));
            Assert.False(CsvParser.IsHeaderRow(new List<string>()));
        }

        [Fact]
        public void AddMessage_CapsAtLimit()
        {
            var messages = new List<string>();
            for (var i = 0; i < CsvParser.MaxMessages + 5; i++)
                CsvParser.AddMessage(messages, "line " + i);

            Assert.Equal(CsvParser.MaxMessages + 1, messages.Count);
            Assert.Contains("省略", messages[messages.Count - 1]);
        }

        [Fact]
        public void ReadText_HandlesUtf8WithBom()
        {
            var path = Path.Combine(Path.GetTempPath(), "csv-" + Guid.NewGuid().ToString("N") + ".csv");
            try
            {
                File.WriteAllBytes(path, new byte[] { 0xEF, 0xBB, 0xBF }
                    .Concat(new UTF8Encoding(false).GetBytes("账号,密码")));
                Assert.Equal("账号,密码", CsvParser.ReadText(path));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void ReadText_FallsBackToGbk()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var gbk = Encoding.GetEncoding(936);
            var path = Path.Combine(Path.GetTempPath(), "csv-" + Guid.NewGuid().ToString("N") + ".csv");
            try
            {
                File.WriteAllBytes(path, gbk.GetBytes("张三,密码"));
                Assert.Equal("张三,密码", CsvParser.ReadText(path));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void ReadText_EmptyFileReturnsEmptyString()
        {
            var path = Path.Combine(Path.GetTempPath(), "csv-" + Guid.NewGuid().ToString("N") + ".csv");
            try
            {
                File.WriteAllBytes(path, Array.Empty<byte>());
                Assert.Equal("", CsvParser.ReadText(path));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }

    internal static class EnumerableExtensions
    {
        public static byte[] Concat(this byte[] head, byte[] tail)
        {
            var result = new byte[head.Length + tail.Length];
            Buffer.BlockCopy(head, 0, result, 0, head.Length);
            Buffer.BlockCopy(tail, 0, result, head.Length, tail.Length);
            return result;
        }
    }
}
