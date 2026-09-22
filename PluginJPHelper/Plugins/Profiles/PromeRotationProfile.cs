namespace PluginJPHelper.Plugins.Profiles;

/// <summary>
/// v0.4.9: PureTimeline系の編集画面は
/// ウィンドウ名にプラグイン名を含まず、中国語タイトルを使う。
/// 実機で確認できた名前だけを所有判定へ追加する。
/// </summary>
internal sealed class PromeRotationProfile : IPluginProfile
{
    public const string PluginName = "PromeRotation";

    private static readonly string[] IgnoreCaseKeywords =
    [
        "PromeRotation",
        "PureTimeline",
    ];

    private static readonly string[] OrdinalKeywords =
    [
        "时间轴编辑器",
        "触发轴编辑器",
    ];

    // v0.4.9: PureTimeline系の未保存確認は独立したモーダルウィンドウとして描画され、
    // 親所有者を継承しない。実機で確認できたタイトル語だけを PromeRotation 所有として扱う。
    private const string UnsavedKeyword = "未保存";

    private static readonly string[] UnsavedCompanionKeywords =
    [
        "放弃",
        "修改",
    ];

    public string Name => PluginName;

    public IReadOnlyList<string> Aliases => [];

    public int SortKey => 10;

    public string DefaultWindowKeyword => PluginName;

    public bool MatchesWindow(string windowName)
    {
        foreach (var keyword in IgnoreCaseKeywords)
            if (windowName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;

        foreach (var keyword in OrdinalKeywords)
            if (windowName.Contains(keyword, StringComparison.Ordinal))
                return true;

        if (!windowName.Contains(UnsavedKeyword, StringComparison.Ordinal)) return false;

        foreach (var keyword in UnsavedCompanionKeywords)
            if (windowName.Contains(keyword, StringComparison.Ordinal))
                return true;

        return false;
    }
}
