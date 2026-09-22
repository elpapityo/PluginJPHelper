using System.Text.Json;
using Dalamud.Plugin.Services;

namespace PluginJPHelper;

[Serializable]
internal sealed class PluginInstallerSettings
{
    public int Version { get; set; } = 4;
    public bool TranslateInstallerDescriptions { get; set; } = true;
    public bool ShowOriginalBelowTranslation { get; set; } = false;
    public int DiffCheckMinutes { get; set; } = 999;
    public string TranslationDictionaryPath { get; set; } = string.Empty;
    public bool DictionarySectionOpen { get; set; } = false;
    public bool DetailsSectionOpen { get; set; } = false;
    public string TranslationProvider { get; set; } = "Google";
    public string PrivateServerUrl { get; set; } = "https://pjh-translate-relay.akumanomaria.workers.dev";

    public static PluginInstallerSettings Load(string path, IPluginLog log)
    {
        try
        {
            if (!File.Exists(path)) return new PluginInstallerSettings();
            var settings = JsonSerializer.Deserialize<PluginInstallerSettings>(File.ReadAllText(path), JsonOptions) ?? new PluginInstallerSettings();

            // v0.4.1: 旧版の既定値10分を使っている環境だけ999分へ移行する。
            // ユーザーが10分以外へ手動変更している場合はその値を維持する。
            if (settings.Version < 2)
            {
                if (settings.DiffCheckMinutes == 10)
                    settings.DiffCheckMinutes = 999;
                settings.Version = 2;
            }

            if (settings.Version < 3)
            {
                settings.TranslationProvider = "Google";
                settings.PrivateServerUrl ??= string.Empty;
                settings.Version = 3;
            }

            if (settings.Version < 4)
            {
                settings.PrivateServerUrl = "https://pjh-translate-relay.akumanomaria.workers.dev";
                settings.Version = 4;
            }

            if (!string.Equals(settings.TranslationProvider, "PrivateServer", StringComparison.OrdinalIgnoreCase))
                settings.TranslationProvider = "Google";

            settings.PrivateServerUrl = "https://pjh-translate-relay.akumanomaria.workers.dev";
            return settings;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[PJH/PluginInstaller] 設定読込に失敗。既定値を使用します。");
            return new PluginInstallerSettings();
        }
    }

    public void Save(string path, IPluginLog log)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[PJH/PluginInstaller] 設定保存に失敗");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}
