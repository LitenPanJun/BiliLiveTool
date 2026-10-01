using System.Text.Json;
using System.Text.Json.Nodes;
using BiliLiveTool.Core.Security;

namespace BiliLiveTool.Infrastructure.Bilibili;

/// <summary>
/// 日志脱敏实现，复刻原 bilibili_api.py 的 _mask_data / _mask_url，
/// 掩码核心为 util.mask_string：空值返回空串、长度不足按位补 *、
/// 否则前 N 位 + 5 个 * + 后 N 位。
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

    // 对应 util.mask_string：空值返回空串；长度不足按位补 *；否则前 N + 5 星 + 后 N
    public string MaskString(string? value, int visibleStart = 4, int visibleEnd = 4)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.Length <= visibleStart + visibleEnd) return new string('*', value.Length);
        return string.Concat(
            value.AsSpan(0, visibleStart),
            "*****",
            value.AsSpan(value.Length - visibleEnd));
    }

    public string MaskUrl(string url)
    {
        if (string.IsNullOrEmpty(url) || !url.Contains('?'))
            return url;
        try
        {
            // 复刻 urlparse → parse_qs → urlencode(doseq=True)：
            // 仅替换 query，scheme/path/fragment 原样保留
            var hashIdx = url.IndexOf('#');
            var head = hashIdx < 0 ? url : url[..hashIdx];
            var fragment = hashIdx < 0 ? string.Empty : url[hashIdx..];
            var queryIdx = head.IndexOf('?');
            var query = head[(queryIdx + 1)..];
            if (query.Length == 0) return url;

            var changed = false;
            var rebuilt = new List<string>();
            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var idx = pair.IndexOf('=');
                if (idx <= 0) continue; // parse_qs 不收无 '=' 或空键项
                var key = Uri.UnescapeDataString(pair[..idx]);
                var value = Uri.UnescapeDataString(pair[(idx + 1)..]);
                if (value.Length == 0) continue; // keep_blank_values=False：空值丢弃

                if (UrlSensitiveKeys.Contains(key, StringComparer.Ordinal))
                {
                    value = MaskString(value, 2, 2);
                    changed = true;
                }
                rebuilt.Add($"{QuotePlus(key)}={QuotePlus(value)}");
            }

            // 无敏感键命中时原串返回（含被 parse_qs 丢弃的项也不影响结果）
            if (!changed) return url;
            return $"{head[..queryIdx]}?{string.Join('&', rebuilt)}{fragment}";
        }
        catch
        {
            return url;
        }
    }

    // 复刻 Python quote_plus：EscapeDataString 的保留集与 quote 一致，另把 %20 归一为 +
    private static string QuotePlus(string value) =>
        Uri.EscapeDataString(value).Replace("%20", "+", StringComparison.Ordinal);

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
