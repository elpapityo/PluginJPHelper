using Dalamud.Configuration;

namespace PluginJPHelper;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;
    public int CaptureSchemaVersion { get; set; } = 0;
    public int DataResetVersion { get; set; } = 0;
    public bool CleanSlateMode { get; set; } = false;
    public Dictionary<string, PluginDictionaryState> Plugins { get; set; } = new(StringComparer.Ordinal);
    public void EnsurePlugins()
    {
        Plugins ??= new Dictionary<string, PluginDictionaryState>(StringComparer.Ordinal);
        foreach (var existingState in Plugins.Values)
            if (existingState != null) existingState.LastCsvPath ??= string.Empty;
        var migrateTranslationTargets = Version < 2;
        if (migrateTranslationTargets)
        {
            foreach (var existing in Plugins.Values)
                if (existing != null) existing.TranslationTarget = true;
            Version = 2;
        }
        foreach (var name in new[] { "RSR", "BMR", "BM" })
        {
            if (!Plugins.TryGetValue(name, out var state) || state == null)
            {
                state = new PluginDictionaryState { Enabled = name == "RSR", TranslationTarget = true, WindowKeyword = name switch { "RSR" => "Rotation Solver", "BMR" => "BossModReborn", "BM" => "BossMod", _ => string.Empty } };
                Plugins[name] = state;
            }
            state.UserOverrides ??= new Dictionary<string, string>(StringComparer.Ordinal);
            state.Locations ??= new Dictionary<string, DictionaryLocation>(StringComparer.Ordinal);
        }
    }
}

public sealed class PluginDictionaryState
{
    public bool Enabled { get; set; }
    public bool TranslationTarget { get; set; }
    public string WindowKeyword { get; set; } = string.Empty;
    public string LastCsvPath { get; set; } = string.Empty;
    public Dictionary<string, string> UserOverrides { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, DictionaryLocation> Locations { get; set; } = new(StringComparer.Ordinal);
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
