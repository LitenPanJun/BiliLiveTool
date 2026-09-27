using BiliLiveTool.Core.Security;

namespace BiliLiveTool.Core.State;

/// <summary>
/// 会话状态的不可变快照（对照原 state.py 的 SessionState）。
/// 仅 init 可赋值；机密字段以 SecureCredential 承载，禁止裸 string。
/// </summary>
public sealed record SessionSnapshot
{
    public static SessionSnapshot Empty { get; } = new();

    public long Uid { get; init; }
    public string RoomId { get; init; } = "";
    public bool IsLive { get; init; }
    public int? CurrentAreaId { get; init; }
    public IReadOnlyList<string> CurrentAreaNames { get; init; } = [];
    public SecureCredential? SesData { get; init; }
    public SecureCredential? BiliJct { get; init; }
    public SecureCredential? Buvid3 { get; init; }
}
