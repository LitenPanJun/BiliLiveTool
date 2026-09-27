using System.Text.Json;
using System.Text.Json.Nodes;
using BiliLiveTool.Core.Security;

namespace BiliLiveTool.Infrastructure.Bilibili;

/// <summary>
/// 日志脱敏实现，复刻原 bilibili_api.py 的 _mask_data / _mask_url 与
/// danmu_service.py 内联的 _mask_string 语义（原 util.mask_string 源未归档，
/// 以该内联同构实现为准：前 N 位 + *** + 后 N 位，长度不足返回 ***）。
/// </summary>
internal sealed class SecretMasker : ISecretMasker
{
    // 对应 _mask_data 敏感键清单（bilibili_api.py:42）
    private static readonly string[] DataSensitiveKeys =
    [
        "rtmp", "addr", "code", "key", "token", "csrf", "csrf_token", "access_key",
        "live_key", "sub_session_key", "url", "qrcode_key", "refresh_token",
        "b_3", "b_4", "room_id", "uid",
    ];

    // 对应 _mask_url 敏感查询参数清单（bilibili_api.py:68）
    private static readonly string[] UrlSensitiveKeys =
    [
        "uid", "room_id", "key", "token", "csrf", "csrf_token", "access_key", "qrcode_key",
    ];

    public string MaskString(string? value, int visibleStart = 4, int visibleEnd = 4)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= visibleStart + visibleEnd)
            return "***";
        return string.Concat(value.AsSpan(0, visibleStart), "***", value.AsSpan(value.Length - visibleEnd));
    }

    public string MaskUrl(string url)
    {
        if (string.IsNullOrEmpty(url) || !url.Contains('?'))
            return url;
        try
        {
            var uri = new Uri(url);
            var query = uri.Query.TrimStart('?');
            if (query.Length == 0) return url;

            var changed = false;
            var pairs = query.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(pair =>
            {
                var idx = pair.IndexOf('=');
                if (idx <= 0) return pair;
                var k = pair[..idx];
                if (!UrlSensitiveKeys.Contains(k, StringComparer.Ordinal)) return pair;
                changed = true;
                var v = pair[(idx + 1)..];
                return $"{k}={MaskString(Uri.UnescapeDataString(v), 2, 2)}";
            }).ToList();

            if (!changed) return url;
            var builder = new UriBuilder(uri) { Query = string.Join("&", pairs) };
            return builder.Uri.AbsoluteUri;
        }
        catch
        {
            return url;
        }
    }

    public string MaskData(string json)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return "Parse Error";
        }

        return node is null ? "null" : Walk(node).ToJsonString();
    }

    private JsonNode Walk(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var result = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    if (value is null)
                    {
                        result[key] = null;
                        continue;
                    }

                    if (DataSensitiveKeys.Contains(key, StringComparer.Ordinal))
                    {
                        // 与原实现一致：敏感键下仅字符串/数字掩码、对象递归，其余原样保留
                        result[key] = value switch
                        {
                            JsonValue v when v.TryGetValue<string>(out var s) =>
                                JsonValue.Create(MaskString(s, 4, 4)),
                            JsonValue v when v.TryGetValue<long>(out var n) =>
                                JsonValue.Create(MaskString(n.ToString(System.Globalization.CultureInfo.InvariantCulture), 2, 2)),
                            JsonValue v when v.TryGetValue<double>(out var d) =>
                                JsonValue.Create(MaskString(d.ToString(System.Globalization.CultureInfo.InvariantCulture), 2, 2)),
                            JsonObject or JsonArray => Walk(value),
                            _ => value.DeepClone(),
                        };
                    }
                    else
                    {
                        result[key] = value is JsonObject or JsonArray ? Walk(value) : value.DeepClone();
                    }
                }
                return result;
            }
            case JsonArray arr:
            {
                var result = new JsonArray();
                foreach (var item in arr)
                    result.Add(item is null ? null : Walk(item));
                return result;
            }
            default:
                return node.DeepClone();
        }
    }
}
