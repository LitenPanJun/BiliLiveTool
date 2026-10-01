using System.Text.Json.Serialization;

namespace BiliLiveTool.Core.Auth;

/// <summary>
/// 账号资料，字段名对照原 save_user_data 的 new_data；
/// 不含 cookie/csrf——机密一律经 <see cref="ISecretVault"/>，禁止落入非密钥记录。
/// </summary>
public sealed record AccountRecord
{
    [JsonPropertyName("uid")]
    public required string Uid { get; init; }

    [JsonPropertyName("uname")]
    public string Uname { get; init; } = "未知用户";

    [JsonPropertyName("face")]
    public string Face { get; init; } = "";

    [JsonPropertyName("roomId")]
    public string RoomId { get; init; } = "";

    [JsonPropertyName("level")]
    public int Level { get; init; }

    [JsonPropertyName("current_exp")]
    public long CurrentExp { get; init; }

    [JsonPropertyName("next_exp")]
    public long NextExp { get; init; }

    [JsonPropertyName("money")]
    public double Money { get; init; }

    [JsonPropertyName("bcoin")]
    public double Bcoin { get; init; }

    [JsonPropertyName("following")]
    public int Following { get; init; }

    [JsonPropertyName("follower")]
    public int Follower { get; init; }

    [JsonPropertyName("dynamic_count")]
    public int DynamicCount { get; init; }

    [JsonPropertyName("last_title")]
    public string LastTitle { get; init; } = "";

    [JsonPropertyName("last_area_id")]
    public int? LastAreaId { get; init; }

    [JsonPropertyName("last_area_name")]
    public IReadOnlyList<string> LastAreaName { get; init; } = [];

    [JsonPropertyName("last_announcement")]
    public string LastAnnouncement { get; init; } = "";
}
