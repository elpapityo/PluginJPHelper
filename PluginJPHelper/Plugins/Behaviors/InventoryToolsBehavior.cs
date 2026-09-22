namespace PluginJPHelper.Plugins.Behaviors;

/// <summary>
/// InventoryTools (Allagan Tools) 固有の処理。
/// 所有判定は設定の WindowKeyword をユーザーが編集できるため、
/// あえてプロファイル化せずフォールバック側に残している。
/// </summary>
internal static class InventoryToolsBehavior
{
    public const string PluginName = "InventoryTools";

    /// <summary>設定画面のウィンドウ名。プラグイン名を含まないため決め打ちで判定する。</summary>
    private const string ConfigurationWindowName = "Configuration";

    /// <summary>設定画面の所有者を確定させるメニュー名。</summary>
    private const string ConfigurationOwnerMenu = "Wizard";

    public static bool MatchesPluginName(string? name)
        => string.Equals(name, PluginName, StringComparison.OrdinalIgnoreCase);

    public static bool IsConfigurationWindow(string? windowName)
        => string.Equals(windowName, ConfigurationWindowName, StringComparison.Ordinal);

    public static bool IsConfigurationOwnerMenu(string? menuLabel)
        => string.Equals(menuLabel, ConfigurationOwnerMenu, StringComparison.Ordinal);

    /// <summary>
    /// 末尾が動的に変化する固定接頭辞を翻訳する。
    /// 完全一致辞書で拾えないものだけをここで扱う。
    /// </summary>
    public static bool TryTranslateDynamic(string pluginName, string source, out string translated)
    {
        translated = string.Empty;
        if (!MatchesPluginName(pluginName)) return false;

        const string sourcePrefix = "Can the item be sourced via ";
        const string sourceMiddle = "?\n\nIt includes these sources: ";
        const string usePrefix = "Can the item be used for ";
        const string useMiddle = "?\n\nIt includes these uses: ";
        const string nextAutosavePrefix = "Next Autosave: ";

        if (source.StartsWith(nextAutosavePrefix, StringComparison.Ordinal))
        {
            translated = "次回自動保存：" + source[nextAutosavePrefix.Length..];
            return true;
        }

        if (source.StartsWith(sourcePrefix, StringComparison.Ordinal))
        {
            var middle = source.IndexOf(sourceMiddle, sourcePrefix.Length, StringComparison.Ordinal);
            if (middle > sourcePrefix.Length)
            {
                var category = source.Substring(sourcePrefix.Length, middle - sourcePrefix.Length);
                var list = source[(middle + sourceMiddle.Length)..];
                translated = $"{CategoryJa(category)}で入手できるアイテムか？\n\n対象となる入手元：{list}";
                return true;
            }
        }

        if (source.StartsWith(usePrefix, StringComparison.Ordinal))
        {
            var middle = source.IndexOf(useMiddle, usePrefix.Length, StringComparison.Ordinal);
            if (middle > usePrefix.Length)
            {
                var category = source.Substring(usePrefix.Length, middle - usePrefix.Length);
                var list = source[(middle + useMiddle.Length)..];
                translated = $"{CategoryJa(category)}に使用するアイテムか？\n\n対象となる用途：{list}";
                return true;
            }
        }

        return false;
    }

    private static string CategoryJa(string value) => value.Trim().ToLowerInvariant() switch
    {
        "botany" => "園芸",
        "crafting" => "製作",
        "deep dungeon" => "ディープダンジョン",
        "duties" => "コンテンツ",
        "field operation" => "特殊フィールド探索",
        "fishing" => "釣り",
        "gathering" => "採集",
        "gathering (ephemeral)" => "刻限の採集",
        "gathering (hidden)" => "未知の採集",
        "gathering (timed)" => "時間限定の採集",
        "mining" => "採掘",
        "venture" => "リテイナーベンチャー",
        "venture (exploration)" => "探索依頼",
        "leves" => "リーヴ",
        "shops" => "ショップ",
        "housing" => "ハウジング",
        "relic weapon" => "武器強化コンテンツ",
        "relic tool" => "道具強化コンテンツ",
        _ => value.Trim()
    };
}
