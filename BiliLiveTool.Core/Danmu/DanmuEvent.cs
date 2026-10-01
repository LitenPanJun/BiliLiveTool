namespace BiliLiveTool.Core.Danmu;

/// <summary>弹幕事件，字段对照 danmu_service.py 六类回调载荷。</summary>
public sealed record DanmuEvent(
    string Type,
    string Msg,
    long Uid = 0,
    string Uname = "",
    string Face = "",
    string GiftName = "",
    int Num = 0,
    string Action = "");

/// <summary>事件类型常量，对照原前端 type 字段取值。</summary>
public static class DanmuEventTypes
{
    public const string Danmu = "danmu";
    public const string Interact = "interact";
    public const string Gift = "gift";
    public const string System = "system";
}
