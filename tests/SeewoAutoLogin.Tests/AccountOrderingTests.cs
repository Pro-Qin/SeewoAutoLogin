using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace SeewoAutoLogin.Tests
{
    public class AccountOrderingTests
    {
        private static List<SeewoAccount> Accounts(int count)
            => Enumerable.Range(0, count)
                .Select(i => new SeewoAccount { Id = "id" + i, DisplayName = "account" + i })
                .ToList();

        [Theory]
        [InlineData("a,b,c", new[] { "a", "b", "c" })]
        [InlineData("a，b；c;d", new[] { "a", "b", "c", "d" })]
        [InlineData("  a , , b  ", new[] { "a", "b" })]
        [InlineData("A,a,B", new[] { "A", "B" })]
        [InlineData("", new string[0])]
        public void ParseTags_NormalisesSeparatorsAndDuplicates(string input, string[] expected)
        {
            Assert.Equal(expected, AccountOrdering.ParseTags(input));
        }

        [Fact]
        public void ParseTags_NullReturnsEmptyList()
        {
            Assert.NotNull(AccountOrdering.ParseTags(null));
            Assert.Empty(AccountOrdering.ParseTags(null));
        }

        [Fact]
        public void MoveToActive_PullsAccountIntoTailOfActiveArea()
        {
            var accounts = Accounts(8);
            var moved = AccountOrdering.MoveToActive(accounts, "id6");

            Assert.True(moved);
            Assert.Equal(PluginConfig.MaxVisibleAccounts - 1, accounts.FindIndex(a => a.Id == "id6"));
            Assert.Equal(8, accounts.Count);
        }

        [Fact]
        public void MoveToActive_DoesNothingWhenAlreadyActive()
        {
            var accounts = Accounts(8);
            Assert.False(AccountOrdering.MoveToActive(accounts, "id0"));
            Assert.False(AccountOrdering.MoveToActive(accounts, "missing"));
            Assert.False(AccountOrdering.MoveToActive(accounts, ""));
            Assert.False(AccountOrdering.MoveToActive(null, "id0"));
        }

        [Fact]
        public void MoveToInactive_PushesAccountBehindActiveArea()
        {
            var accounts = Accounts(8);
            var moved = AccountOrdering.MoveToInactive(accounts, "id0");

            Assert.True(moved);
            Assert.Equal(PluginConfig.MaxVisibleAccounts, accounts.FindIndex(a => a.Id == "id0"));
            Assert.Equal(8, accounts.Count);
        }

        [Fact]
        public void MoveToInactive_DoesNothingWhenNotActive()
        {
            var accounts = Accounts(8);
            Assert.False(AccountOrdering.MoveToInactive(accounts, "id7"));
            Assert.False(AccountOrdering.MoveToInactive(accounts, "missing"));
            Assert.False(AccountOrdering.MoveToInactive(null, "id0"));
        }

        [Fact]
        public void MoveToInactive_WorksWhenActiveAreaIsWholeList()
        {
            var accounts = Accounts(3);
            Assert.True(AccountOrdering.MoveToInactive(accounts, "id1"));
            Assert.Equal(3, accounts.Count);
            Assert.Equal("id1", accounts[accounts.Count - 1].Id);
        }

        [Fact]
        public void ApplyOrder_RespectsGivenOrderThenKeepsRest()
        {
            var accounts = Accounts(5);
            AccountOrdering.ApplyOrder(accounts, new[] { "id3", "id0" });

            Assert.Equal(new[] { "id3", "id0", "id1", "id2", "id4" }, accounts.Select(a => a.Id));
        }

        [Fact]
        public void ApplyOrder_IgnoresUnknownAndDuplicateIds()
        {
            var accounts = Accounts(3);
            AccountOrdering.ApplyOrder(accounts, new[] { "id2", "id2", "ghost", "" });

            Assert.Equal(new[] { "id2", "id0", "id1" }, accounts.Select(a => a.Id));
            Assert.Equal(3, accounts.Count);
        }

        [Fact]
        public void ApplyOrder_NoOpOnEmptyInput()
        {
            var accounts = Accounts(3);
            AccountOrdering.ApplyOrder(accounts, new string[0]);
            AccountOrdering.ApplyOrder(accounts, null);
            AccountOrdering.ApplyOrder(null, new[] { "id0" });

            Assert.Equal(new[] { "id0", "id1", "id2" }, accounts.Select(a => a.Id));
        }
    }
}
