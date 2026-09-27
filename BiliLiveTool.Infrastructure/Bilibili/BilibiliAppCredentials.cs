namespace BiliLiveTool.Infrastructure.Bilibili;

/// <summary>
/// B站直播姬 App Key 凭证类（只读）。
/// 对应原 bilibili_api.py:17-18，值不得出现在其他任何位置。
/// </summary>
public static class BilibiliAppCredentials
{
    public static string AppKey => "aae92bc66f3edfab";

    internal static string AppSecret => "af125a0d5279fd576c1b4418a3e8276d";
}
