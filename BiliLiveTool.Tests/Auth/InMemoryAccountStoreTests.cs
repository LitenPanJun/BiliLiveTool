using BiliLiveTool.Core.Auth;
using BiliLiveTool.Services.Auth;
using FluentAssertions;

namespace BiliLiveTool.Tests.Auth;

public class InMemoryAccountStoreTests
{
    private static AccountRecord Account(string uid, string uname = "tester") =>
        new() { Uid = uid, Uname = uname };

    [Fact]
    public void Save_Upserts_And_Marks_Current()
    {
        var store = new InMemoryAccountStore();

        store.Save(Account("1"));

        store.Get("1")!.Uname.Should().Be("tester");
        store.CurrentUid.Should().Be("1");

        store.Save(Account("1", "renamed"));
        store.Get("1")!.Uname.Should().Be("renamed");
        store.CurrentUid.Should().Be("1");
    }

    [Fact]
    public void Upsert_Keeps_Current_Pointer()
    {
        var store = new InMemoryAccountStore();
        store.Save(Account("1"));

        store.Upsert(Account("2"));

        store.CurrentUid.Should().Be("1");
        store.Get("2").Should().NotBeNull();
    }

    [Fact]
    public void SetCurrent_Accepts_Null()
    {
        var store = new InMemoryAccountStore();
        store.Save(Account("1"));

        store.SetCurrent(null);

        store.CurrentUid.Should().BeNull();
        store.Get("1").Should().NotBeNull();
    }

    [Fact]
    public void Remove_Deletes_And_Clears_Current_When_Matching()
    {
        var store = new InMemoryAccountStore();
        store.Save(Account("1"));

        store.Remove("1").Should().BeTrue();

        store.Get("1").Should().BeNull();
        store.CurrentUid.Should().BeNull();
        store.Remove("1").Should().BeFalse();
    }

    [Fact]
    public void Remove_Keeps_Current_When_Other_Account_Deleted()
    {
        var store = new InMemoryAccountStore();
        store.Save(Account("1"));
        store.Upsert(Account("2"));

        store.Remove("2").Should().BeTrue();

        store.CurrentUid.Should().Be("1");
    }

    [Fact]
    public void List_Returns_All_Saved_Accounts()
    {
        var store = new InMemoryAccountStore();
        store.Save(Account("1"));
        store.Upsert(Account("2"));

        store.List().Select(a => a.Uid).Should().BeEquivalentTo("1", "2");
    }
}
