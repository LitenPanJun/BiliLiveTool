using System.Text.Json.Nodes;
using BiliLiveTool.Core.Auth;
using BiliLiveTool.Services.Config;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BiliLiveTool.Tests.Config;

/// <summary>
/// config.json 落盘与迁移（指南迁移策略：备份 .bak、白名单仅非密钥、
/// 机密丢弃要求重扫码）以及 700/600 权限收紧。
/// </summary>
public sealed class FileConfigStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "bilivelivetool-tests",
        Guid.NewGuid().ToString("N"));

    private string ConfigPath => Path.Combine(_dir, "config.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响断言结果
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private FileConfigStore Create() =>
        new(ConfigPath, NullLogger<FileConfigStore>.Instance);

    [Fact]
    public void NewInstall_Writes_Skeleton_And_Roundtrips()
    {
        var first = Create();

        first.MinToTray.Should().BeTrue();
        first.CurrentUid.Should().BeNull();

        first.Save(new AccountRecord
        {
            Uid = "123",
            Uname = "测试用户",
            LastTitle = "上次标题",
            LastAreaId = 72,
            LastAreaName = ["娱乐", "舞蹈"],
        });
        first.SetMinToTray(false);

        // 重开（新实例同路径）：全部偏好还原
        var second = Create();
        second.MinToTray.Should().BeFalse();
        second.CurrentUid.Should().Be("123");
        var reloaded = second.Get("123");
        reloaded.Should().NotBeNull();
        reloaded!.Uname.Should().Be("测试用户");
        reloaded.LastTitle.Should().Be("上次标题");
        reloaded.LastAreaId.Should().Be(72);
        reloaded.LastAreaName.Should().Equal("娱乐", "舞蹈");
    }

    [Fact]
    public void Remove_Clears_Current_And_Survives_Reload()
    {
        var first = Create();
        first.Save(new AccountRecord { Uid = "7" });
        first.Save(new AccountRecord { Uid = "8" });
        first.Remove("8").Should().BeTrue();
        first.CurrentUid.Should().BeNull();

        var second = Create();
        second.Get("8").Should().BeNull();
        second.CurrentUid.Should().BeNull();
        second.Remove("8").Should().BeFalse();
    }

    [Fact]
    public void Legacy_Config_Migrates_Whitelist_And_Drops_Secrets()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigPath, """
            {
              "min_to_tray": false,
              "current_uid": "42",
              "users": {
                "42": {
                  "uid": "42",
                  "uname": "老用户",
                  "last_title": "上次标题",
                  "last_area_id": 11,
                  "SESSDATA": "secret-plain-value",
                  "bili_jct": "csrf-plain-value",
                  "cookies": "buvid3=xxx; sid=yyy"
                }
              }
            }
            """);

        var store = Create();

        // 迁移前原样备份
        File.Exists(ConfigPath + ".bak").Should().BeTrue();
        File.ReadAllText(ConfigPath + ".bak").Should().Contain("secret-plain-value");
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(ConfigPath + ".bak").Should().Be(
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        // 白名单字段保留
        store.CurrentUid.Should().Be("42");
        store.MinToTray.Should().BeFalse();
        var migrated = store.Get("42");
        migrated.Should().NotBeNull();
        migrated!.Uname.Should().Be("老用户");
        migrated.LastTitle.Should().Be("上次标题");
        migrated.LastAreaId.Should().Be(11);

        // 重写文件：机密键彻底消失、schema 升到 2
        var rewritten = File.ReadAllText(ConfigPath);
        rewritten.Should()
            .NotContain("secret-plain-value")
            .And.NotContain("csrf-plain-value")
            .And.NotContain("SESSDATA")
            .And.NotContain("bili_jct")
            .And.NotContain("cookies");
        JsonNode.Parse(rewritten)!["schema"]!.GetValue<int>()
            .Should().Be(FileConfigStore.SchemaVersion);
    }

    [Fact]
    public void Legacy_Secrets_Require_Rescan_Not_Respawn()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigPath, """
            {"users": {"42": {"uid": "42", "SESSDATA": "secret-plain-value"}}}
            """);

        var store = Create();

        // 迁移不产生任何机密载体：重开后依然没有，只能重扫码
        var second = Create();
        second.List().Should().ContainSingle().Which.Uid.Should().Be("42");
        File.ReadAllText(ConfigPath).Should().NotContain("secret-plain-value");
        store.Get("42")!.Uid.Should().Be("42");
    }

    [Fact]
    public void Corrupt_Config_Backs_Up_Original_And_Resets()
    {
        Directory.CreateDirectory(_dir);
        const string broken = "{ this is not json";
        File.WriteAllText(ConfigPath, broken);

        var store = Create();

        File.Exists(ConfigPath + ".bak").Should().BeTrue();
        File.ReadAllText(ConfigPath + ".bak").Should().Be(broken);
        store.List().Should().BeEmpty();
        JsonNode.Parse(File.ReadAllText(ConfigPath))!["schema"]!.GetValue<int>()
            .Should().Be(FileConfigStore.SchemaVersion);
    }

    [Fact]
    public void Config_Directory_700_File_600_On_Unix()
    {
        if (OperatingSystem.IsWindows())
            return;

        var store = Create();
        store.Save(new AccountRecord { Uid = "1" });

        File.GetUnixFileMode(_dir).Should().Be(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.GetUnixFileMode(ConfigPath).Should().Be(
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
