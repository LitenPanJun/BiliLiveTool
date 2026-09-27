using System.Security.Cryptography;
using System.Text;

namespace BiliLiveTool.Infrastructure.Bilibili;

/// <summary>
/// WBI 签名（冻结项）：mixinKeyEncTab 64 项打乱表、getMixinKey 取前 32 字符、
/// 加 wts → 按 key 排序 → 过滤值中 !'()* → urlencode → md5(query + mixin) 得 w_rid。
/// 逐行对照 .refs/source-refs/get_wbi.py 的 encWbi/getMixinKey。
/// </summary>
internal sealed class WbiSigner
{
    private static readonly int[] MixinKeyEncTab =
    [
        46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35, 27, 43, 5, 49,
        33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13, 37, 48, 7, 16, 24, 55, 40,
        61, 26, 17, 0, 1, 60, 51, 30, 4, 22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11,
        36, 20, 34, 44, 52,
    ];

    public static string GetMixinKey(string orig)
    {
        var sb = new StringBuilder();
        foreach (var i in MixinKeyEncTab) sb.Append(orig[i]);
        return sb.ToString()[..32];
    }

    /// <param name="wts">固定时间戳供金丝雀测试注入；生产调用留空取当前时间。</param>
    public Dictionary<string, string> Sign(IDictionary<string, string> input, string imgKey, string subKey, long? wts = null)
    {
        var mixin = GetMixinKey(imgKey + subKey);
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in input)
            map[kv.Key] = new string(kv.Value.Where(c => !"!'()*".Contains(c)).ToArray());
        map["wts"] = (wts ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString();
        var query = string.Join("&", map.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        map["w_rid"] = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(query + mixin)));
        return new Dictionary<string, string>(map);
    }
}
