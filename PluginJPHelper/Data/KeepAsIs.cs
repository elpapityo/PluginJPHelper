namespace PluginJPHelper.Data;

internal static class KeepAsIs
{
    public static readonly HashSet<string> Labels = new(StringComparer.Ordinal)
    {
        "DNC", "BRD", "MCH", "BLM", "SMN", "RDM", "PCT", "MNK", "DRG", "NIN", "SAM", "RPR", "VPR",
        "AutoDuty", "BossMod", "BossMod Reborn", "Wrath Combo", "XIV Combo", "XIV Combo Expanded", "XIVSlothCombo",
        "ReAction", "ReActionEX", "Redirect", "Olympus", "Reborn"
    };
}
