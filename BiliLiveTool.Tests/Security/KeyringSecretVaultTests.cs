using BiliLiveTool.Core.Security;
using BiliLiveTool.Infrastructure.Security;
using FluentAssertions;
using Latchkey;

namespace BiliLiveTool.Tests.Security;

/// <summary>
/// 钥匙串机密后端：会话缓存同一性、跨实例持久化、不可用/故障时仅驻留
/// 内存绝不落文件（AGENTS 机密硬约束），以及与 InMemory 对齐的 Dispose 语义。
/// </summary>
public sealed class KeyringSecretVaultTests
{
    private const string Uid = "42";

    [Fact]
    public void Production_Construction_Never_Throws_And_Is_Bounded()
    {
        // 真实 LatchkeyFactory：有钥匙串的桌面、无 Secret Service 的 CI
        // 容器均须构造成功；不可用即降级，不得抛给组合根，也不得让
        // D-Bus 超时（约 25s）拖死启动——限时 3s 降级，留 10s 余量。
        var started = System.Diagnostics.Stopwatch.StartNew();
        var vault = new KeyringSecretVault();
        started.Stop();

        started.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        if (!vault.Available)
            vault.Load("absent-uid").Should().BeNull();
    }

    [Fact]
    public void Save_Load_Returns_Same_Instances_Within_Session()
    {
        var vault = new KeyringSecretVault(new FakeLatchkey());
        var credential = new SecureCredential("session-value");
        vault.Save(Uid, new Dictionary<string, SecureCredential>
        {
            ["SESSDATA"] = credential,
        });

        var loaded = vault.Load(Uid);

        loaded.Should().NotBeNull();
        loaded!["SESSDATA"].Should().BeSameAs(credential); // 回滚路径依赖实例同一性
        loaded["SESSDATA"].DangerousGetValue().Should().Be("session-value");
    }

    [Fact]
    public void Fresh_Instance_Roundtrips_Payload_Through_Store()
    {
        var store = new FakeLatchkey();
        var writer = new KeyringSecretVault(store);
        writer.Save(Uid, new Dictionary<string, SecureCredential>
        {
            ["bili_jct"] = new SecureCredential("csrf-persisted"),
        });

        var reader = new KeyringSecretVault(store); // 空缓存：强制读钥匙串
        var loaded = reader.Load(Uid);

        loaded.Should().NotBeNull();
        loaded!["bili_jct"].DangerousGetValue().Should().Be("csrf-persisted");
        store.Entries.Should().ContainKey("account:" + Uid);
    }

    [Fact]
    public void Unavailable_Backend_Stays_In_Memory_Only()
    {
        var vault = new KeyringSecretVault(null);
        vault.Available.Should().BeFalse();
        vault.Save(Uid, new Dictionary<string, SecureCredential>
        {
            ["SESSDATA"] = new SecureCredential("memory-only"),
        });

        vault.Load(Uid)!["SESSDATA"].DangerousGetValue().Should().Be("memory-only");

        // 重启等价物：新实例无从恢复，也不曾写过任何文件
        new KeyringSecretVault(null).Load(Uid).Should().BeNull();
    }

    [Fact]
    public void Failing_Backend_Keeps_Session_Cache_Without_Throwing()
    {
        var store = new ThrowingLatchkey();
        var vault = new KeyringSecretVault(store);

        var act = () => vault.Save(Uid, new Dictionary<string, SecureCredential>
        {
            ["SESSDATA"] = new SecureCredential("locked-keyring"),
        });

        act.Should().NotThrow();
        vault.Load(Uid)!["SESSDATA"].DangerousGetValue().Should().Be("locked-keyring");
        new KeyringSecretVault(store).Load(Uid).Should().BeNull(); // 故障视为不存在
    }

