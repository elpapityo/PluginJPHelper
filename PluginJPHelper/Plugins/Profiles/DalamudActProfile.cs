namespace PluginJPHelper.Plugins.Profiles;

internal sealed class DalamudActProfile : IPluginProfile
{
    public const string PluginName = "DalamudACT";

    // 実機で確認できた DalamudACT の独立ウィンドウ名。
    // TODO: SettingsWindow は他プラグインでも使われる一般名のため誤爆しうる。
    //       ここは所有判定の挙動を変えないまま切り出しただけなので、
    //       絞り込みは別途対応する。
    private static readonly string[] WindowKeywords =
    [
        "DalamudACT",
        "CombatTimelineWindow",
        "StatusObserverWindow",
        "PartyMonitorWindow",
        "StatsPanelWindow",
        "SkillMonitorWindow",
        "SettingsWindow",
    ];

    public string Name => PluginName;

    public IReadOnlyList<string> Aliases => [];

    public int SortKey => 10;

    public string DefaultWindowKeyword => PluginName;

    bool IPluginProfile.MatchesWindow(string windowName) => MatchesWindow(windowName);

    public static bool MatchesPluginName(string? name)
        => string.Equals(name, PluginName, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesWindow(string windowName)
    {
        foreach (var keyword in WindowKeywords)
            if (windowName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }
}
