using System.Text.Json.Nodes;

namespace BiliLiveTool.Core.Bilibili;

/// <summary>服务层返回，对照原 services 的 {"code","msg","data"} 字典。</summary>
public sealed record ServiceResult(int Code, string Message, JsonObject? Data = null)
{
    public bool IsSuccess => Code == 0;

    public static ServiceResult Ok(JsonObject? data = null) => new(0, "", data);

    public static ServiceResult Fail(int code, string message) => new(code, message);
}
