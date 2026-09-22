namespace PluginJPHelper.Data;

internal static class RsrNavigationVocabulary
{
    public static readonly HashSet<string> FixedMenus = new(StringComparer.Ordinal)
    {
        "Main", "Actions", "List", "Basic", "UI", "Auto", "Target", "Duty", "Extra", "Debug", "AutoDuty"
    };

    public static readonly HashSet<string> JobMenus = new(StringComparer.Ordinal)
    {
        "PLD", "WAR", "DRK", "GNB", "WHM", "SCH", "AST", "SGE", "MNK", "DRG", "NIN", "SAM", "RPR", "VPR",
        "BRD", "MCH", "DNC", "BLM", "SMN", "RDM", "PCT", "BLU"
    };

    // RSRソースの CollapsingHeaderGroup と実機ログで確認できた画面内項目。
    // ここに無いSelectableを意味だけで「項目」と推測しない。
    public static readonly HashSet<string> KnownSections = new(StringComparer.Ordinal)
    {
        "Timer", "Others", "Information", "Windows",
        "Auto Switch", "Action Usage and Control", "Healing Usage and Control",
        "Configuration", "Hostile",
        "Ultimate", "Savage", "Extreme", "Chaotic Alliance Raid", "Alliance Raid", "Dungeon", "Deep Dungeon",
        "Variant Dungeon", "Treasure Dungeon", "Field Ops", "PvP", "The Masked Carnivale", "Crucible of the Unbroken",
        "Event", "Internal",
        "Preset", "Statuses", "Map-specific settings", "Compatibility", "Links", "Many thanks to Ko-fi sponsors.",
        "Action and Setting Macros", "State Macros"
    };
}
