namespace PluginJPHelper.Plugins.Profiles;

internal sealed class BossModProfile : IPluginProfile
{
    public const string PluginName = "BM";

    // BossMod Reborn と区別するため、Reborn を含む窓は除外する。
    private const string RebornKeyword = "Reborn";

    private static readonly string[] WindowKeywords =
    [
        "BossMod",
        "Boss Mod",
    ];

    public string Name => PluginName;

    public IReadOnlyList<string> Aliases => [];

    public int SortKey => 2;

    public string DefaultWindowKeyword => "BossMod";

    public bool MatchesWindow(string windowName)
    {
        if (windowName.Contains(RebornKeyword, StringComparison.OrdinalIgnoreCase)) return false;

        foreach (var keyword in WindowKeywords)
            if (windowName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }
}
