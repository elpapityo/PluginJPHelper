namespace PluginJPHelper.Plugins.Profiles;

internal static class PluginProfileRegistry
{
    private const int DefaultSortKey = 10;

    private static readonly IPluginProfile[] Profiles =
    [
        new RsrProfile(),
        new BossModRebornProfile(),
        new BossModProfile(),
        new DalamudActProfile(),
        new PromeRotationProfile(),
        new ExplorersIceboxProfile(),
    ];

    private static readonly Dictionary<string, IPluginProfile> ByName = BuildIndex();

    public static IReadOnlyList<IPluginProfile> All => Profiles;

    public static IPluginProfile? Find(string pluginName)
        => ByName.GetValueOrDefault(pluginName);

    public static int SortKey(string pluginName)
        => Find(pluginName)?.SortKey ?? DefaultSortKey;

    /// <summary>
    /// 登録済みプロファイルがあればそれで判定し、無ければ設定の WindowKeyword で判定する。
    ///
    /// この経路は翻訳フックから「登録プラグイン数 × 描画文字列数 × 毎フレーム」で呼ばれる。
    /// 未登録プラグインのたびにインスタンスを作らないよう、フォールバックは静的メソッドにしている。
    /// </summary>
    public static bool MatchesWindow(string pluginName, string windowName, string? customWindowKeyword)
    {
        var profile = Find(pluginName);
        return profile != null
            ? profile.MatchesWindow(windowName)
            : MatchesCustomKeywords(pluginName, windowName, customWindowKeyword);
    }

    private static bool MatchesCustomKeywords(string pluginName, string windowName, string? customWindowKeyword)
    {
        // 通常のプラグインは、まずプラグイン名そのものを含むWindowを本体Windowとして扱う。
        // 例: "Pawprint###BeastmasterMain"
        if (windowName.Contains(pluginName, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.IsNullOrWhiteSpace(customWindowKeyword)) return false;

        foreach (var keyword in customWindowKeyword.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (windowName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    private static Dictionary<string, IPluginProfile> BuildIndex()
    {
        var index = new Dictionary<string, IPluginProfile>(StringComparer.Ordinal);
        foreach (var profile in Profiles)
        {
            index[profile.Name] = profile;
            foreach (var alias in profile.Aliases)
                index[alias] = profile;
        }

        return index;
    }
}
