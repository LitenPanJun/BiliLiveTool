using BiliLiveTool.Core.Security;
using BiliLiveTool.Services.Auth;
using FluentAssertions;

namespace BiliLiveTool.Tests.Auth;

public class InMemorySecretVaultTests
{
    private static Dictionary<string, SecureCredential> Secrets(params (string Key, string Value)[] pairs)
    {
        var dict = new Dictionary<string, SecureCredential>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs) dict[key] = new SecureCredential(value);
        return dict;
    }

    [Fact]
    public void Save_And_Load_Return_Same_Credential_Instances()
    {
        var vault = new InMemorySecretVault();
        var secrets = Secrets(("SESSDATA", "ses-val"), ("bili_jct", "jct-val"));
        vault.Save("1", secrets);

        var loaded = vault.Load("1");

        loaded.Should().NotBeNull();
        loaded!["SESSDATA"].Should().BeSameAs(secrets["SESSDATA"]);
        loaded["bili_jct"].Should().BeSameAs(secrets["bili_jct"]);
    }

    [Fact]
    public void Load_Missing_Uid_Returns_Null() =>
        new InMemorySecretVault().Load("nope").Should().BeNull();

    [Fact]
    public void Save_Replaces_And_Zeroes_Displaced_Credentials()
    {
        var vault = new InMemorySecretVault();
        using var old = new SecureCredential("old-val");
        vault.Save("1", new Dictionary<string, SecureCredential> { ["SESSDATA"] = old });

        using var fresh = new SecureCredential("new-val");
        vault.Save("1", new Dictionary<string, SecureCredential> { ["SESSDATA"] = fresh });

        old.DangerousGetValue().Should().Be(new string('\0', 7));
        vault.Load("1")!["SESSDATA"].DangerousGetValue().Should().Be("new-val");
    }

    [Fact]
    public void ReSaving_Loaded_Instances_Does_Not_Dispose_Them()
    {
        // 回滚路径：Load 出的原集合整体回存，实例不得被误清零
        var vault = new InMemorySecretVault();
        using var cred = new SecureCredential("keep");
        vault.Save("1", new Dictionary<string, SecureCredential> { ["SESSDATA"] = cred });
        var loaded = vault.Load("1")!;

        vault.Save("1", loaded);

        cred.DangerousGetValue().Should().Be("keep");
        vault.Load("1")!["SESSDATA"].DangerousGetValue().Should().Be("keep");
    }

    [Fact]
    public void Remove_Disposes_And_Drops_Entry()
    {
        var vault = new InMemorySecretVault();
        using var cred = new SecureCredential("bye");
        vault.Save("1", new Dictionary<string, SecureCredential> { ["SESSDATA"] = cred });

        vault.Remove("1").Should().BeTrue();

        vault.Load("1").Should().BeNull();
        cred.DangerousGetValue().Should().Be(new string('\0', 3));
        vault.Remove("1").Should().BeFalse();
    }

    [Fact]
    public void Load_Returns_Independent_Dict_Shells()
    {
        var vault = new InMemorySecretVault();
        var secrets = Secrets(("SESSDATA", "ses-val"));
        vault.Save("1", secrets);

        var first = vault.Load("1");
        var second = vault.Load("1");

        first.Should().NotBeSameAs(second);
        first!["SESSDATA"].Should().BeSameAs(second!["SESSDATA"]);
    }
}
