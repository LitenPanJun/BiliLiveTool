using System.Text.Json.Nodes;

namespace BiliLiveTool.Core.Bilibili;

/// <summary>
/// startLive 应答 data（rtmp / protocols / qr）；
/// 双路解析为领域对象的过程由 LiveService 完成。
/// </summary>
public sealed record StartLiveResponse(JsonObject? Rtmp, JsonArray Protocols, string? Qr)
{
    public static StartLiveResponse Parse(JsonNode? data)
    {
        if (data is not JsonObject obj) return new(null, [], null);
        return new(
            obj["rtmp"] as JsonObject,
            obj["protocols"] as JsonArray ?? [],
            obj["qr"] is JsonValue qr && qr.TryGetValue<string>(out var qrText) ? qrText : null);
    }
}
