using Dalamud.Configuration;

namespace PluginJPHelper;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;
    public int CaptureSchemaVersion { get; set; } = 0;
    public int DataResetVersion { get; set; } = 0;
    public bool CleanSlateMode { get; set; } = false;
    public bool CaptureAutoImport { get; set; } = true;
    public string LastAcknowledgedOfficialNotice { get; set; } = string.Empty;
    public string LastAcknowledgedOfficialNoticeSha { get; set; } = string.Empty;
    // 旧GitHub投稿方式の設定値。互換性のため残すが、新方式では使用しない。
    public string CommunityGitHubUserName { get; set; } = string.Empty;
    public string CommunityPosterName { get; set; } = string.Empty;
    public string LastAcknowledgedCommunityIndexSha { get; set; } = string.Empty;
    public Dictionary<string, PluginDictionaryState> Plugins { get; set; } = new(StringComparer.Ordinal);
    public void EnsurePlugins()
    {
        Plugins ??= new Dictionary<string, PluginDictionaryState>(StringComparer.Ordinal);
        foreach (var existingState in Plugins.Values)
            if (existingState != null)
            {
                existingState.LastCsvPath ??= string.Empty;
                existingState.OpenCommand ??= string.Empty;
                existingState.OfficialOverrides ??= new Dictionary<string, string>(StringComparer.Ordinal);
            }
        var migrateTranslationTargets = Version < 2;
        if (migrateTranslationTargets)
        {
            foreach (var existing in Plugins.Values)
                if (existing != null) existing.TranslationTarget = true;
            Version = 2;
        }
        // v0.3.1: Artisan はメイン画面とは別名の List Editor / Processing List を使用する。
        // 既に登録済みの設定にも不足キーワードだけを補完し、ユーザー設定は消さない。
        foreach (var (pluginName, artisanState) in Plugins)
        {
            if (artisanState == null || !string.Equals(pluginName, "Artisan", StringComparison.OrdinalIgnoreCase)) continue;

            var keywords = (artisanState.WindowKeyword ?? string.Empty)
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            foreach (var required in new[] { "Artisan", "List Editor", "Processing List" })
                if (!keywords.Contains(required, StringComparer.OrdinalIgnoreCase)) keywords.Add(required);
            artisanState.WindowKeyword = string.Join("|", keywords);
        }

        foreach (var name in new[] { "RSR", "BMR", "BM" })
        {
            if (!Plugins.TryGetValue(name, out var state) || state == null)
            {
                state = new PluginDictionaryState { Enabled = name == "RSR", TranslationTarget = true, WindowKeyword = name switch { "RSR" => "Rotation Solver", "BMR" => "BossModReborn", "BM" => "BossMod", _ => string.Empty } };
                Plugins[name] = state;
            }
            state.UserOverrides ??= new Dictionary<string, string>(StringComparer.Ordinal);
            state.OfficialOverrides ??= new Dictionary<string, string>(StringComparer.Ordinal);
            state.Locations ??= new Dictionary<string, DictionaryLocation>(StringComparer.Ordinal);
            state.DeletedKeys ??= new HashSet<string>(StringComparer.Ordinal);
        }
    }
}

public sealed class PluginDictionaryState
{
    public bool Enabled { get; set; }
    public bool TranslationTarget { get; set; }
    public string WindowKeyword { get; set; } = string.Empty;
    public string LastCsvPath { get; set; } = string.Empty;
    public string OpenCommand { get; set; } = string.Empty;
    public Dictionary<string, string> UserOverrides { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> OfficialOverrides { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, DictionaryLocation> Locations { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> DeletedKeys { get; set; } = new(StringComparer.Ordinal);
}

public sealed class DictionaryLocation : IEquatable<DictionaryLocation>
{
    public string Menu { get; set; } = string.Empty;
    public string Section { get; set; } = string.Empty;

    public bool Equals(DictionaryLocation? other)
        => other != null && string.Equals(Menu, other.Menu, StringComparison.Ordinal) && string.Equals(Section, other.Section, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is DictionaryLocation other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Menu, Section);
}
