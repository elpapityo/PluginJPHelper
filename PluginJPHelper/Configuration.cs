using Dalamud.Configuration;
using PluginJPHelper.Plugins.Behaviors;
using PluginJPHelper.Plugins.Profiles;

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
            if (artisanState == null || !ArtisanBehavior.MatchesPluginName(pluginName)) continue;

            var keywords = (artisanState.WindowKeyword ?? string.Empty)
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            ArtisanBehavior.EnsureWindowKeywords(keywords);
            artisanState.WindowKeyword = string.Join("|", keywords);
        }

        foreach (var name in new[] { RsrProfile.PluginName, BossModRebornProfile.PluginName, BossModProfile.PluginName })
        {
            if (!Plugins.TryGetValue(name, out var state) || state == null)
            {
                state = new PluginDictionaryState
                {
                    Enabled = name == RsrProfile.PluginName,
                    TranslationTarget = true,
                    WindowKeyword = PluginProfileRegistry.Find(name)?.DefaultWindowKeyword ?? string.Empty,
                };
                Plugins[name] = state;
            }
            state.UserOverrides ??= new Dictionary<string, string>(StringComparer.Ordinal);
            state.OfficialOverrides ??= new Dictionary<string, string>(StringComparer.Ordinal);
            state.Locations ??= new Dictionary<string, DictionaryLocation>(StringComparer.Ordinal);
            state.DeletedKeys ??= new HashSet<string>(StringComparer.Ordinal);
            state.DictionaryWindowKeywordSources ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            state.SuppressedDictionaryWindowKeywords ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}

public sealed class PluginDictionaryState
{
    public bool Enabled { get; set; }
    public bool TranslationTarget { get; set; }
    public string WindowKeyword { get; set; } = string.Empty;

    // 辞書ファイルに埋め込まれた別ウィンドウ関連付け。
    // Key=WindowKeyword / Value=由来（公式辞書・コミュニティ辞書・CSV等）。
    public Dictionary<string, string> DictionaryWindowKeywordSources { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // 辞書由来の関連付けをユーザーが手動で削除した場合、
    // 辞書再読込のたびに勝手に復活させないための抑止リスト。
    public HashSet<string> SuppressedDictionaryWindowKeywords { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string LastCsvPath { get; set; } = string.Empty;
    public string OpenCommand { get; set; } = string.Empty;
    public Dictionary<string, string> UserOverrides { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> OfficialOverrides { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, DictionaryLocation> Locations { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> DeletedKeys { get; set; } = new(StringComparer.Ordinal);
}

// record が生成する Equals は EqualityComparer<string>.Default を使う。
// これは string の序数比較なので、手書きしていた StringComparison.Ordinal と同じ。
// 設定ファイルへは従来どおり Menu / Section の 2 プロパティとして保存される。
public sealed record DictionaryLocation
{
    public string Menu { get; set; } = string.Empty;
    public string Section { get; set; } = string.Empty;
}
