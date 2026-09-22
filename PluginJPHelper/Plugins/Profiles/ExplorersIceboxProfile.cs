namespace PluginJPHelper.Plugins.Profiles;

/// <summary>
/// Explorer's Icebox。設定キーとして ExplorersIcebox と ICE の両方が使われてきたため、
/// 同じプロファイルを両方の名前で引けるようにしている。
/// </summary>
internal sealed class ExplorersIceboxProfile : IPluginProfile
{
    public const string PluginName = "ExplorersIcebox";
    public const string ShortName = "ICE";

    private static readonly string[] WindowKeywords =
    [
        "Explorer's Icebox",
        "ExplorersIceboxMainWindow",
    ];

    public string Name => PluginName;

    public IReadOnlyList<string> Aliases => [ShortName];

    public int SortKey => 10;

    public string DefaultWindowKeyword => PluginName;

    public bool MatchesWindow(string windowName)
    {
        foreach (var keyword in WindowKeywords)
            if (windowName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }
}
