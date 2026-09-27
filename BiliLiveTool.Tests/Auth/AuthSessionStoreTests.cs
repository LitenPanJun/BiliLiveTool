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
}
