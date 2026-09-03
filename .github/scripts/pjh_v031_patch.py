from pathlib import Path

p = Path("PluginJPHelper/Plugin.cs")
s = p.read_text(encoding="utf-8")

call = "        if (TryTranslateInventoryToolsDynamic(pluginName, source, out translated)) return true;\n"
if "private static bool TryTranslateArtisanDynamic" not in s:
    if s.count(call) != 2:
        raise SystemExit(f"InventoryTools dynamic call sites: {s.count(call)}")

    first = s.find(call)
    s = s[:first + len(call)] + "        if (TryTranslateArtisanDynamic(pluginName, source, false, out translated)) return true;\n" + s[first + len(call):]

    second = s.find(call, first + len(call) + 1)
    if second < 0:
        raise SystemExit("Second dynamic call site not found")
    s = s[:second + len(call)] + "        if (TryTranslateArtisanDynamic(pluginName, source, preserveImGuiId, out translated)) return true;\n" + s[second + len(call):]

    anchor = "    private bool TryTranslatePointer(byte* begin, byte* end, bool preserveImGuiId, out string translated)\n"
    if anchor not in s:
        raise SystemExit("TryTranslatePointer anchor not found")

    helper = '''    // v0.3.1: Artisan の可変表示を安全に翻訳する。数値・アイテム名・ImGui ID は保持する。\n    private static bool TryTranslateArtisanDynamic(string pluginName, string source, bool interactiveLabel, out string translated)\n    {\n        translated = string.Empty;\n        if (!string.Equals(pluginName, "Artisan", StringComparison.OrdinalIgnoreCase)) return false;\n\n        var idMarker = source.IndexOf("##", StringComparison.Ordinal);\n        var visible = idMarker >= 0 ? source[..idMarker] : source;\n        var idSuffix = idMarker >= 0 ? source[idMarker..] : string.Empty;\n        string? result = null;\n\n        const string listTime = "Approximate List Time: ";\n        const string difficulty = "Difficulty: ";\n        const string durability = " | Durability: ";\n        const string quality = " | Quality: ";\n        const string completedMinimum = "Craft completed and minimum quality required met in ";\n        const string completedFullQuality = "Craft completed with full quality in ";\n        const string currentProgress = "Current Item Progress: ";\n        const string overallProgress = "Overall List Progress: ";\n        const string remaining = "Approximate Remaining Duration: ";\n        const string crafting = "Crafting: ";\n        const string retainerItem = "Retainer Item: ";\n\n        if (visible.StartsWith(listTime, StringComparison.Ordinal))\n            result = "おおよそのリスト所要時間: " + visible[listTime.Length..];\n        else if (visible.StartsWith(difficulty, StringComparison.Ordinal))\n        {\n            var durabilityPos = visible.IndexOf(durability, difficulty.Length, StringComparison.Ordinal);\n            var qualityPos = durabilityPos >= 0 ? visible.IndexOf(quality, durabilityPos + durability.Length, StringComparison.Ordinal) : -1;\n            if (durabilityPos > difficulty.Length && qualityPos > durabilityPos)\n            {\n                var difficultyValue = visible.Substring(difficulty.Length, durabilityPos - difficulty.Length);\n                var durabilityValue = visible.Substring(durabilityPos + durability.Length, qualityPos - (durabilityPos + durability.Length));\n                var qualityValue = visible[(qualityPos + quality.Length)..];\n                result = $"難易度: {difficultyValue} | 耐久: {durabilityValue} | 品質: {qualityValue}";\n            }\n        }\n        else if (visible.StartsWith(completedMinimum, StringComparison.Ordinal) && visible.EndsWith("s!", StringComparison.Ordinal))\n        {\n            var seconds = visible.Substring(completedMinimum.Length, visible.Length - completedMinimum.Length - 2);\n            if (!string.IsNullOrWhiteSpace(seconds)) result = $"製作完了、必要最低品質を{seconds}秒で達成しました！";\n        }\n        else if (visible.StartsWith(completedFullQuality, StringComparison.Ordinal) && visible.EndsWith("s!", StringComparison.Ordinal))\n        {\n            var seconds = visible.Substring(completedFullQuality.Length, visible.Length - completedFullQuality.Length - 2);\n            if (!string.IsNullOrWhiteSpace(seconds)) result = $"製作完了、最高品質を{seconds}秒で達成しました！";\n        }\n        else if (visible.StartsWith(currentProgress, StringComparison.Ordinal))\n            result = "現在のアイテム進捗: " + visible[currentProgress.Length..];\n        else if (visible.StartsWith(overallProgress, StringComparison.Ordinal))\n            result = "リスト全体の進捗: " + visible[overallProgress.Length..];\n        else if (visible.StartsWith(remaining, StringComparison.Ordinal))\n            result = "おおよその残り時間: " + visible[remaining.Length..];\n        else if (visible.StartsWith(crafting, StringComparison.Ordinal))\n            result = "製作中: " + visible[crafting.Length..];\n        else if (visible.StartsWith(retainerItem, StringComparison.Ordinal))\n            result = "リテイナー所持品: " + visible[retainerItem.Length..];\n\n        if (result == null) return false;\n        translated = interactiveLabel ? result : result + idSuffix;\n        return true;\n    }\n\n'''
    s = s.replace(anchor, helper + anchor, 1)

old_keywords = '''        var keywords = new[] { plugin.Name?.Trim(), plugin.InternalName?.Trim() }\n            .Where(x => !string.IsNullOrWhiteSpace(x))\n            .Distinct(StringComparer.OrdinalIgnoreCase);\n        state.WindowKeyword = string.Join("|", keywords);\n'''
new_keywords = '''        var keywords = new[] { plugin.Name?.Trim(), plugin.InternalName?.Trim() }\n            .Where(x => !string.IsNullOrWhiteSpace(x))\n            .Distinct(StringComparer.OrdinalIgnoreCase)\n            .ToList();\n        if (string.Equals(plugin.InternalName, "Artisan", StringComparison.OrdinalIgnoreCase)\n            || string.Equals(plugin.Name, "Artisan", StringComparison.OrdinalIgnoreCase))\n        {\n            foreach (var required in new[] { "Artisan", "List Editor", "Processing List" })\n                if (!keywords.Contains(required, StringComparer.OrdinalIgnoreCase)) keywords.Add(required);\n        }\n        state.WindowKeyword = string.Join("|", keywords);\n'''

if old_keywords in s:
    s = s.replace(old_keywords, new_keywords, 1)
elif '"Artisan", "List Editor", "Processing List"' not in s:
    raise SystemExit("Installed plugin keyword block not found")

p.write_text(s, encoding="utf-8")

if "TryTranslateArtisanDynamic" not in s or "Retainer Item: " not in s:
    raise SystemExit("Artisan v0.3.1 verification failed")
