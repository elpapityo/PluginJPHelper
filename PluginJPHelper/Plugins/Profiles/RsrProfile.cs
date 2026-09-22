namespace PluginJPHelper.Plugins.Profiles;

using Data;

internal sealed class RsrProfile : IPluginProfile
{
    public const string PluginName = "RSR";

    private const string SideBarWindowKeyword = "Rotation Solver Side bar";

    private static readonly string[] WindowKeywords =
    [
        "Rotation Solver Reborn",
        "RotationSolverReborn",
        "Rotation Solver",
    ];

    public string Name => PluginName;

    public IReadOnlyList<string> Aliases => [];

    public int SortKey => 0;

    public string DefaultWindowKeyword => "Rotation Solver";

    bool IPluginProfile.MatchesWindow(string windowName) => MatchesWindow(windowName);

    public static bool MatchesPluginName(string? name)
        => string.Equals(name, PluginName, StringComparison.Ordinal);

    public static bool MatchesWindow(string windowName)
    {
        foreach (var keyword in WindowKeywords)
            if (windowName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    /// <summary>
    /// RSRサイドバーは「現在メニュー判定専用」。本文辞書へは登録しない。
    /// </summary>
    public static bool IsSideBarWindow(string capturePlugin, string windowName)
        => MatchesPluginName(capturePlugin)
           && windowName.Contains(SideBarWindowKeyword, StringComparison.OrdinalIgnoreCase);

    public static bool IsNavigationMenu(string visible)
        => RsrNavigationVocabulary.FixedMenus.Contains(visible)
           || RsrNavigationVocabulary.JobMenus.Contains(visible)
           || visible.StartsWith("Duty - ", StringComparison.Ordinal);

    public static bool IsKnownSection(string visible)
        => RsrNavigationVocabulary.KnownSections.Contains(visible);
}
