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

    // v0.4.20: 文字化け判定ルールとホワイトリストをユーザー側で調整できる設定。
    public List<MojibakeMarkerRule> MojibakeMarkerRules { get; set; } = new()
    {
        new MojibakeMarkerRule { Text = "�", MinimumConsecutive = 1, Enabled = true, Label = "置換文字" },
        new MojibakeMarkerRule { Text = "□", MinimumConsecutive = 3, Enabled = true, Label = "白四角" },
        new MojibakeMarkerRule { Text = "ï¿½", MinimumConsecutive = 1, Enabled = true, Label = "UTF-8誤変換パターン" },
        new MojibakeMarkerRule { Text = "â€", MinimumConsecutive = 1, Enabled = true, Label = "引用符系誤変換パターン" },
        new MojibakeMarkerRule { Text = "Ãƒ", MinimumConsecutive = 1, Enabled = true, Label = "二重エンコード系パターン" },
    };
    public List<MojibakeWhitelistRule> MojibakeWhitelistRules { get; set; } = new();
    public int MojibakeRulesSchemaVersion { get; set; } = 0;
    public bool MojibakeDetectControlCharacters { get; set; } = true;
    // 旧設定との互換性のため残す。v0.4.20以降、既知パターンは MojibakeMarkerRules に表示・保存する。
    public bool MojibakeDetectKnownEncodingPatterns { get; set; } = true;

    // v0.4.25: Plugin Installerで文字化けを自動復元した履歴。
    // ユーザーが後から「どのプラグインのどの項目を原文へ戻したか」を確認できるよう保持する。
    public List<InstallerMojibakeRepairRecord> InstallerMojibakeRepairHistory { get; set; } = new();

    public void EnsurePlugins()
    {
        Plugins ??= new Dictionary<string, PluginDictionaryState>(StringComparer.Ordinal);
        MojibakeMarkerRules ??= new List<MojibakeMarkerRule>();
        MojibakeWhitelistRules ??= new List<MojibakeWhitelistRule>();
        InstallerMojibakeRepairHistory ??= new List<InstallerMojibakeRepairRecord>();

        DeduplicateMojibakeRules();
        DeduplicateInstallerMojibakeRepairHistory();

        // v0.4.20: v0.4.19以前の設定から、画面に見えていなかった既知パターンを
        // 可視・編集可能な通常ルールへ「一度だけ」補完する。補完後にユーザーが削除した
        // ルールを次回起動で勝手に復活させない。
        if (MojibakeRulesSchemaVersion < 1)
        {
            EnsureMojibakeRule("�", 1, "置換文字", true);
            EnsureMojibakeRule("□", 3, "白四角", true);
            EnsureMojibakeRule("ï¿½", 1, "UTF-8誤変換パターン", MojibakeDetectKnownEncodingPatterns);
            EnsureMojibakeRule("â€", 1, "引用符系誤変換パターン", MojibakeDetectKnownEncodingPatterns);
            EnsureMojibakeRule("Ãƒ", 1, "二重エンコード系パターン", MojibakeDetectKnownEncodingPatterns);
            MojibakeRulesSchemaVersion = 1;
        }
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

        void EnsureMojibakeRule(string text, int minimum, string label, bool enabled)
        {
            var existing = MojibakeMarkerRules.FirstOrDefault(x => x != null && string.Equals(x.Text, text, StringComparison.Ordinal));
            if (existing != null)
            {
                if (string.IsNullOrWhiteSpace(existing.Label)) existing.Label = label;
                return;
            }
            MojibakeMarkerRules.Add(new MojibakeMarkerRule
            {
                Text = text,
                MinimumConsecutive = minimum,
                Enabled = enabled,
                Label = label,
            });
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

    public bool DeduplicateInstallerMojibakeRepairHistory()
    {
        var changed = false;
        InstallerMojibakeRepairHistory ??= new List<InstallerMojibakeRepairRecord>();

        // 同一内容は履歴へ1件だけ残す。最初に検知した記録を保持し、
        // PJH再起動後に同じ文字化けを再検知しても件数を増やさない。
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < InstallerMojibakeRepairHistory.Count;)
        {
            var item = InstallerMojibakeRepairHistory[i];
            if (item == null)
            {
                InstallerMojibakeRepairHistory.RemoveAt(i);
                changed = true;
                continue;
            }

            var key = string.Join("\u001F",
                item.PluginName ?? string.Empty,
                item.FieldName ?? string.Empty,
                item.Reason ?? string.Empty,
                item.DetectedText ?? string.Empty,
                item.RestoredSourceText ?? string.Empty);

            if (!seen.Add(key))
            {
                InstallerMojibakeRepairHistory.RemoveAt(i);
                changed = true;
                continue;
            }

            i++;
        }

        return changed;
    }

    public bool DeduplicateMojibakeRules()
    {
        var changed = false;

        MojibakeMarkerRules ??= new List<MojibakeMarkerRule>();
        var seenRules = new HashSet<string>(StringComparer.Ordinal);
        for (var i = MojibakeMarkerRules.Count - 1; i >= 0; i--)
        {
            var rule = MojibakeMarkerRules[i];
            if (rule == null || string.IsNullOrEmpty(rule.Text))
            {
                MojibakeMarkerRules.RemoveAt(i);
                changed = true;
                continue;
            }

            // 同じ判定文字列は1ルールだけ。後から同じものを追加・移行しない。
            if (!seenRules.Add(rule.Text))
            {
                MojibakeMarkerRules.RemoveAt(i);
                changed = true;
            }
        }

        // 上の逆順走査は末尾側を残すため、元の並びを保つよう最後に逆転させない。
        // 既存設定では最新側の値（回数・名前・有効状態）を優先する。

        MojibakeWhitelistRules ??= new List<MojibakeWhitelistRule>();
        var seenWhitelist = new HashSet<string>(StringComparer.Ordinal);
        for (var i = MojibakeWhitelistRules.Count - 1; i >= 0; i--)
        {
            var rule = MojibakeWhitelistRules[i];
            if (rule == null || string.IsNullOrEmpty(rule.Text))
            {
                MojibakeWhitelistRules.RemoveAt(i);
                changed = true;
                continue;
            }

            var key = (rule.PartialMatch ? "P\u001F" : "E\u001F") + rule.Text;
            if (!seenWhitelist.Add(key))
            {
                MojibakeWhitelistRules.RemoveAt(i);
                changed = true;
            }
        }

        return changed;
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


public sealed class MojibakeMarkerRule
{
    public string Text { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int MinimumConsecutive { get; set; } = 1;
    public bool Enabled { get; set; } = true;
}

public sealed class MojibakeWhitelistRule
{
    public string Text { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public bool PartialMatch { get; set; } = false;
}


public sealed class InstallerMojibakeRepairRecord
{
    public DateTimeOffset RepairedAt { get; set; } = DateTimeOffset.Now;
    public string PluginName { get; set; } = string.Empty;
    public string FieldName { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string DetectedText { get; set; } = string.Empty;
    public string RestoredSourceText { get; set; } = string.Empty;
}
