using System.Security.Cryptography;
using System.Text;

namespace BiliLiveTool.Infrastructure.Bilibili;

/// <summary>
/// APP 签名（冻结项）：加 appkey → 按 key 字典序排序 → urlencode → md5(query + APP_SEC) → 追加 sign。
/// 逐行对照 .refs/source-refs/bilibili_api.py 的 _appsign。
/// </summary>
internal sealed class AppSigner
{
    public Dictionary<string, string> Sign(IDictionary<string, string> input)
    {
        var map = new SortedDictionary<string, string>(input, StringComparer.Ordinal);
        map["appkey"] = BilibiliAppCredentials.AppKey;
        var query = string.Join("&", map.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        var sign = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(query + BilibiliAppCredentials.AppSecret)));
        map["sign"] = sign;
        return new Dictionary<string, string>(map);
    }
}
