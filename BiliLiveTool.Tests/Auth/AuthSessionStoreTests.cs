using BiliLiveTool.Core.Security;
using BiliLiveTool.Core.State;
using BiliLiveTool.Services.Auth;
using FluentAssertions;

namespace BiliLiveTool.Tests.Auth;

public class AuthSessionStoreTests
{
    [Fact]
    public void Starts_Empty()
    {
        new AuthSessionStore().CurrentSnapshot.Should().BeSameAs(SessionSnapshot.Empty);
    }

    [Fact]
    public void Replace_Swaps_Snapshot_And_Returns_Previous_For_Rollback()
    {
        var store = new AuthSessionStore();
        var next = new SessionSnapshot { Uid = 42, RoomId = "12345" };

        var previous = store.Replace(next);

        previous.Should().BeSameAs(SessionSnapshot.Empty);
        store.CurrentSnapshot.Should().BeSameAs(next);
        store.CurrentSnapshot.Uid.Should().Be(42);
    }

    [Fact]
    public void Clear_Resets_To_Empty_And_Returns_Old_Snapshot()
    {
        var store = new AuthSessionStore();
        store.Replace(new SessionSnapshot { Uid = 7, RoomId = "99" });

        var old = store.Clear();

        old.Uid.Should().Be(7);
        store.CurrentSnapshot.Should().BeSameAs(SessionSnapshot.Empty);
    }

    [Fact]
    public void Snapshot_Is_Immutable_And_Keeps_Credentials_As_Secure_Objects()
    {
        using var jct = new SecureCredential("jct-secret");
        var snapshot = new SessionSnapshot { BiliJct = jct };

        var mutated = snapshot with { RoomId = "555" };

        snapshot.RoomId.Should().Be("");
        mutated.RoomId.Should().Be("555");
        snapshot.BiliJct.Should().BeSameAs(jct);
        snapshot.BiliJct!.ToString().Should().Be("***");
    }

    [Fact]
    public void Csrf_Is_Derived_From_BiliJct_Not_A_Separate_Copy()
    {
        using var jct = new SecureCredential("jct-secret");
        var snapshot = new SessionSnapshot { BiliJct = jct };

        snapshot.Csrf.Should().BeSameAs(jct);
        new SessionSnapshot().Csrf.Should().BeNull();
    }

    [Fact]
    public void ResolveAreaId_Looks_Up_Partition_Map()
    {
        var store = new AuthSessionStore();
        store.SetPartitions(new Dictionary<string, IReadOnlyDictionary<string, int>>
        {
            ["宅系"] = new Dictionary<string, int> { ["A-SOUL"] = 235, ["虚拟主播"] = 3259 },
            ["知识"] = new Dictionary<string, int> { ["科学"] = 204 },
        });

        store.ResolveAreaId("宅系", "A-SOUL").Should().Be(235);
        store.ResolveAreaId("知识", "科学").Should().Be(204);
        store.ResolveAreaId("知识", "不存在").Should().BeNull();
        store.ResolveAreaId("不存在", "科学").Should().BeNull();
    }

    [Fact]
    public void SetPartitions_Replaces_Map_Wholesale()
    {
        var store = new AuthSessionStore();
        store.SetPartitions(new Dictionary<string, IReadOnlyDictionary<string, int>>
        {
            ["旧"] = new Dictionary<string, int> { ["区"] = 1 },
        });

        store.SetPartitions(new Dictionary<string, IReadOnlyDictionary<string, int>>());

        store.Partitions.Should().BeEmpty();
        store.ResolveAreaId("旧", "区").Should().BeNull();
    }
}