    [Fact]
    public void Remove_Deletes_Store_Entry_And_Disposes_Cache()
    {
        var store = new FakeLatchkey();
        var vault = new KeyringSecretVault(store);
        var credential = new SecureCredential("to-remove");
        vault.Save(Uid, new Dictionary<string, SecureCredential>
        {
            ["SESSDATA"] = credential,
        });

        vault.Remove(Uid).Should().BeTrue();

        vault.Load(Uid).Should().BeNull();
        store.Entries.Should().NotContainKey("account:" + Uid);
        // Dispose 清零：原文不存，缓冲全为 NUL
        var wiped = credential.DangerousGetValue();
        wiped.Should().NotBe("to-remove");
        wiped.ToCharArray().Should().OnlyContain(c => c == '\0');
    }

    [Fact]
    public void Replacing_Secrets_Disposes_Displaced_Credentials()
    {
        var vault = new KeyringSecretVault(new FakeLatchkey());
        var displaced = new SecureCredential("old");
        vault.Save(Uid, new Dictionary<string, SecureCredential> { ["a"] = displaced });

        var kept = new SecureCredential("same");
        vault.Save(Uid, new Dictionary<string, SecureCredential> { ["a"] = kept });

        displaced.DangerousGetValue().ToCharArray()
            .Should().OnlyContain(c => c == '\0'); // 被顶替：清零
        kept.DangerousGetValue().Should().Be("same"); // 未被波及
        vault.Load(Uid)!["a"].Should().BeSameAs(kept);
    }

    // --- 测试后端 ---

    private sealed class FakeLatchkey : ILatchkey
    {
        public Dictionary<string, string> Entries { get; } = new(StringComparer.Ordinal);

        public void Set(string key, string value) => Entries[key] = value;

        public void Set(string key, ReadOnlySpan<byte> value) =>
            Entries[key] = Convert.ToBase64String(value);

        public string? Get(string key) => Entries.GetValueOrDefault(key);

        public byte[]? GetBytes(string key) =>
            Entries.TryGetValue(key, out var value) ? Convert.FromBase64String(value) : null;

        public bool Delete(string key) => Entries.Remove(key);

        public bool Contains(string key) => Entries.ContainsKey(key);

        public ValueTask SetAsync(string key, string value, CancellationToken ct = default)
        {
            Set(key, value);
            return ValueTask.CompletedTask;
        }

        public ValueTask SetAsync(
            string key,
            ReadOnlyMemory<byte> value,
            CancellationToken ct = default)
        {
            Set(key, value.Span);
            return ValueTask.CompletedTask;
        }

        public ValueTask<string?> GetAsync(string key, CancellationToken ct = default) =>
            new(Get(key));

        public ValueTask<byte[]?> GetBytesAsync(string key, CancellationToken ct = default) =>
            new(GetBytes(key));

        public ValueTask<bool> DeleteAsync(string key, CancellationToken ct = default) =>
            new(Delete(key));

        public ValueTask<bool> ContainsAsync(string key, CancellationToken ct = default) =>
            new(Contains(key));
    }

    private sealed class ThrowingLatchkey : ILatchkey
    {
        private static Exception Fail() =>
            new InvalidOperationException("keyring locked");

        public void Set(string key, string value) => throw Fail();
        public void Set(string key, ReadOnlySpan<byte> value) => throw Fail();
        public string? Get(string key) => throw Fail();
        public byte[]? GetBytes(string key) => throw Fail();
        public bool Delete(string key) => throw Fail();
        public bool Contains(string key) => throw Fail();

        public ValueTask SetAsync(string key, string value, CancellationToken ct = default) =>
            throw Fail();

        public ValueTask SetAsync(
            string key,
            ReadOnlyMemory<byte> value,
            CancellationToken ct = default) =>
            throw Fail();

        public ValueTask<string?> GetAsync(string key, CancellationToken ct = default) =>
            throw Fail();

        public ValueTask<byte[]?> GetBytesAsync(string key, CancellationToken ct = default) =>
            throw Fail();

        public ValueTask<bool> DeleteAsync(string key, CancellationToken ct = default) =>
            throw Fail();

        public ValueTask<bool> ContainsAsync(string key, CancellationToken ct = default) =>
            throw Fail();
    }
}
