using System.Text;

namespace PluginJPHelper;

internal enum MojibakeTargetKind
{
    Plugin,
    PluginInstaller,
}

internal sealed record MojibakeIncident(
    MojibakeTargetKind TargetKind,
    string TargetName,
    string FieldName,
    string SourceText,
    string TranslatedText,
    string Reason,
    string DictionarySource)
{
    internal string DedupeKey => string.Join("\u001f", TargetKind, TargetName, FieldName, SourceText, TranslatedText);
}

internal static class MojibakeSafety
{
    private static readonly object Sync = new();
    private static List<MojibakeMarkerRule> markerRules = new()
    {
        new MojibakeMarkerRule { Text = "�", MinimumConsecutive = 1, Enabled = true, Label = "置換文字" },
        new MojibakeMarkerRule { Text = "□", MinimumConsecutive = 3, Enabled = true, Label = "白四角" },
        new MojibakeMarkerRule { Text = "ï¿½", MinimumConsecutive = 1, Enabled = true, Label = "UTF-8誤変換パターン" },
        new MojibakeMarkerRule { Text = "â€", MinimumConsecutive = 1, Enabled = true, Label = "引用符系誤変換パターン" },
        new MojibakeMarkerRule { Text = "Ãƒ", MinimumConsecutive = 1, Enabled = true, Label = "二重エンコード系パターン" },
    };
    private static List<MojibakeWhitelistRule> whitelistRules = new();
    private static bool detectControlCharacters = true;

    internal static void Configure(Configuration config)
    {
        lock (Sync)
        {
            markerRules = (config.MojibakeMarkerRules ?? new List<MojibakeMarkerRule>())
                .Where(x => x != null)
                .Select(x => new MojibakeMarkerRule
                {
                    Text = x.Text ?? string.Empty,
                    Label = x.Label ?? string.Empty,
                    MinimumConsecutive = Math.Clamp(x.MinimumConsecutive, 1, 64),
                    Enabled = x.Enabled,
                })
                .ToList();
            whitelistRules = (config.MojibakeWhitelistRules ?? new List<MojibakeWhitelistRule>())
                .Where(x => x != null)
                .Select(x => new MojibakeWhitelistRule
                {
                    Text = x.Text ?? string.Empty,
                    Label = x.Label ?? string.Empty,
                    Enabled = x.Enabled,
                    PartialMatch = x.PartialMatch,
                })
                .ToList();
            detectControlCharacters = config.MojibakeDetectControlCharacters;
        }
    }

    // False-positive safety is more important than breadth here.
    // Source-side corruption is not evaluated by this method; only the translated text is passed in.
    internal static bool TryDetectTranslatedText(string? text, out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrEmpty(text)) return false;

        List<MojibakeMarkerRule> rules;
        List<MojibakeWhitelistRule> white;
        bool control;
        lock (Sync)
        {
            rules = markerRules.Select(x => new MojibakeMarkerRule
            {
                Text = x.Text,
                Label = x.Label,
                MinimumConsecutive = x.MinimumConsecutive,
                Enabled = x.Enabled,
            }).ToList();
            white = whitelistRules.Select(x => new MojibakeWhitelistRule
            {
                Text = x.Text,
                Label = x.Label,
                Enabled = x.Enabled,
                PartialMatch = x.PartialMatch,
            }).ToList();
            control = detectControlCharacters;
        }

        // ホワイトリストは常に文字化け判定より優先する。
        foreach (var entry in white)
        {
            if (!entry.Enabled || string.IsNullOrEmpty(entry.Text)) continue;
            var matched = entry.PartialMatch
                ? text.Contains(entry.Text, StringComparison.Ordinal)
                : string.Equals(text, entry.Text, StringComparison.Ordinal);
            if (matched) return false;
        }

        foreach (var rule in rules)
        {
            if (!rule.Enabled || string.IsNullOrEmpty(rule.Text)) continue;
            var repeat = Math.Clamp(rule.MinimumConsecutive, 1, 64);
            var needle = string.Concat(Enumerable.Repeat(rule.Text, repeat));
            if (!text.Contains(needle, StringComparison.Ordinal)) continue;

            reason = repeat <= 1
                ? $"登録文字『{rule.Text}』を検出"
                : $"登録文字『{rule.Text}』が{repeat}回以上連続";
            return true;
        }

        if (control)
        {
            foreach (var ch in text)
            {
                if (ch == '\r' || ch == '\n' || ch == '\t') continue;
                if (!char.IsControl(ch)) continue;
                reason = $"制御文字 U+{(int)ch:X4} を検出";
                return true;
            }
        }

        return false;
    }
}
