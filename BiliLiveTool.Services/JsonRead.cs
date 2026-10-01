using System.Globalization;
using System.Text.Json.Nodes;

namespace BiliLiveTool.Services;

/// <summary>
/// 宽松读取 JSON 字段：兼容 typed 节点（.NET 的 JsonValuePrimitive 严格不宽化，
/// 需按实际类型逐支匹配）与解析节点（element），数字/字符串互认，
/// 对照原实现对类型混用的容忍（如 room_id 的 int/str 混用）。
/// </summary>
internal static class JsonRead
{
    public static string? ReadText(JsonNode? node) => node switch
    {
        null => null,
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonValue value when value.TryGetValue<long>(out var number) =>
            number.ToString(CultureInfo.InvariantCulture),
        JsonValue value when value.TryGetValue<int>(out var number) =>
            number.ToString(CultureInfo.InvariantCulture),
        JsonValue value when value.TryGetValue<double>(out var number) =>
            number.ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

    public static long? ReadLong(JsonNode? node) => node switch
    {
        null => null,
        JsonValue value when value.TryGetValue<long>(out var number) => number,
        JsonValue value when value.TryGetValue<int>(out var number) => number,
        JsonValue value when value.TryGetValue<double>(out var number) => (long)number,
        JsonValue value when value.TryGetValue<string>(out var text)
            && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };

    public static int? ReadInt(JsonNode? node) => ReadLong(node) is { } value ? (int)value : null;

    public static double ReadDouble(JsonNode? node) => node switch
    {
        JsonValue value when value.TryGetValue<double>(out var number) => number,
        JsonValue value when value.TryGetValue<long>(out var number) => number,
        JsonValue value when value.TryGetValue<int>(out var number) => number,
        JsonValue value when value.TryGetValue<string>(out var text)
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => 0,
    };
}
