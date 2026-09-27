using System.Text.Json.Nodes;

namespace BiliLiveTool.Tests.Canary;

/// <summary>
/// 加载提交进仓库的金丝雀基准值 vectors.json（由 generate_vectors.py
/// 从 .refs 参照源执行生成，是逐字节一致的冻结产物）。
/// </summary>
internal static class CanaryVectors
{
    public static JsonObject Root { get; } = Load();

    private static JsonObject Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Canary", "vectors.json");
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }

    public static Dictionary<string, string> ToStringMap(JsonNode node) =>
        node.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>());
}
