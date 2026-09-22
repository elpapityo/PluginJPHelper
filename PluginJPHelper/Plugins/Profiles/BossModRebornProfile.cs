namespace PluginJPHelper.Plugins.Profiles;

internal sealed class BossModRebornProfile : IPluginProfile
{
    public const string PluginName = "BMR";

    private static readonly string[] WindowKeywords =
    [
        "BossMod Reborn",
        "Boss Mod Reborn",
        "BossModReborn",
    ];

    public string Name => PluginName;

    public IReadOnlyList<string> Aliases => [];

    public int SortKey => 1;

    public string DefaultWindowKeyword => "BossModReborn";

    public bool MatchesWindow(string windowName)
    {
        foreach (var keyword in WindowKeywords)
            if (windowName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }
}
