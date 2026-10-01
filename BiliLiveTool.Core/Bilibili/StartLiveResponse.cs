using System.Text.Json.Nodes;

namespace BiliLiveTool.Core.Bilibili;

/// <summary>
/// startLive 应答 data（rtmp / protocols / qr）；
/// 双路解析为领域对象的过程由 LiveService 完成。
/// </summary>
public sealed record StartLiveResponse(JsonObject? Rtmp, JsonArray Protocols, string? Qr)
{
    public static StartLiveResponse Parse(JsonObject? data)
    {
        if (data is null) return new(null, [], null);
        return new(
            data["rtmp"] as JsonObject,
            data["protocols"] as JsonArray ?? [],
            data["qr"] is JsonValue qr && qr.TryGetValue<string>(out var qrText) ? qrText : null);
    }
}
