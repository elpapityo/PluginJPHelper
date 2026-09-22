using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace PluginJPHelper;

internal sealed class PluginInstallerModule : IDisposable
{
        private const string DictionaryFileName = "plugin-installer-translations.json";
    private const string TerminologyFileName = "plugin-installer-terminology.json";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly IPluginLog log;
    private readonly PluginInstallerSettings config;
    private readonly string settingsPath;
    private readonly MainWindow mainWindow;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(25) };
    private readonly ConcurrentQueue<TranslationResult> completed = new();
    private readonly ConcurrentQueue<string> pendingInstallerSearch = new();
    private readonly ConcurrentQueue<string> pendingDictionarySelection = new();
    private readonly ConcurrentQueue<TranslationWork> translationQueue = new();
    private readonly ConcurrentQueue<CommandTranslationWork> commandTranslationQueue = new();
    private readonly ConcurrentQueue<CommandTranslationResult> completedCommandTranslations = new();
    private readonly Dictionary<string, CommandTranslationWork> pendingCommandWork = new(StringComparer.Ordinal);
    private readonly HashSet<string> queuedCommandWork = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CommandTranslationEntry> commandDictionary = new(StringComparer.Ordinal);
    // 差分検出はバックグラウンドで行ってよいが、翻訳サービスへの送信はユーザー操作時だけに限定する。
    private readonly Dictionary<string, TranslationWork> pendingGoogleWork = new(StringComparer.Ordinal);
    private readonly HashSet<string> queuedWork = new(StringComparer.Ordinal);
    private readonly Dictionary<object, OriginalManifest> originals = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, TranslationDictionaryEntry> dictionary = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> terminology = new(StringComparer.OrdinalIgnoreCase);
    private readonly object sync = new();
    private string dictionaryPath;
    private string terminologyPath;
    private string commandDictionaryPath;
    private string commandReacquireStatus = "未実行";
    private readonly string sharedDictionaryDirectory;
    private readonly string manifestSnapshotPath;
    private readonly string configDirectory;
    private readonly HashSet<object> translatedManifests = new(ReferenceEqualityComparer.Instance);

    private object? pluginInstallerWindow;
    private Type? serviceGenericType;
    private string reflectionStatus = "未接続";
    private string searchJapanese = string.Empty;
    private string searchEnglish = string.Empty;
    private string translationStatus = "待機中";
    private string dictionaryPathInput = string.Empty;
    private long lastDiffCheckTick;
    private bool forceDiffCheck = true;
    // v0.4.7 hotfix: 起動後の追従は重い差分走査ではなく、表示用Manifestへの既存辞書再適用だけで行う。
    // 差分チェック・Manifestスナップショット保存・外部翻訳キューは通常の間隔/手動実行時だけ。
    // v0.4.7: Plugin Installer がRemote Manifestを後から差し替えたり、同じオブジェクトの表示文字列を戻した場合でも
    // PJHをOFF→ONせず自動復旧できるよう、外部翻訳を伴わない表示再適用を定期実行する。
    private long lastDisplayReapplyTick;
    private const long DisplayReapplyIntervalMs = 2_000;

    // v0.4.12: Dalamud Plugin Installer がリポジトリを読み込み中は、
    // Manifestへの翻訳書き込みを一切行わない。
    // PluginManager.PluginsReady / ReposReady が両方 true になってから、
    // さらに短い猶予を置いてから翻訳を開始する。
    private object? pluginManager;
    private Type? pluginManagerType;
    private long installerReadySinceTick;
    private bool installerTranslationReady;
    private bool installerWaitLogged;
    private const long InstallerReadyGraceMs = 2_500;

    private int translatedManifestCount;
    private int manifestCount;
    private int availableManifestCount;
    private int installedManifestCount;
    private int dictionaryCount;
    private int missingCount;
    private int changedCount;
    private int manualReviewCount;
    private int queuedCount;
    private bool workerRunning;
    private volatile bool stopWorkerAfterRateLimit;
    private volatile bool stopWorkerAfterProviderFailure;
    private string workerStopReason = string.Empty;
    private DateTimeOffset? rateLimitedUntil;
    private DateTimeOffset lastGoogleRequest = DateTimeOffset.MinValue;
    private bool? relayGoogleAvailable;
    private DateTimeOffset relayGoogleAvailabilityCheckedAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan RelayGoogleAvailabilityCache = TimeSpan.FromMinutes(5);
    private const string PjhRelayBaseUrl = "https://pjh-translate-relay.akumanomaria.workers.dev";
    private string googleStatus = "未使用";
    private string googleStatusDetail = "まだGoogle翻訳へ送信していません";
    private int googleFailureCount;
    private readonly List<UiLogEntry> uiLogs = new();
    private readonly Dictionary<string, UpdatePluginRow> updatePluginRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> manualReviewSeen = new(StringComparer.Ordinal);
    private int updateSortColumn;
    private bool updateSortAscending = true;
    private int updateTableGeneration;

    // v0.4.5: Plugin Installer の「更新履歴」は Manifest の Description とは別経路。
    // 外部翻訳への送信は、ユーザーが「更新履歴を翻訳」を押した時だけ行う。
    private readonly Dictionary<object, string> originalChangelogTexts = new(ReferenceEqualityComparer.Instance);
    private readonly ConcurrentQueue<ChangelogTranslationResult> completedChangelogTranslations = new();
    private Task? changelogTranslationTask;
    private CancellationTokenSource? changelogTranslationCts;
    private string changelogTranslationStatus = "待機中";
    private int changelogTranslationTotal;
    private int changelogTranslationCompleted;
    private int changelogTranslationFailed;
    private int changelogUntranslatedBeforeBatch;

    internal string GoogleStatus => this.googleStatus;
    internal string GoogleStatusDetail => this.googleStatusDetail;
    internal DateTimeOffset? GoogleRateLimitedUntil => this.rateLimitedUntil;
    internal bool UsePrivateServer => string.Equals(this.config.TranslationProvider, "PrivateServer", StringComparison.OrdinalIgnoreCase);
    internal string TranslationProviderName => this.UsePrivateServer ? "PJH中継サーバー" : "Google翻訳";
    internal string EffectiveTranslationProviderName => this.UsePrivateServer && this.relayGoogleAvailable == true ? "Google翻訳" : this.TranslationProviderName;
    internal string PrivateServerUrl
    {
        get => PjhRelayBaseUrl;
        set
        {
            if (string.Equals(this.config.PrivateServerUrl, PjhRelayBaseUrl, StringComparison.Ordinal)) return;
            this.config.PrivateServerUrl = PjhRelayBaseUrl;
            this.SaveConfig();
        }
    }

    internal void SetTranslationProvider(bool usePrivateServer)
    {
        var next = usePrivateServer ? "PrivateServer" : "Google";
        if (string.Equals(this.config.TranslationProvider, next, StringComparison.OrdinalIgnoreCase)) return;
        this.config.TranslationProvider = next;
        this.rateLimitedUntil = null;
        this.relayGoogleAvailable = null;
        this.relayGoogleAvailabilityCheckedAt = DateTimeOffset.MinValue;
        this.googleStatus = "未使用";
        this.googleStatusDetail = this.TranslationProviderName + "はまだ使用していません";
        this.SaveConfig();
    }

    private static readonly Dictionary<string, string> SearchGlossary = new(StringComparer.OrdinalIgnoreCase)
    {
        ["手配書"] = "hunt",
        ["討伐手帳"] = "hunting",
        ["モブハント"] = "hunt",
        ["リスキーモブ"] = "hunt",
        ["ハント"] = "hunt",
        ["製作"] = "crafting",
        ["クラフト"] = "crafting",
        ["製作手帳"] = "crafting",
        ["採集"] = "gathering",
        ["採集手帳"] = "gathering",
        ["ギャザラー"] = "gathering",
        ["リテイナー"] = "retainer",
        ["マーケット"] = "market",
        ["マーケットボード"] = "market",
        ["FATE"] = "FATE",
        ["フェイト"] = "FATE",
        ["ダンジョン"] = "dungeon",
        ["コンテンツ"] = "duty",
        ["コンテンツファインダー"] = "duty",
        ["レイド"] = "raid",
        ["討伐討滅"] = "trial",
        ["自動戦闘"] = "combat",
        ["戦闘"] = "combat",
        ["テレポ"] = "teleport",
        ["地図"] = "map",
        ["宝の地図"] = "treasure",
        ["釣り"] = "fishing",
        ["装備"] = "gear",
        ["インベントリ"] = "inventory",
        ["所持品"] = "inventory",
        ["軍票"] = "seal",
        ["フリーカンパニー"] = "company",
        ["パーティ"] = "party",
        ["マクロ"] = "macro",
    };

    private static readonly Dictionary<string, string> DefaultTerminology = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Hunting Log"] = "討伐手帳",
        ["Grand Company"] = "グランドカンパニー",
        ["Company Seals"] = "軍票",
        ["Crafting Log"] = "製作手帳",
        ["Gathering Log"] = "採集手帳",
        ["Duty Finder"] = "コンテンツファインダー",
        ["Free Company"] = "フリーカンパニー",
        ["Market Board"] = "マーケットボード",
        ["Retainer"] = "リテイナー",
        ["Collectables"] = "収集品",
        ["Collectable"] = "収集品",
        ["Sidequest"] = "サブクエスト",
        ["Job Quest"] = "ジョブクエスト",
        ["Class Quest"] = "クラスクエスト",
        ["Trial"] = "討伐・討滅戦",
        ["Raid"] = "レイド",
        ["Crafting"] = "製作",
        ["Gathering"] = "採集",
        ["Rotation"] = "スキル回し",
        ["Plugin"] = "プラグイン",
        ["Text Command"] = "テキストコマンド",
        ["Party"] = "パーティ",
        ["Quest"] = "クエスト",
        ["FATE"] = "FATE",
        ["Hunt Marks"] = "リスキーモブ",
        ["Hunt Mark"] = "リスキーモブ",
        ["Elite Mark"] = "リスキーモブ",
        ["Hunt Mark Bills"] = "モブハントの手配書",
        ["Hunt Mark Bill"] = "モブハントの手配書",
        ["Hunt Targets"] = "モブハント対象",
        ["Hunt Target"] = "モブハント対象",
        ["Hunt Mobs"] = "モブハント対象",
        ["Hunt Mob"] = "モブハント対象",
        ["Hunt Icons"] = "モブハントアイコン",
        ["Hunt Icon"] = "モブハントアイコン",
        ["Hunt Area"] = "モブハント範囲",
        ["Local Hunts"] = "現在エリアのモブハント",
        ["Local Area"] = "現在エリア",
        ["Map Zone"] = "マップエリア",
        ["Hunt Bill"] = "モブハントの手配書",
        ["Hunts"] = "モブハント",
        ["Hunting"] = "モブハント",
        ["Hunt"] = "モブハント",
        ["B-Rank"] = "Bランク",
        ["A-Rank"] = "Aランク",
        ["S-Rank"] = "Sランク",
        ["ARR"] = "新生エオルゼア",
        ["Dalamud"] = "Dalamud",
        ["vnavmesh"] = "vnavmesh",
        ["BossMod"] = "BossMod",
        ["BossModReborn"] = "BossModReborn",
        ["RotationSolverReborn"] = "RotationSolverReborn",
        ["AutoDuty"] = "AutoDuty",
        ["Artisan"] = "Artisan",
        ["GatherBuddyReborn"] = "GatherBuddyReborn",
        ["Lifestream"] = "Lifestream",
        ["HaselTweaks"] = "HaselTweaks",
    };

    public PluginInstallerModule(IDalamudPluginInterface pluginInterface, ICommandManager commandManager, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.log = log;
        this.settingsPath = Path.Combine(pluginInterface.GetPluginConfigDirectory(), "plugin-installer-settings.json");
        this.config = PluginInstallerSettings.Load(this.settingsPath, log);
        var configDir = pluginInterface.GetPluginConfigDirectory();
        Directory.CreateDirectory(configDir);
        this.configDirectory = configDir;

        this.sharedDictionaryDirectory = Path.Combine(configDir, "Dictionaries", "PluginInstaller");
        Directory.CreateDirectory(this.sharedDictionaryDirectory);
        var defaultDictionaryPath = Path.Combine(this.sharedDictionaryDirectory, DictionaryFileName);
        this.dictionaryPath = string.IsNullOrWhiteSpace(this.config.TranslationDictionaryPath)
            ? defaultDictionaryPath
            : this.config.TranslationDictionaryPath.Trim();
        this.terminologyPath = Path.Combine(Path.GetDirectoryName(this.dictionaryPath) ?? this.sharedDictionaryDirectory, TerminologyFileName);
        this.dictionaryPathInput = this.dictionaryPath;
        this.manifestSnapshotPath = Path.Combine(configDir, "plugin-installer-manifests.json");
        this.commandDictionaryPath = Path.Combine(configDir, "plugin-installer-command-translations.json");

        // Default path only: seed the bundled dictionary. A manually selected file is never overwritten.
        if (string.Equals(this.dictionaryPath, defaultDictionaryPath, StringComparison.OrdinalIgnoreCase))
        {
            this.SeedSharedDictionaryIfMissing(DictionaryFileName, this.dictionaryPath);
            this.SeedSharedDictionaryIfMissing(TerminologyFileName, this.terminologyPath);
        }

        this.LoadDictionary();
        this.LoadTerminology();
        this.LoadCommandDictionary();

        this.mainWindow = new MainWindow(this);
        this.log.Information("[PJH/PluginInstaller] 起動。辞書 {Count} 件。辞書ファイル: {Path}", this.dictionary.Count, this.dictionaryPath);
    }

    public void Dispose()
    {
        try { this.changelogTranslationCts?.Cancel(); } catch { }
        this.RestoreAll();
        this.SaveDictionary();
        this.SaveCommandDictionary();
        this.SaveConfig();
        this.http.Dispose();
    }

    public void DrawUi() => this.mainWindow.Draw();


    private void SeedSharedDictionaryIfMissing(string fileName, string destinationPath)
    {
        try
        {
            var assemblyDir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? string.Empty;
            var bundledPath = Path.Combine(assemblyDir, "Dictionaries", "PluginInstaller", fileName);

            // v0.2.5: A previous test build may have already created an empty [] dictionary.
            // Treat a missing or zero-entry translation dictionary as uninitialized and restore
            // the bundled seed dictionary. Never overwrite a non-empty user dictionary.
            var destinationNeedsSeed = !File.Exists(destinationPath);
            if (!destinationNeedsSeed && string.Equals(fileName, DictionaryFileName, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var existingJson = File.ReadAllText(destinationPath, Encoding.UTF8);
                    var existing = JsonSerializer.Deserialize<List<TranslationDictionaryEntry>>(existingJson, JsonOptions);
                    destinationNeedsSeed = existing == null || existing.Count == 0;
                }
                catch
                {
                    destinationNeedsSeed = true;
                }
            }

            if (!destinationNeedsSeed)
                return;

            if (File.Exists(bundledPath))
            {
                File.Copy(bundledPath, destinationPath, true);
                this.log.Information("[PJH/PluginInstaller] PJH共有辞書へ初期ファイルを配置/復旧: {Path}", destinationPath);
                return;
            }

            var oldPath = Path.Combine(this.configDirectory, fileName);
            if (File.Exists(oldPath))
            {
                File.Copy(oldPath, destinationPath, true);
                this.log.Information("[PJH/PluginInstaller] 旧PIJT辞書をPJH共有辞書へ移行: {Path}", destinationPath);
            }
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[PJH/PluginInstaller] PJH共有辞書への初期配置に失敗: {Path}", destinationPath);
        }
    }

    private void ReloadDictionaryFromDisk()
    {
        try
        {
            lock (this.sync)
                this.dictionary.Clear();
            this.LoadDictionary();
            this.translationStatus = $"辞書を再読み込みしました（{this.dictionaryCount}件）";
            this.log.Information("[PJH/PluginInstaller] 辞書を再読み込み: {Count}件 / {Path}", this.dictionaryCount, this.dictionaryPath);
            this.forceDiffCheck = true;
            this.lastDiffCheckTick = 0;
        }
        catch (Exception ex)
        {
            this.translationStatus = "辞書再読み込み失敗";
            this.log.Error(ex, "[PJH/PluginInstaller] 辞書再読み込み失敗");
        }
    }

    private void SelectDictionaryFile()
    {
        try
        {
            var initialDir = Path.GetDirectoryName(this.dictionaryPath) ?? this.sharedDictionaryDirectory;
            var escaped = initialDir.Replace("'", "''");
            var script = "$ErrorActionPreference='Stop'; Add-Type -AssemblyName System.Windows.Forms; " +
                         "$d=New-Object System.Windows.Forms.OpenFileDialog; $d.Filter='JSON files (*.json)|*.json|All files (*.*)|*.*'; " +
                         "$d.InitialDirectory='" + escaped + "'; if($d.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK){[Console]::OutputEncoding=[Text.Encoding]::UTF8; Write-Output $d.FileName}";
            _ = Task.Run(() =>
            {
                try
                {
                    var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
                    var psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = "-NoProfile -STA -ExecutionPolicy Bypass -EncodedCommand " + encoded,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                    };
                    using var p = Process.Start(psi);
                    if (p == null) return;
                    var selected = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit();
                    if (p.ExitCode == 0 && !string.IsNullOrWhiteSpace(selected) && File.Exists(selected))
                        this.pendingDictionarySelection.Enqueue(selected);
                }
                catch (Exception ex)
                {
                    this.translationStatus = "ファイル選択に失敗";
                    this.log.Error(ex, "[PJH/PluginInstaller] 翻訳ファイル選択に失敗");
                }
            });
        }
        catch (Exception ex)
        {
            this.translationStatus = "ファイル選択に失敗";
            this.log.Error(ex, "[PJH/PluginInstaller] 翻訳ファイル選択に失敗");
        }
    }

    private void ApplyManualDictionaryPath()
    {
        try
        {
            var path = this.dictionaryPathInput.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(path))
            {
                this.translationStatus = "辞書ファイルのパスを入力してください";
                return;
            }

            if (!File.Exists(path))
            {
                this.translationStatus = "指定した辞書ファイルが見つかりません";
                this.log.Warning("[PJH/PluginInstaller] 指定辞書が存在しません: {Path}", path);
                return;
            }

            // Validate before switching so a typo/bad JSON does not break the current dictionary.
            var json = File.ReadAllText(path, Encoding.UTF8);
            var items = JsonSerializer.Deserialize<List<TranslationDictionaryEntry>>(json, JsonOptions);
            if (items == null || items.Count == 0)
            {
                this.translationStatus = "指定した辞書は0件です";
                return;
            }

            this.RestoreAll();
            this.dictionaryPath = Path.GetFullPath(path);
            // 翻訳JSONを別フォルダーから選んでも、FFXIV用語辞書はPJH標準Glossaryを継続使用する。
            this.terminologyPath = Path.Combine(this.sharedDictionaryDirectory, TerminologyFileName);
            this.config.TranslationDictionaryPath = this.dictionaryPath;
            this.SaveConfig();

            lock (this.sync)
                this.dictionary.Clear();
            this.LoadDictionary();
            this.LoadTerminology();
            this.translationStatus = $"指定辞書を読み込みました（{this.dictionaryCount}件）";
            this.log.Information("[PJH/PluginInstaller] 手動指定辞書へ切替: {Count}件 / {Path}", this.dictionaryCount, this.dictionaryPath);
            this.forceDiffCheck = true;
            this.lastDiffCheckTick = 0;
        }
        catch (Exception ex)
        {
            this.translationStatus = "辞書ファイル指定に失敗";
            this.log.Error(ex, "[PJH/PluginInstaller] 辞書ファイル指定に失敗: {Path}", this.dictionaryPathInput);
        }
    }

    private void OpenDictionaryDirectory()
    {
        try
        {
            var dir = Path.GetDirectoryName(this.dictionaryPath);
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                dir = this.configDirectory;
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{dir}\"",
                UseShellExecute = true,
            });
            this.log.Information("[PJH/PluginInstaller] 辞書フォルダーを開きました: {Path}", dir);
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[PJH/PluginInstaller] 辞書フォルダーを開けませんでした: {Path}", this.dictionaryPath);
        }
    }

    private void RevealDictionaryFile()
    {
        try
        {
            if (!File.Exists(this.dictionaryPath))
            {
                this.translationStatus = "辞書ファイルが見つかりません";
                return;
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{this.dictionaryPath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[PJH/PluginInstaller] 辞書ファイルを表示できませんでした: {Path}", this.dictionaryPath);
        }
    }

    private void AddUiLog(string level, string message, string plugin = "")
    {
        lock (this.sync)
        {
            this.uiLogs.Add(new UiLogEntry(DateTimeOffset.Now, level, plugin, message));
            if (this.uiLogs.Count > 1000)
                this.uiLogs.RemoveRange(0, this.uiLogs.Count - 1000);
        }
    }

    private void UpsertUpdateRow(string internalName, string name, string changeType, string fields, string translation, string saveState, string status)
    {
        lock (this.sync)
        {
            if (!this.updatePluginRows.TryGetValue(internalName, out var row))
            {
                row = new UpdatePluginRow
                {
                    InternalName = internalName,
                    Name = string.IsNullOrWhiteSpace(name) ? internalName : name,
                    FirstDetected = DateTimeOffset.Now,
                };
                this.updatePluginRows[internalName] = row;
            }
            row.LastUpdated = DateTimeOffset.Now;
            if (!string.IsNullOrWhiteSpace(changeType)) row.ChangeType = changeType;
            if (!string.IsNullOrWhiteSpace(fields)) row.Fields = fields;
            if (!string.IsNullOrWhiteSpace(translation)) row.Translation = translation;
            if (!string.IsNullOrWhiteSpace(saveState)) row.SaveState = saveState;
            if (!string.IsNullOrWhiteSpace(status)) row.Status = status;
        }
    }

    private List<UiLogEntry> GetUiLogsSnapshot()
    {
        lock (this.sync)
            return this.uiLogs.ToList();
    }

    private List<UpdatePluginRow> GetUpdateRowsSnapshot()
    {
        List<UpdatePluginRow> rows;
        lock (this.sync)
            rows = this.updatePluginRows.Values.Select(x => x.Clone()).ToList();

        Comparison<UpdatePluginRow> cmp = this.updateSortColumn switch
        {
            1 => (a, b) => string.Compare(a.ChangeType, b.ChangeType, StringComparison.OrdinalIgnoreCase),
            2 => (a, b) => string.Compare(a.Fields, b.Fields, StringComparison.OrdinalIgnoreCase),
            3 => (a, b) => string.Compare(a.Translation, b.Translation, StringComparison.OrdinalIgnoreCase),
            4 => (a, b) => string.Compare(a.SaveState, b.SaveState, StringComparison.OrdinalIgnoreCase),
            5 => (a, b) => DateTimeOffset.Compare(a.LastUpdated, b.LastUpdated),
            _ => (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
        };
        rows.Sort(cmp);
        if (!this.updateSortAscending) rows.Reverse();
        return rows;
    }

    private void ToggleUpdateSort(int column)
    {
        if (this.updateSortColumn == column) this.updateSortAscending = !this.updateSortAscending;
        else { this.updateSortColumn = column; this.updateSortAscending = true; }
    }

    private void ClearUiLogs()
    {
        lock (this.sync) this.uiLogs.Clear();
    }

    private void ClearUpdateRows()
    {
        lock (this.sync) this.updatePluginRows.Clear();
    }

    private string BuildUiLogText()
    {
        var sb = new StringBuilder();
        foreach (var item in this.GetUiLogsSnapshot())
            sb.Append(item.Time.ToString("HH:mm:ss")).Append(" [").Append(item.Level).Append("] ")
              .Append(string.IsNullOrWhiteSpace(item.Plugin) ? string.Empty : item.Plugin + " - ")
              .AppendLine(item.Message);
        return sb.ToString();
    }

    public void Tick()
    {
        while (this.pendingDictionarySelection.TryDequeue(out var selectedDictionary))
        {
            this.dictionaryPathInput = selectedDictionary;
            this.ApplyManualDictionaryPath();
        }

        // v0.4.12: Plugin Installerのリポジトリ読込中は、
        // Manifest・検索欄・更新履歴など、Dalamud側オブジェクトへの書き込みを行わない。
        // 読み込み完了後も2.5秒待ってから処理を再開する。
        if (!this.IsPluginInstallerReadyForTranslation())
            return;

        this.ApplyCompletedTranslations();
        this.ApplyCompletedCommandTranslations();
        this.ApplyCompletedChangelogTranslations();
        while (this.pendingInstallerSearch.TryDequeue(out var pendingSearch))
            this.SetInstallerSearchText(pendingSearch);

        if (this.config.TranslateInstallerDescriptions)
        {
            var intervalMs = Math.Max(1, this.config.DiffCheckMinutes) * 60_000L;
            var now = Environment.TickCount64;

            // v0.4.7: 差分チェックとは別に、現在Plugin Installerが保持している表示用Manifest全件へ
            // 既存辞書だけを2秒間隔で再適用する。Google/PJH中継サーバーへの送信は一切行わない。
            // v0.4.12: この処理もPluginManagerの読込完了＋安全待機後にだけ実行する。
            if (now - this.lastDisplayReapplyTick >= DisplayReapplyIntervalMs)
            {
                this.lastDisplayReapplyTick = now;
                this.ReapplyDictionaryToCurrentRemoteManifests();
            }

            // 重い差分走査は手動要求、または設定された通常間隔が来た時だけ実行する。
            if (this.forceDiffCheck || now - this.lastDiffCheckTick >= intervalMs)
            {
                this.forceDiffCheck = false;
                this.lastDiffCheckTick = now;
                this.ScanAllInstallerManifests();
            }
        }

        this.StartWorkerIfNeeded();
    }

    private bool IsPluginInstallerReadyForTranslation()
    {
        try
        {
            if (!this.TryResolvePluginManager(out var manager))
            {
                this.ResetInstallerReadyGate("PluginManager待ち");
                return false;
            }

            var type = manager.GetType();
            var pluginsReady = ReadBool(type, manager, "PluginsReady", false);
            var reposReady = ReadBool(type, manager, "ReposReady", false);
            var safeMode = ReadBool(type, manager, "SafeMode", false);

            if (!pluginsReady || !reposReady || safeMode)
            {
                this.ResetInstallerReadyGate(safeMode
                    ? "Dalamudセーフモード中"
                    : "Dalamud Plugin Installer読み込み完了待ち");
                return false;
            }

            var now = Environment.TickCount64;
            if (this.installerReadySinceTick == 0)
            {
                this.installerReadySinceTick = now;
                this.installerTranslationReady = false;
                this.reflectionStatus = "Dalamud読み込み完了 / 翻訳安全待機中";
                return false;
            }

            if (now - this.installerReadySinceTick < InstallerReadyGraceMs)
            {
                this.reflectionStatus = "Dalamud読み込み完了 / 翻訳安全待機中";
                return false;
            }

            if (!this.installerTranslationReady)
            {
                this.installerTranslationReady = true;
                this.installerWaitLogged = false;
                this.lastDisplayReapplyTick = 0;
                this.forceDiffCheck = true;
                this.log.Information("[PJH/PluginInstaller] PluginManager読込完了を確認。安全待機後にInstaller翻訳を開始します。");
            }

            return true;
        }
        catch (Exception ex)
        {
            this.ResetInstallerReadyGate("PluginManager状態確認エラー");
            this.log.Debug(ex, "[PJH/PluginInstaller] PluginManagerの準備状態確認に失敗");
            return false;
        }
    }

    private void ResetInstallerReadyGate(string status)
    {
        this.installerReadySinceTick = 0;
        this.installerTranslationReady = false;
        this.reflectionStatus = status;
        if (!this.installerWaitLogged)
        {
            this.installerWaitLogged = true;
            this.log.Information("[PJH/PluginInstaller] {Status}。Installer翻訳を一時停止します。", status);
        }
    }

    private bool TryResolvePluginManager(out object manager)
    {
        if (this.pluginManager != null)
        {
            manager = this.pluginManager;
            return true;
        }

        manager = null!;
        var dalamudAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, "Dalamud", StringComparison.Ordinal));
        if (dalamudAssembly == null) return false;

        this.serviceGenericType ??= dalamudAssembly.GetTypes()
            .FirstOrDefault(t => t.IsGenericTypeDefinition && t.Name == "Service`1" && t.Namespace == "Dalamud");
        this.pluginManagerType ??= dalamudAssembly.GetType("Dalamud.Plugin.Internal.PluginManager");
        if (this.serviceGenericType == null || this.pluginManagerType == null) return false;

        var serviceType = this.serviceGenericType.MakeGenericType(this.pluginManagerType);
        var getMethod = serviceType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(m => m.Name == "Get" && m.GetParameters().Length == 0);
        this.pluginManager = getMethod?.Invoke(null, null);
        if (this.pluginManager == null) return false;

        manager = this.pluginManager;
        return true;
    }

    private static bool ReadBool(Type type, object instance, string propertyName, bool fallback)
    {
        try
        {
            var property = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return property?.GetValue(instance) is bool value ? value : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private void ScanAllInstallerManifests()
    {
        try
        {
            if (!this.TryResolvePluginInstaller(out var installer))
                return;

            var remoteManifests = CollectRemoteManifests(installer, out var availableCount, out var installedCount, out var rawCount);
            var manifests = BuildCanonicalManifestMap(remoteManifests);
            if (remoteManifests.Count == 0)
            {
                this.reflectionStatus = "Dalamud内部構造を取得できません";
                return;
            }

            var total = manifests.Count;
            var missing = 0;
            var changed = 0;
            var manualReview = 0;
            var queued = 0;
            var applied = 0;
            var snapshot = new List<ManifestSnapshotEntry>();
            var allowAutoGoogle = this.dictionary.Count > 0;

            foreach (var manifest in manifests.Values)
            {
                var t = manifest.GetType();
                var internalName = ReadString(t, manifest, "InternalName");
                var name = ReadString(t, manifest, "Name");
                var version = ReadObject(t, manifest, "AssemblyVersion")?.ToString() ?? string.Empty;
                var currentPunchline = ReadString(t, manifest, "Punchline");
                var currentDescription = ReadString(t, manifest, "Description");
                if (string.IsNullOrWhiteSpace(internalName) || (string.IsNullOrWhiteSpace(currentPunchline) && string.IsNullOrWhiteSpace(currentDescription)))
                    continue;

                this.EnsureOriginalManifest(manifest);
                OriginalManifest original;
                lock (this.sync) original = this.originals[manifest];
                // Never compare against Japanese text written by this plugin.
                var punchline = original.Punchline;
                var description = original.Description;
                snapshot.Add(new ManifestSnapshotEntry
                {
                    InternalName = internalName,
                    Name = name,
                    AssemblyVersion = version,
                    Punchline = punchline,
                    Description = description,
                });

                if (this.dictionary.TryGetValue(internalName, out var entry))
                {
                    var punchChanged = !string.Equals(entry.PunchlineSource ?? string.Empty, punchline, StringComparison.Ordinal);
                    var descChanged = !string.Equals(entry.DescriptionSource ?? string.Empty, description, StringComparison.Ordinal);
                    if (!punchChanged && !descChanged)
                    {
                        this.ApplyDictionaryEntry(manifest, entry);
                        applied++;
                        continue;
                    }

                    var changedFields = GetFieldDisplayName(punchChanged, descChanged);
                    // Human-reviewed/manual dictionary entries are never overwritten by Google.
                    // Record them as review-needed, but do not spam the same log every scan.
                    if (!entry.AutoTranslated)
                    {
                        manualReview++;
                        var reviewKey = internalName + "|" + punchline + "|" + description;
                        bool firstNotice;
                        lock (this.sync) firstNotice = this.manualReviewSeen.Add(reviewKey);
                        if (firstNotice)
                        {
                            this.UpsertUpdateRow(internalName, name, "[要確認]", changedFields, "[保護]", "既存辞書", "手動辞書保護");
                            this.AddUiLog("要確認", $"手動辞書の原文変更を検出: {changedFields}（Google上書きなし）", internalName);
                            this.log.Information("[PJH/PluginInstaller] 手動辞書を保護: {Plugin} / 原文変更あり・Google上書きなし", internalName);
                        }
                        this.ApplyDictionaryEntry(manifest, entry);
                        applied++;
                        continue;
                    }

                    changed++;
                    this.UpsertUpdateRow(internalName, name, "[変更]", changedFields, "[待機]", "未保存", "差分検出");
                    this.AddUiLog("変更", $"原文変更を検出: {changedFields}", internalName);
                    if (allowAutoGoogle && this.QueueWork(new TranslationWork(manifest, internalName, name, version, punchline, description, punchChanged, descChanged, entry)))
                        queued++;
                }
                else
                {
                    missing++;
                    this.UpsertUpdateRow(internalName, name, "[新規]", "プラグイン説明 / 詳細説明", "[待機]", "未保存", "新規検出");
                    this.AddUiLog("新規", "辞書にないプラグインを検出", internalName);
                    if (allowAutoGoogle && this.QueueWork(new TranslationWork(manifest, internalName, name, version, punchline, description, true, true, null)))
                        queued++;
                }
            }

            // Apply translations to installed plugin manifests for display only.
            // Privacy/safety rule: only InternalNames confirmed by Remote Manifest are eligible.
            // Local-only plugins are ignored, and Local text is never used for diff/Google/dictionary updates.
            // 表示用Remote Manifestは同一InternalNameでも別リポジトリ分を含む全件へ適用する。
            var remoteAppliedAll = this.ApplyDictionaryToAllRemoteManifests(remoteManifests);
            var installedApplied = this.ApplyDictionaryToInstalledPublicManifests(installer, manifests.Keys);

            this.SaveManifestSnapshot(snapshot);
            this.manifestCount = total;
            this.availableManifestCount = availableCount;
            this.installedManifestCount = installedCount;
            this.missingCount = missing;
            this.changedCount = changed;
            this.manualReviewCount = manualReview;
            this.queuedCount = queued;
            this.dictionaryCount = this.dictionary.Count;
            this.reflectionStatus = $"接続済み（Remote {availableCount} / 対象 {total} / 導入済 {installedCount}）";
            this.translationStatus = !allowAutoGoogle && missing > 0
                ? $"初回Manifest取得完了（{missing}件）。辞書作成待ち"
                : queued > 0
                    ? $"差分 {queued} 件を検出（手動翻訳待ち）"
                    : $"辞書適用完了（新規 {missing} / 変更 {changed} / 要確認 {manualReview}）";

            this.log.Information("[PJH/PluginInstaller] 差分チェック完了: RemoteManifest={Available}, 導入済={Installed}, Remote生件数={Raw}, 対象={Unique}, Remote辞書適用={Applied}, Remote全表示適用={RemoteAllApplied}, 導入済表示適用={InstalledApplied}, 新規={Missing}, 変更={Changed}, 要確認={ManualReview}, 新規キュー={Queued}", availableCount, installedCount, rawCount, total, applied, remoteAppliedAll, installedApplied, missing, changed, manualReview, queued);
            this.AddUiLog("確認", $"差分チェック完了: RemoteManifest={availableCount}, 対象={total}, 導入済={installedCount}, 導入済翻訳={installedApplied}, 新規={missing}, 変更={changed}, 要確認={manualReview}, キュー={queued}");
        }
        catch (Exception ex)
        {
            this.reflectionStatus = "接続エラー: " + ex.GetType().Name;
            this.log.Error(ex, "[PJH/PluginInstaller] Plugin Installer全Manifest走査に失敗");
        }
    }

    private static List<object> CollectRemoteManifests(object installer, out int availableCount, out int installedCount, out int rawCount)
    {
        var result = new List<object>();
        availableCount = 0;
        installedCount = 0;
        rawCount = 0;

        var installerType = installer.GetType();
        var availableField = installerType.GetField("pluginListAvailable", BindingFlags.Instance | BindingFlags.NonPublic);
        var installedField = installerType.GetField("pluginListInstalled", BindingFlags.Instance | BindingFlags.NonPublic);

        if (availableField?.GetValue(installer) is IEnumerable available)
        {
            foreach (var manifest in available)
            {
                if (manifest == null) continue;
                availableCount++;
                rawCount++;
                var internalName = ReadString(manifest.GetType(), manifest, "InternalName");
                if (!string.IsNullOrWhiteSpace(internalName))
                    result.Add(manifest);
            }
        }

        // Privacy rule: Local manifests are deliberately excluded from translation and diff checks.
        // Count installed entries only; do not inspect their Manifest/name/description.
        if (installedField?.GetValue(installer) is IEnumerable installed)
        {
            foreach (var local in installed)
            {
                if (local == null) continue;
                installedCount++;
            }
        }

        // Fallback for a future Dalamud internal change. Remote manifests only.
        if (result.Count == 0)
        {
            var gatherMethod = installerType.GetMethod("GatherProxies", BindingFlags.Instance | BindingFlags.NonPublic);
            if (gatherMethod?.Invoke(installer, null) is IEnumerable proxies)
            {
                foreach (var proxy in proxies)
                {
                    if (proxy == null) continue;
                    rawCount++;
                    var manifest = ResolveRemoteManifestFromProxy(proxy);
                    if (manifest == null) continue;
                    var internalName = ReadString(manifest.GetType(), manifest, "InternalName");
                    if (!string.IsNullOrWhiteSpace(internalName))
                        result.Add(manifest);
                }
                availableCount = result.Count;
            }
        }

        return result;
    }

    private static Dictionary<string, object> BuildCanonicalManifestMap(IEnumerable<object> manifests)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var manifest in manifests)
        {
            if (manifest == null) continue;
            var internalName = ReadString(manifest.GetType(), manifest, "InternalName");
            if (string.IsNullOrWhiteSpace(internalName)) continue;
            if (!result.TryGetValue(internalName, out var existing) || IsPreferredRemoteManifest(manifest, existing))
                result[internalName] = manifest;
        }
        return result;
    }

    private static bool IsPreferredRemoteManifest(object candidate, object existing)
    {
        static Version ParseVersion(object manifest)
        {
            var raw = ReadObject(manifest.GetType(), manifest, "AssemblyVersion")?.ToString() ?? "0.0.0.0";
            return Version.TryParse(raw, out var version) ? version : new Version(0, 0, 0, 0);
        }

        var cv = ParseVersion(candidate);
        var ev = ParseVersion(existing);
        var versionCompare = cv.CompareTo(ev);
        if (versionCompare != 0) return versionCompare > 0;

        // Deterministic tie-breakers stop the selected manifest from flipping between scans.
        var candidateName = ReadString(candidate.GetType(), candidate, "Name");
        var existingName = ReadString(existing.GetType(), existing, "Name");
        var nameCompare = string.Compare(candidateName, existingName, StringComparison.OrdinalIgnoreCase);
        if (nameCompare != 0) return nameCompare < 0;

        var candidateDescription = ReadString(candidate.GetType(), candidate, "Description");
        var existingDescription = ReadString(existing.GetType(), existing, "Description");
        return string.Compare(candidateDescription, existingDescription, StringComparison.Ordinal) < 0;
    }

    private static object? ResolveRemoteManifestFromProxy(object proxy)
    {
        var proxyType = proxy.GetType();
        return proxyType.GetProperty("RemoteManifest", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(proxy);
    }

    private int ApplyDictionaryToAllRemoteManifests(IEnumerable<object> manifests)
    {
        var applied = 0;
        foreach (var manifest in manifests)
        {
            if (manifest == null) continue;
            var type = manifest.GetType();
            var internalName = ReadString(type, manifest, "InternalName");
            if (string.IsNullOrWhiteSpace(internalName) || !this.dictionary.TryGetValue(internalName, out var entry))
                continue;

            this.EnsureOriginalManifest(manifest);
            this.ApplyDictionaryEntry(manifest, entry);
            applied++;
        }
        return applied;
    }

    private void EnsureOriginalManifest(object manifest)
    {
        lock (this.sync)
        {
            if (this.originals.ContainsKey(manifest)) return;
            var type = manifest.GetType();
            var punchline = ReadString(type, manifest, "Punchline");
            var description = ReadString(type, manifest, "Description");
            this.originals[manifest] = new OriginalManifest(manifest, punchline, description);
        }
    }

    private void ReapplyDictionaryToCurrentRemoteManifests()
    {
        try
        {
            if (!this.TryResolvePluginInstaller(out var installer)) return;
            var manifests = CollectRemoteManifests(installer, out _, out _, out _);
            if (manifests.Count == 0) return;
            this.ApplyDictionaryToAllRemoteManifests(manifests);
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "[PJH/PluginInstaller] Remote Manifest表示再適用に失敗");
        }
    }

    private int ApplyDictionaryToInstalledPublicManifests(object installer, IEnumerable<string> remoteInternalNames)
    {
        var remoteNames = new HashSet<string>(remoteInternalNames, StringComparer.OrdinalIgnoreCase);
        if (remoteNames.Count == 0)
            return 0;

        var installerType = installer.GetType();
        var installedField = installerType.GetField("pluginListInstalled", BindingFlags.Instance | BindingFlags.NonPublic);
        if (installedField?.GetValue(installer) is not IEnumerable installed)
            return 0;

        var applied = 0;
        foreach (var localPlugin in installed)
        {
            if (localPlugin == null)
                continue;

            try
            {
                var localType = localPlugin.GetType();
                var localManifest = localType.GetProperty("Manifest", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(localPlugin)
                    ?? localType.GetField("Manifest", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(localPlugin);
                if (localManifest == null)
                    continue;

                var manifestType = localManifest.GetType();
                var internalName = ReadString(manifestType, localManifest, "InternalName");
                if (string.IsNullOrWhiteSpace(internalName) || !remoteNames.Contains(internalName))
                    continue; // Local-only/private plugin: never touch it.

                if (!this.dictionary.TryGetValue(internalName, out var entry))
                    continue;

                // Local Manifest is a DISPLAY TARGET only. Capture its original text solely so
                // OFF/Dispose can restore the UI; never compare/send/save this text.
                lock (this.sync)
                {
                    if (!this.originals.ContainsKey(localManifest))
                    {
                        var originalPunchline = ReadString(manifestType, localManifest, "Punchline");
                        var originalDescription = ReadString(manifestType, localManifest, "Description");
                        this.originals[localManifest] = new OriginalManifest(localManifest, originalPunchline, originalDescription);
                    }
                }

                this.ApplyDictionaryEntry(localManifest, entry);
                applied++;
            }
            catch (Exception ex)
            {
                // Never log Local plugin names/text here; Local-only/private development plugins
                // must not leak into logs.
                this.log.Debug(ex, "[PJH/PluginInstaller] 導入済み表示への辞書適用を1件スキップ");
            }
        }

        return applied;
    }

    private static string GetFieldDisplayName(bool punchline, bool description)
        => punchline && description ? "プラグイン説明 / 詳細説明" : punchline ? "プラグイン説明" : "詳細説明";

    private bool QueueWork(TranslationWork work)
    {
        var key = work.InternalName + "|" + work.Punchline + "|" + work.Description;
        lock (this.sync)
        {
            if (!this.queuedWork.Add(key))
                return false;
            // ここでは翻訳サービスへ送信しない。差分だけ保持し、実送信は明示ボタンから行う。
            this.pendingGoogleWork[key] = work with { QueueKey = key };
        }
        this.log.Information("[PJH/PluginInstaller] 翻訳待ちに追加（未送信）: {Plugin} / Punchline={PunchChanged} Description={DescChanged}", work.InternalName, work.TranslatePunchline, work.TranslateDescription);
        var fields = GetFieldDisplayName(work.TranslatePunchline, work.TranslateDescription);
        this.UpsertUpdateRow(work.InternalName, work.Name, work.Existing == null ? "[新規]" : "[変更]", fields, "[待機]", "未保存", "手動開始待ち");
        this.AddUiLog("待機", $"翻訳待ちに追加: {fields}（翻訳サービスへ未送信）", work.InternalName);
        return true;
    }


    private void ReacquireCommandDescriptions()
    {
        try
        {
            if (!this.TryResolvePluginInstaller(out var installer))
            {
                this.commandReacquireStatus = "Plugin Installerへ接続できません";
                return;
            }

            var manifests = BuildCanonicalManifestMap(CollectRemoteManifests(installer, out _, out _, out _));

            // Plugin に注入される ICommandManager は CommandManagerPluginScoped。
            // Plugin Installer 本体が使っている GetHandlersByAssemblyName は、
            // その内側の CommandManager サービスにあるため、そこだけを取得する。
            object commandManagerObject = this.commandManager;
            var getHandlers = commandManagerObject.GetType().GetMethod("GetHandlersByAssemblyName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (getHandlers == null)
            {
                var innerField = commandManagerObject.GetType().GetField("commandManagerService", BindingFlags.Instance | BindingFlags.NonPublic);
                var inner = innerField?.GetValue(commandManagerObject);
                if (inner != null)
                {
                    commandManagerObject = inner;
                    getHandlers = commandManagerObject.GetType().GetMethod("GetHandlersByAssemblyName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }
            }

            if (getHandlers == null)
            {
                this.commandReacquireStatus = "Dalamudのコマンド一覧を取得できません";
                return;
            }

            var found = 0;
            var queued = 0;
            var applied = 0;
            foreach (var manifest in manifests.Values)
            {
                var mt = manifest.GetType();
                var internalName = ReadString(mt, manifest, "InternalName");
                if (string.IsNullOrWhiteSpace(internalName)) continue;

                if (getHandlers.Invoke(commandManagerObject, new object?[] { internalName }) is not IEnumerable handlers) continue;
                foreach (var item in handlers)
                {
                    if (!TryReadCommandHandler(item, out var command, out var info) || info == null) continue;
                    var help = ReadString(info.GetType(), info, "HelpMessage");
                    var showInHelpObj = ReadObject(info.GetType(), info, "ShowInHelp");
                    var showInHelp = showInHelpObj is bool b ? b : true;
                    if (!showInHelp || string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(help)) continue;
                    found++;

                    var key = internalName + "|" + command;
                    if (this.commandDictionary.TryGetValue(key, out var existing))
                    {
                        if (string.Equals(existing.Source, help, StringComparison.Ordinal))
                        {
                            WriteString(info, "HelpMessage", existing.Japanese);
                            applied++;
                            continue;
                        }
                        if (string.Equals(existing.Japanese, help, StringComparison.Ordinal))
                        {
                            applied++;
                            continue;
                        }
                    }

                    var queueKey = key + "|" + help;
                    lock (this.sync)
                    {
                        if (!this.queuedCommandWork.Add(queueKey)) continue;
                        this.pendingCommandWork[queueKey] = new CommandTranslationWork(info, internalName, command, help, queueKey);
                    }
                    queued++;
                }
            }

            this.commandReacquireStatus = $"再取得 {found}件 / 翻訳待ち {queued}件 / 適用 {applied}件";
            this.AddUiLog("確認", $"コマンド説明を再取得: {found}件 / 翻訳待ち{queued}件 / 既存訳適用{applied}件");
        }
        catch (Exception ex)
        {
            this.commandReacquireStatus = "再取得エラー: " + ex.GetType().Name;
            this.log.Warning(ex, "[PJH/PluginInstaller] コマンド説明の再取得に失敗");
        }
    }

    private static bool TryReadCommandHandler(object item, out string command, out object? info)
    {
        command = string.Empty;
        info = null;
        try
        {
            var itemType = item.GetType();
            var keyObj = ReadObject(itemType, item, "Key");
            if (keyObj == null) return false;
            var keyType = keyObj.GetType();
            command = (keyType.GetField("Item1", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(keyObj)
                ?? keyType.GetProperty("Item1", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(keyObj))?.ToString() ?? string.Empty;
            info = keyType.GetField("Item2", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(keyObj)
                ?? keyType.GetProperty("Item2", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(keyObj);
            return !string.IsNullOrWhiteSpace(command) && info != null;
        }
        catch { return false; }
    }

    private async Task ProcessCommandWorkAsync(CommandTranslationWork work)
    {
        try
        {
            this.translationStatus = $"コマンド説明を翻訳中: {work.Command}";
            var protectedText = ProtectTechnicalText(work.Source, work.InternalName, out var tokens);
            var translated = PostProcessJapanese(RestoreTechnicalText(await this.TranslateSelectedAsync(protectedText, "en", "ja", work.InternalName, "CommandHelp").ConfigureAwait(false), tokens));
            var entry = new CommandTranslationEntry
            {
                InternalName = work.InternalName,
                Command = work.Command,
                Source = work.Source,
                Japanese = translated,
                UpdatedAt = DateTimeOffset.Now,
            };
            lock (this.sync)
                this.commandDictionary[work.InternalName + "|" + work.Command] = entry;
            this.SaveCommandDictionary();
            this.completedCommandTranslations.Enqueue(new CommandTranslationResult(work.CommandInfo, work.QueueKey, entry, null));
        }
        catch (Exception ex)
        {
            this.completedCommandTranslations.Enqueue(new CommandTranslationResult(work.CommandInfo, work.QueueKey, null, ex.Message));
            this.googleFailureCount++;
            if (ex is HttpRequestException && ex.Message.Contains("429", StringComparison.OrdinalIgnoreCase))
            {
                this.stopWorkerAfterRateLimit = true;
                this.workerStopReason = this.TranslationProviderName + "のHTTP 429で停止";
            }
        }
    }

    private void ApplyCompletedCommandTranslations()
    {
        while (this.completedCommandTranslations.TryDequeue(out var result))
        {
            lock (this.sync) this.queuedCommandWork.Remove(result.QueueKey);
            if (result.Error != null || result.Entry == null) continue;
            WriteString(result.CommandInfo, "HelpMessage", result.Entry.Japanese);
            this.commandReacquireStatus = $"翻訳完了: {result.Entry.Command}";
        }
    }

    private void LoadCommandDictionary()
    {
        try
        {
            if (!File.Exists(this.commandDictionaryPath)) return;
            var entries = JsonSerializer.Deserialize<List<CommandTranslationEntry>>(File.ReadAllText(this.commandDictionaryPath, Encoding.UTF8), JsonOptions) ?? [];
            this.commandDictionary.Clear();
            foreach (var entry in entries.Where(x => !string.IsNullOrWhiteSpace(x.InternalName) && !string.IsNullOrWhiteSpace(x.Command)))
                this.commandDictionary[entry.InternalName + "|" + entry.Command] = entry;
        }
        catch (Exception ex) { this.log.Warning(ex, "[PJH/PluginInstaller] コマンド説明辞書の読み込みに失敗"); }
    }

    private void SaveCommandDictionary()
    {
        try
        {
            var entries = this.commandDictionary.Values.OrderBy(x => x.InternalName, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Command, StringComparer.OrdinalIgnoreCase).ToList();
            File.WriteAllText(this.commandDictionaryPath, JsonSerializer.Serialize(entries, JsonOptions), new UTF8Encoding(false));
        }
        catch (Exception ex) { this.log.Warning(ex, "[PJH/PluginInstaller] コマンド説明辞書の保存に失敗"); }
    }

    private void StartPendingTranslations()
    {
        List<TranslationWork> pending;
        List<CommandTranslationWork> pendingCommands;
        lock (this.sync)
        {
            if (this.workerRunning)
            {
                this.translationStatus = "翻訳処理中です";
                return;
            }

            pending = this.pendingGoogleWork.Values.ToList();
            this.pendingGoogleWork.Clear();
            pendingCommands = this.pendingCommandWork.Values.ToList();
            this.pendingCommandWork.Clear();
        }

        if (pending.Count == 0 && pendingCommands.Count == 0)
        {
            this.translationStatus = "翻訳待ちの項目はありません";
            this.AddUiLog("確認", "翻訳待ちの項目はありません");
            return;
        }

        this.stopWorkerAfterRateLimit = false;
        this.stopWorkerAfterProviderFailure = false;
        this.workerStopReason = string.Empty;
        foreach (var work in pending)
            this.translationQueue.Enqueue(work);
        foreach (var work in pendingCommands)
            this.commandTranslationQueue.Enqueue(work);

        var totalPending = pending.Count + pendingCommands.Count;
        this.translationStatus = $"{this.TranslationProviderName}を手動開始: {totalPending} 件";
        this.AddUiLog("開始", $"ユーザー操作で{this.TranslationProviderName}を開始: {totalPending}件");
        this.StartWorkerIfNeeded();
    }

    private void StartWorkerIfNeeded()
    {
        lock (this.sync)
        {
            if (this.workerRunning || (this.translationQueue.IsEmpty && this.commandTranslationQueue.IsEmpty))
                return;
            this.workerRunning = true;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    if (this.translationQueue.TryDequeue(out var work))
                        await this.ProcessWorkAsync(work).ConfigureAwait(false);
                    else if (this.commandTranslationQueue.TryDequeue(out var commandWork))
                        await this.ProcessCommandWorkAsync(commandWork).ConfigureAwait(false);
                    else
                        break;

                    if (this.stopWorkerAfterRateLimit || this.stopWorkerAfterProviderFailure)
                    {
                        var restored = 0;
                        while (this.translationQueue.TryDequeue(out var remaining))
                        {
                            lock (this.sync)
                                this.pendingGoogleWork[remaining.QueueKey] = remaining;
                            restored++;
                        }
                        while (this.commandTranslationQueue.TryDequeue(out var remainingCommand))
                        {
                            lock (this.sync)
                                this.pendingCommandWork[remainingCommand.QueueKey] = remainingCommand;
                            restored++;
                        }
                        var reason = string.IsNullOrWhiteSpace(this.workerStopReason)
                            ? (this.stopWorkerAfterRateLimit ? "HTTP 429で停止" : "翻訳サービスエラーで停止")
                            : this.workerStopReason;
                        this.translationStatus = restored > 0
                            ? $"{reason}（残り {restored} 件は未送信のまま保持）"
                            : reason;
                        this.AddUiLog("停止", restored > 0
                            ? $"{reason}。残り{restored}件は翻訳サービスへ送信していません"
                            : reason);
                        break;
                    }
                }
            }
            finally
            {
                lock (this.sync)
                    this.workerRunning = false;
            }
        });
    }

    private async Task ProcessWorkAsync(TranslationWork work)
    {
        try
        {
            this.translationStatus = $"翻訳中: {work.InternalName}";
            this.UpsertUpdateRow(work.InternalName, work.Name, work.Existing == null ? "[新規]" : "[変更]", "", "[翻訳中]", "未保存", this.TranslationProviderName + "中");
            this.AddUiLog("翻訳", this.TranslationProviderName + "を開始", work.InternalName);
            var jpPunch = work.Existing?.PunchlineJapanese ?? string.Empty;
            var jpDesc = work.Existing?.DescriptionJapanese ?? string.Empty;

            if (work.TranslatePunchline && !string.IsNullOrWhiteSpace(work.Punchline))
            {
                var protectedText = ProtectTechnicalText(work.Punchline, work.InternalName, out var tokens);
                jpPunch = PostProcessJapanese(RestoreTechnicalText(await this.TranslateSelectedAsync(protectedText, "en", "ja", work.InternalName, "Punchline").ConfigureAwait(false), tokens));
            }
            else if (string.IsNullOrWhiteSpace(work.Punchline))
            {
                jpPunch = string.Empty;
            }

            if (work.TranslateDescription && !string.IsNullOrWhiteSpace(work.Description))
            {
                var protectedText = ProtectTechnicalText(work.Description, work.InternalName, out var tokens);
                jpDesc = PostProcessJapanese(RestoreTechnicalText(await this.TranslateSelectedAsync(protectedText, "en", "ja", work.InternalName, "Description").ConfigureAwait(false), tokens));
            }
            else if (string.IsNullOrWhiteSpace(work.Description))
            {
                jpDesc = string.Empty;
            }

            var entry = new TranslationDictionaryEntry
            {
                InternalName = work.InternalName,
                Name = work.Name,
                AssemblyVersion = work.Version,
                PunchlineSource = work.Punchline,
                PunchlineJapanese = jpPunch,
                DescriptionSource = work.Description,
                DescriptionJapanese = jpDesc,
                AutoTranslated = true,
                UpdatedAt = DateTimeOffset.Now,
            };

            lock (this.sync)
            {
                this.dictionary[work.InternalName] = entry;
                this.dictionaryCount = this.dictionary.Count;
            }
            this.SaveDictionary();
            this.completed.Enqueue(new TranslationResult(work.Manifest, work.QueueKey, entry, null));
            this.log.Information("[PJH/PluginInstaller] 辞書追記/更新成功: {Plugin}", work.InternalName);
            this.UpsertUpdateRow(work.InternalName, work.Name, work.Existing == null ? "[新規]" : "[変更]", "", "[翻訳済]", "[保存済]", "完了");
            this.AddUiLog("完了", this.TranslationProviderName + "成功・辞書へ保存", work.InternalName);
        }
        catch (Exception ex)
        {
            this.completed.Enqueue(new TranslationResult(work.Manifest, work.QueueKey, null, ex.Message));
            this.log.Warning(ex, "[PJH/PluginInstaller] 翻訳失敗: {Plugin}", work.InternalName);
            this.googleFailureCount++;
            if (ex is TaskCanceledException)
            {
                this.googleStatus = "タイムアウト";
                this.googleStatusDetail = this.TranslationProviderName + "から時間内に応答がありませんでした";
            }
            else if (ex is HttpRequestException && ex.Message.Contains("429", StringComparison.OrdinalIgnoreCase))
            {
                this.stopWorkerAfterRateLimit = true;
                this.workerStopReason = this.TranslationProviderName + "のHTTP 429で停止";
                this.googleStatus = "利用制限中（HTTP 429）";
                this.googleStatusDetail = this.TranslationProviderName + "側の利用制限です。自動再試行は行いません";
            }
            else if (ex is HttpRequestException)
            {
                this.googleStatus = "接続エラー";
                this.googleStatusDetail = ex.Message;
                if (this.UsePrivateServer)
                {
                    this.stopWorkerAfterProviderFailure = true;
                    this.workerStopReason = "PJH中継サーバーを利用できないため停止";
                }
            }
            else
            {
                this.googleStatus = "翻訳結果エラー";
                this.googleStatusDetail = ex.Message;
            }
            this.UpsertUpdateRow(work.InternalName, work.Name, work.Existing == null ? "[新規]" : "[変更]", "", "[エラー]", "未保存", this.googleStatusDetail);
            this.AddUiLog("エラー", this.googleStatus + ": " + this.googleStatusDetail, work.InternalName);
        }
    }

    internal async Task<(Dictionary<string, string> Translations, int Skipped, int Failed)> TranslateDictionaryBatchAsync(
        string pluginName,
        string[] texts,
        Func<string, string?> getSkipReason,
        IReadOnlyCollection<string>? protectedNames = null,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var translations = new Dictionary<string, string>(StringComparer.Ordinal);
        var skipped = 0;
        var failed = 0;
        foreach (var text in texts)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                progress?.Invoke("[停止] ユーザー操作で自動翻訳を停止しました。途中までの翻訳結果は保持します。");
                break;
            }

            var skipReason = getSkipReason(text);
            if (!string.IsNullOrWhiteSpace(skipReason))
            {
                skipped++;
                progress?.Invoke($"[スキップ] {skipReason}: {text}");
                continue;
            }

            if (IsProtectedDictionaryIdentity(text, protectedNames))
            {
                skipped++;
                progress?.Invoke($"[保護] プラグイン名・作者名などの固有名詞: {text}");
                continue;
            }

            try
            {
                progress?.Invoke($"[翻訳] {text}");
                var translated = await TranslateDictionaryTextAsync(text, pluginName, protectedNames, progress, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(translated) && !string.Equals(translated, text, StringComparison.Ordinal))
                {
                    translations[text] = translated;
                    progress?.Invoke($"  → {translated}");
                }
                else
                {
                    skipped++;
                    progress?.Invoke("  → 翻訳結果に変化がないため対象外");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                progress?.Invoke("[停止] ユーザー操作で自動翻訳を停止しました。途中までの翻訳結果は保持します。");
                break;
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("429", StringComparison.OrdinalIgnoreCase))
            {
                failed++;
                progress?.Invoke($"[失敗] {text} / {ex.Message}");
                progress?.Invoke($"[停止] {this.TranslationProviderName} のHTTP 429のため、この自動翻訳を停止しました。途中までの翻訳結果は保持します。");
                break;
            }
            catch (Exception ex)
            {
                failed++;
                progress?.Invoke($"[失敗] {text} / {ex.Message}");
            }
        }

        progress?.Invoke($"[完了] 翻訳 {translations.Count}件 / 対象外 {skipped}件 / 失敗 {failed}件");
        return (translations, skipped, failed);
    }

    internal async Task<string> TranslateDictionaryTextAsync(
        string text,
        string pluginName,
        IReadOnlyCollection<string>? protectedNames = null,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var protectedText = ProtectTechnicalText(text, pluginName, out var tokens, protectedNames);
        if (!string.Equals(protectedText, text, StringComparison.Ordinal))
            progress?.Invoke("  [保護] FF14用語・プラグイン名・URL/コマンド/ImGui IDを保護");
        var translated = await this.TranslateSelectedAsync(protectedText, "auto", "ja", pluginName, "未翻訳辞書", cancellationToken, progress).ConfigureAwait(false);
        return PostProcessJapanese(RestoreTechnicalText(translated, tokens));
    }

    private static bool IsProtectedDictionaryIdentity(string text, IReadOnlyCollection<string>? protectedNames)
    {
        var value = text.Trim();
        if (protectedNames != null && protectedNames.Any(x => string.Equals(value, x, StringComparison.OrdinalIgnoreCase))) return true;
        return Regex.IsMatch(value, @"^(?:by|author|created by|developer|maintainer)\s*[:\-]?\s*.+$", RegexOptions.IgnoreCase);
    }

    private async Task<string> TranslateSelectedAsync(string text, string source, string target, string pluginName, string fieldName, CancellationToken cancellationToken = default, Action<string>? progress = null)
    {
        if (!this.UsePrivateServer)
            return await this.TranslateGoogleAsync(text, source, target, pluginName, fieldName, cancellationToken, progress).ConfigureAwait(false);

        var now = DateTimeOffset.Now;
        var cacheFresh = this.relayGoogleAvailable.HasValue &&
                         now - this.relayGoogleAvailabilityCheckedAt < RelayGoogleAvailabilityCache;

        if (!cacheFresh)
        {
            progress?.Invoke("[自動選択] Google翻訳が利用可能か1回だけ確認します…");
            var result = await this.CheckGoogleAvailabilityAsync("PJH中継サーバー選択時の自動確認", progress, cancellationToken).ConfigureAwait(false);
            this.relayGoogleAvailable = string.Equals(result, "利用可能", StringComparison.Ordinal);
            this.relayGoogleAvailabilityCheckedAt = DateTimeOffset.Now;
        }

        if (this.relayGoogleAvailable == true)
        {
            progress?.Invoke("[自動選択] Google翻訳が利用可能なため、Google翻訳を使用します。");
            try
            {
                return await this.TranslateGoogleAsync(text, source, target, pluginName, fieldName, cancellationToken, progress).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                this.relayGoogleAvailable = false;
                this.relayGoogleAvailabilityCheckedAt = DateTimeOffset.Now;
                progress?.Invoke($"[自動切替] Google翻訳に失敗したため、PJH中継サーバーへ切り替えます。({ex.Message})");
            }
        }
        else
        {
            progress?.Invoke("[自動選択] Google翻訳を利用できないため、PJH中継サーバーを使用します。");
        }

        return await this.TranslatePrivateServerAsync(text, source, target, pluginName, fieldName, cancellationToken, progress).ConfigureAwait(false);
    }

    private async Task<string> TranslatePrivateServerAsync(string text, string source, string target, string pluginName, string fieldName, CancellationToken cancellationToken = default, Action<string>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var chunks = SplitTranslationText(text, 850);
        var output = new StringBuilder(text.Length + 64);
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            if (string.IsNullOrEmpty(chunk)) continue;
            output.Append(await this.TranslatePrivateServerSingleAsync(chunk, source, target, pluginName, fieldName, i + 1, chunks.Count, cancellationToken, progress).ConfigureAwait(false));
        }
        return output.ToString();
    }

    private async Task<string> TranslatePrivateServerSingleAsync(string text, string source, string target, string pluginName, string fieldName, int chunkNo, int chunkTotal, CancellationToken cancellationToken = default, Action<string>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configured = PjhRelayBaseUrl;

        var uiFieldName = fieldName == "Punchline" ? "プラグイン説明" : fieldName == "Description" ? "詳細説明" : fieldName;
        var endpoint = configured.TrimEnd('/');
        if (!endpoint.EndsWith("/translate", StringComparison.OrdinalIgnoreCase)) endpoint += "/translate";

        this.googleStatus = "通信中";
        this.googleStatusDetail = $"{pluginName} / {uiFieldName}";
        this.log.Information("[PJH/PluginInstaller] PJH中継送信: {Plugin} {Field} chunk {Chunk}/{Total} chars={Chars}", pluginName, fieldName, chunkNo, chunkTotal, text.Length);
        this.AddUiLog("PJH中継送信", $"{uiFieldName} chunk {chunkNo}/{chunkTotal} / {text.Length}文字", pluginName);

        var payload = JsonSerializer.Serialize(new { text, q = text, source, target, format = "text" });
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        using var response = await this.http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new HttpRequestException("HTTP 429 Too Many Requests");
        if ((int)response.StatusCode == 402 || response.StatusCode == HttpStatusCode.Forbidden)
        {
            this.googleStatus = "月間上限到達";
            this.googleStatusDetail = "PJH中継サーバーの月間安全上限に到達したため停止しました";
            throw new HttpRequestException("月間無料枠の安全上限に到達しました");
        }
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;
        string? translated = null;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("translatedText", out var translatedText) && translatedText.ValueKind == JsonValueKind.String)
            translated = translatedText.GetString();
        else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("translation", out var translation) && translation.ValueKind == JsonValueKind.String)
            translated = translation.GetString();

        if (string.IsNullOrWhiteSpace(translated))
            throw new InvalidOperationException("翻訳結果形式を認識できません");

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("usedChars", out var used) && used.TryGetInt64(out var usedChars) &&
            root.TryGetProperty("limitChars", out var limit) && limit.TryGetInt64(out var limitChars))
        {
            this.googleStatusDetail = $"今月 {usedChars:N0} / {limitChars:N0}文字 / 残り {Math.Max(0, limitChars - usedChars):N0}文字";
        }
        else
        {
            this.googleStatusDetail = $"最終成功: {DateTimeOffset.Now:HH:mm:ss} / {pluginName} / {uiFieldName}";
        }

        this.googleStatus = "利用可能";
        this.log.Information("[PJH/PluginInstaller] PJH中継成功: {Plugin} {Field} chunk {Chunk}/{Total}", pluginName, fieldName, chunkNo, chunkTotal);
        this.AddUiLog("PJH中継成功", $"{uiFieldName} chunk {chunkNo}/{chunkTotal}", pluginName);
        return translated;
    }

    private async Task<string> TranslateGoogleAsync(string text, string source, string target, string pluginName, string fieldName, CancellationToken cancellationToken = default, Action<string>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var chunks = SplitTranslationText(text, 850);
        var output = new StringBuilder(text.Length + 64);
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            if (string.IsNullOrEmpty(chunk))
                continue;
            output.Append(await this.TranslateGoogleSingleAsync(chunk, source, target, pluginName, fieldName, i + 1, chunks.Count, cancellationToken, progress).ConfigureAwait(false));
        }
        return output.ToString();
    }

    private async Task<string> TranslateGoogleSingleAsync(string text, string source, string target, string pluginName, string fieldName, int chunkNo, int chunkTotal, CancellationToken cancellationToken = default, Action<string>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uiFieldName = fieldName == "Punchline" ? "プラグイン説明" : fieldName == "Description" ? "詳細説明" : fieldName;

        // Google が automated queries と判定した 429 に対して自動再試行を繰り返すと、
        // 制限を長引かせる可能性があるため、1回の429で停止してユーザーへ明示する。
        var sinceLast = DateTimeOffset.Now - this.lastGoogleRequest;
        var minGap = TimeSpan.FromSeconds(3);
        if (sinceLast < minGap)
            await Task.Delay(minGap - sinceLast, cancellationToken).ConfigureAwait(false);

        var url = "https://translate.googleapis.com/translate_a/single?client=gtx&dt=t&sl=" + source + "&tl=" + target + "&q=" + Uri.EscapeDataString(text);
        this.googleStatus = "通信中";
        this.googleStatusDetail = $"{pluginName} / {uiFieldName}";
        this.log.Information("[PJH/PluginInstaller] Google送信: {Plugin} {Field} chunk {Chunk}/{Total} chars={Chars}", pluginName, fieldName, chunkNo, chunkTotal, text.Length);
        this.AddUiLog("Google送信", $"{uiFieldName} chunk {chunkNo}/{chunkTotal} / {text.Length}文字", pluginName);
        this.lastGoogleRequest = DateTimeOffset.Now;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", "application/json,text/plain,*/*");
        request.Headers.TryAddWithoutValidation("Accept-Language", "ja,en-US;q=0.9,en;q=0.8");
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/152.0.0.0 Safari/537.36");
        request.Headers.Referrer = new Uri("https://translate.google.com/");
        progress?.Invoke($"[Google診断] GET translate.googleapis.com / client=gtx sl={source} tl={target} q={text.Length}文字");
        progress?.Invoke($"[Google診断] Request HTTP/{request.Version} UA=Chrome152 Accept=application/json,text/plain,*/* Referer=https://translate.google.com/");
        using var response = await this.http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var server = response.Headers.Server?.ToString();
        var retryHeader = response.Headers.RetryAfter?.ToString();
        progress?.Invoke($"[Google診断] Response HTTP/{response.Version} {(int)response.StatusCode} {response.ReasonPhrase} Server={server ?? "(なし)"} Retry-After={retryHeader ?? "(なし)"}");

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta;
            this.rateLimitedUntil = retryAfter is { } wait ? DateTimeOffset.Now + wait : null;
            this.googleStatus = "利用制限中（HTTP 429）";
            this.googleStatusDetail = retryAfter is { } wait2
                ? $"Google側が自動アクセスを拒否しました。Retry-After: 約{Math.Ceiling(wait2.TotalSeconds)}秒"
                : "Google側が自動アクセスを拒否しました。Retry-After 指定なし";
            this.googleFailureCount++;
            this.log.Warning("[PJH/PluginInstaller] Google 429: {Plugin} {Field} chunk {Chunk}/{Total}. RetryAfter={RetryAfter}", pluginName, fieldName, chunkNo, chunkTotal, retryAfter?.TotalSeconds ?? -1d);
            this.AddUiLog("429", this.googleStatusDetail, pluginName);
            progress?.Invoke("[Google 429] HTTP 429 Too Many Requests / Google側が自動アクセスを拒否しました。自動再試行は行いません。");
            if (!string.IsNullOrWhiteSpace(responseBody))
                progress?.Invoke($"[Google応答] {TrimForLog(responseBody, 300)}");
            throw new HttpRequestException("Google Translate HTTP 429 (Too Many Requests) / 自動再試行停止");
        }

        if (!response.IsSuccessStatusCode)
        {
            this.googleStatus = $"エラー（HTTP {(int)response.StatusCode}）";
            this.googleStatusDetail = response.ReasonPhrase ?? "HTTPエラー";
            this.googleFailureCount++;
            this.AddUiLog("Googleエラー", $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", pluginName);
            progress?.Invoke($"[Googleエラー] HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            if (!string.IsNullOrWhiteSpace(responseBody))
                progress?.Invoke($"[Google応答] {TrimForLog(responseBody, 300)}");
            throw new HttpRequestException($"Google Translate HTTP {(int)response.StatusCode} ({response.ReasonPhrase}) / {text.Length} chars");
        }

        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0 || root[0].ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("翻訳結果の形式が不明です");
        var sb = new StringBuilder();
        foreach (var seg in root[0].EnumerateArray())
        {
            if (seg.ValueKind == JsonValueKind.Array && seg.GetArrayLength() > 0 && seg[0].ValueKind == JsonValueKind.String)
                sb.Append(seg[0].GetString());
        }
        this.rateLimitedUntil = null;
        this.googleStatus = "利用可能";
        this.googleStatusDetail = $"最終成功: {DateTimeOffset.Now:HH:mm:ss} / {pluginName} / {uiFieldName}";
        this.log.Information("[PJH/PluginInstaller] Google成功: {Plugin} {Field} chunk {Chunk}/{Total}", pluginName, fieldName, chunkNo, chunkTotal);
        this.AddUiLog("Google成功", $"{uiFieldName} chunk {chunkNo}/{chunkTotal}", pluginName);
        return sb.ToString();
    }

    internal Task<string> CheckSelectedTranslationAvailabilityAsync(string context, Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        return this.UsePrivateServer
            ? this.CheckPrivateServerAvailabilityAsync(context, progress, cancellationToken)
            : this.CheckGoogleAvailabilityAsync(context, progress, cancellationToken);
    }

    private async Task<string> CheckPrivateServerAvailabilityAsync(string context, Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        this.googleStatus = "利用可否を確認中";
        this.googleStatusDetail = context;
        progress?.Invoke("[PJH中継確認] 利用可能か確認しています…");
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            var configured = PjhRelayBaseUrl;
            var endpoint = configured.TrimEnd('/') + "/status";
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            using var response = await this.http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
                throw new InvalidOperationException("状態応答を認識できません");

            if (root.TryGetProperty("usedChars", out var used) && used.TryGetInt64(out var usedChars) &&
                root.TryGetProperty("limitChars", out var limit) && limit.TryGetInt64(out var limitChars))
            {
                this.googleStatusDetail = $"今月 {usedChars:N0} / {limitChars:N0}文字 / 残り {Math.Max(0, limitChars - usedChars):N0}文字";
            }
            else
            {
                this.googleStatusDetail = $"確認成功: {DateTimeOffset.Now:HH:mm:ss} / {context}";
            }
            this.googleStatus = "利用可能";
            progress?.Invoke("[PJH中継確認] 利用可能です。翻訳文字数は消費していません。");
            return "利用可能";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            this.googleStatus = "タイムアウト";
            this.googleStatusDetail = "PJH中継サーバーから12秒以内に応答がありませんでした";
            progress?.Invoke("[PJH中継エラー] タイムアウト（12秒）");
            return "タイムアウト";
        }
        catch (Exception ex)
        {
            this.googleStatus = ex is HttpRequestException ? "通信エラー" : "状態確認エラー";
            this.googleStatusDetail = ex.Message;
            progress?.Invoke($"[PJH中継エラー] {ex.Message}");
            return this.googleStatus + ": " + ex.Message;
        }
    }

    internal async Task<string> CheckGoogleAvailabilityAsync(string context, Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        // 利用可能チェックは「現在、本当に通信できるか」を1回だけ確認する。
        // 過去の429待機状態を参照して即NGにはせず、必ず実通信する。
        this.googleStatus = "利用可否を確認中";
        this.googleStatusDetail = context;
        progress?.Invoke("[Google確認] 利用可能か確認しています…");
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            var url = "https://translate.googleapis.com/translate_a/single?client=gtx&dt=t&sl=en&tl=ja&q=Hello";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Accept", "application/json,text/plain,*/*");
            request.Headers.TryAddWithoutValidation("Accept-Language", "ja,en-US;q=0.9,en;q=0.8");
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/152.0.0.0 Safari/537.36");
            request.Headers.Referrer = new Uri("https://translate.google.com/");
            progress?.Invoke($"[Google診断] GET {url}");
            progress?.Invoke($"[Google診断] Request HTTP/{request.Version} UA=Chrome152 Accept=application/json,text/plain,*/* Referer=https://translate.google.com/");
            using var response = await this.http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            var server = response.Headers.Server?.ToString();
            var retry = response.Headers.RetryAfter?.ToString();
            progress?.Invoke($"[Google診断] Response HTTP/{response.Version} {(int)response.StatusCode} {response.ReasonPhrase} Server={server ?? "(なし)"} Retry-After={retry ?? "(なし)"}");

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta;
                this.googleStatus = "利用制限中（HTTP 429）";
                this.googleStatusDetail = retryAfter is { } wait
                    ? $"Google側が自動アクセスを拒否しました。Retry-After: 約{Math.Ceiling(wait.TotalSeconds)}秒"
                    : "Google側が自動アクセスを拒否しました。Retry-After 指定なし";
                var waitText = retryAfter is { } retryDelay
                    ? $" / Retry-After 約{Math.Ceiling(retryDelay.TotalSeconds)}秒"
                    : " / Retry-After 指定なし";
                this.relayGoogleAvailable = false;
                this.relayGoogleAvailabilityCheckedAt = DateTimeOffset.Now;
                progress?.Invoke($"[Google 429] HTTP 429 Too Many Requests{waitText}");
                if (!string.IsNullOrWhiteSpace(responseBody))
                    progress?.Invoke($"[Google応答] {TrimForLog(responseBody, 300)}");
                return $"429: 自動アクセス拒否{waitText}";
            }

            if (!response.IsSuccessStatusCode)
            {
                this.relayGoogleAvailable = false;
                this.relayGoogleAvailabilityCheckedAt = DateTimeOffset.Now;
                this.googleStatus = $"エラー（HTTP {(int)response.StatusCode}）";
                this.googleStatusDetail = response.ReasonPhrase ?? "HTTPエラー";
                progress?.Invoke($"[Googleエラー] HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                if (!string.IsNullOrWhiteSpace(responseBody))
                    progress?.Invoke($"[Google応答] {TrimForLog(responseBody, 300)}");
                return $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}";
            }

            // 正常応答まで確認できた場合だけ、過去の429待機情報を stale と判断して解除する。
            this.rateLimitedUntil = null;
            this.relayGoogleAvailable = true;
            this.relayGoogleAvailabilityCheckedAt = DateTimeOffset.Now;
            this.googleStatus = "利用可能";
            this.googleStatusDetail = $"確認成功: {DateTimeOffset.Now:HH:mm:ss} / {context}";
            progress?.Invoke("[Google確認] 利用可能です。");
            return "利用可能";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            this.relayGoogleAvailable = false;
            this.relayGoogleAvailabilityCheckedAt = DateTimeOffset.Now;
            this.googleStatus = "タイムアウト";
            this.googleStatusDetail = "Google翻訳から12秒以内に応答がありませんでした";
            progress?.Invoke("[Googleエラー] タイムアウト（12秒）");
            return "タイムアウト";
        }
        catch (HttpRequestException ex)
        {
            this.relayGoogleAvailable = false;
            this.relayGoogleAvailabilityCheckedAt = DateTimeOffset.Now;
            this.googleStatus = "通信エラー";
            this.googleStatusDetail = ex.Message;
            progress?.Invoke($"[Googleエラー] 通信エラー: {ex.Message}");
            return "通信エラー: " + ex.Message;
        }
    }

    private static string TrimForLog(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text;
        return text[..maxLength] + "…";
    }

    private void ApplyCompletedTranslations()
    {
        while (this.completed.TryDequeue(out var result))
        {
            lock (this.sync)
                this.queuedWork.Remove(result.QueueKey);

            if (result.Error != null)
            {
                this.translationStatus = "翻訳エラー: " + result.Error;
                continue;
            }
            if (result.Entry != null && this.config.TranslateInstallerDescriptions)
            {
                this.ApplyDictionaryEntry(result.Manifest, result.Entry);
                this.translationStatus = "翻訳完了・辞書へ保存済み";
            }
        }
    }

    private void ApplyDictionaryEntry(object manifest, TranslationDictionaryEntry entry)
    {
        try
        {
            OriginalManifest? original;
            lock (this.sync)
                this.originals.TryGetValue(manifest, out original);
            if (original == null)
                return;

            var punch = entry.PunchlineJapanese ?? string.Empty;
            var desc = entry.DescriptionJapanese ?? string.Empty;
            if (this.config.ShowOriginalBelowTranslation)
            {
                if (!string.IsNullOrWhiteSpace(original.Punchline) && !string.IsNullOrWhiteSpace(punch))
                    punch += "\n[EN] " + original.Punchline;
                if (!string.IsNullOrWhiteSpace(original.Description) && !string.IsNullOrWhiteSpace(desc))
                    desc += "\n\n[原文]\n" + original.Description;
            }
            WriteString(manifest, "Punchline", punch);
            WriteString(manifest, "Description", desc);
            lock (this.sync)
            {
                this.translatedManifests.Add(manifest);
                this.translatedManifestCount = this.translatedManifests.Count;
            }
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "[PJH/PluginInstaller] 辞書適用失敗: {Plugin}", entry.InternalName);
        }
    }

    private void RestoreAll()
    {
        lock (this.sync)
        {
            foreach (var original in this.originals.Values)
            {
                try
                {
                    WriteString(original.Manifest, "Punchline", original.Punchline);
                    WriteString(original.Manifest, "Description", original.Description);
                }
                catch { }
            }
            this.originals.Clear();
            this.translatedManifests.Clear();
            this.translatedManifestCount = 0;

            foreach (var pair in this.originalChangelogTexts)
            {
                try { TryWriteChangelogText(pair.Key, pair.Value); } catch { }
            }
            this.originalChangelogTexts.Clear();
        }
    }

    private void StartChangelogTranslation()
    {
        if (this.changelogTranslationTask is { IsCompleted: false })
            return;

        if (!this.config.TranslateInstallerDescriptions)
        {
            this.changelogTranslationStatus = "Plugin Installer日本語化をONにしてください";
            return;
        }

        if (!this.TryResolvePluginInstaller(out var installer))
        {
            this.changelogTranslationStatus = "Plugin Installerへ接続できません";
            return;
        }

        // 更新履歴はまず全件を取得し、翻訳済みを除外した未翻訳件数を把握する。
        // 1回の送信は新しい順から最大50件。次に押した時は翻訳済みを飛ばして次の50件へ進む。
        var untranslatedEntries = CollectCurrentChangelogEntries(installer)
            .Where(x => !string.IsNullOrWhiteSpace(x.Text) && Regex.IsMatch(x.Text, "[A-Za-z]"))
            .Where(x => !this.originalChangelogTexts.ContainsKey(x.Entry))
            .ToList();

        this.changelogUntranslatedBeforeBatch = untranslatedEntries.Count;
        var entries = untranslatedEntries.Take(50).ToList();

        if (entries.Count == 0)
        {
            this.changelogTranslationStatus = "未翻訳 0件 / すべて翻訳済み";
            return;
        }

        this.changelogTranslationCts?.Dispose();
        this.changelogTranslationCts = new CancellationTokenSource();
        var token = this.changelogTranslationCts.Token;
        this.changelogTranslationTotal = entries.Count;
        this.changelogTranslationCompleted = 0;
        this.changelogTranslationFailed = 0;
        this.changelogTranslationStatus = $"未翻訳 {this.changelogUntranslatedBeforeBatch}件 / 今回 {entries.Count}件 / 翻訳中 0/{entries.Count}";
        this.AddUiLog("更新履歴", $"更新履歴の翻訳を開始: 未翻訳 {this.changelogUntranslatedBeforeBatch}件 / 今回 {entries.Count}件");

        this.changelogTranslationTask = this.TranslateChangelogEntriesAsync(entries, token);
    }

    private void StopChangelogTranslation()
    {
        if (this.changelogTranslationTask is not { IsCompleted: false })
            return;
        this.changelogTranslationStatus = "停止中…";
        this.changelogTranslationCts?.Cancel();
    }

    private async Task TranslateChangelogEntriesAsync(List<ChangelogWorkItem> entries, CancellationToken cancellationToken)
    {
        foreach (var item in entries)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                var protectedNames = new List<string>();
                if (!string.IsNullOrWhiteSpace(item.Title)) protectedNames.Add(item.Title);
                if (!string.IsNullOrWhiteSpace(item.Author)) protectedNames.Add(item.Author);
                foreach (Match match in Regex.Matches(item.Text, @"\(by\s+[^)]+\)", RegexOptions.IgnoreCase))
                    protectedNames.Add(match.Value);

                var protectedText = ProtectTechnicalText(item.Text, item.Title, out var tokens, protectedNames);
                string translated;
                try
                {
                    translated = await this.TranslateSelectedAsync(
                        protectedText, "en", "ja", item.Title, "更新履歴", cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException ex) when (!this.UsePrivateServer && ex.Message.Contains("429", StringComparison.OrdinalIgnoreCase))
                {
                    // 更新履歴翻訳だけは、Google選択中でも429時にPJH中継へ1回だけ切り替える。
                    // 429後にGoogleへ自動再試行はしない。
                    this.AddUiLog("更新履歴", "Google翻訳がHTTP 429のためPJH中継サーバーへ切替", item.Title);
                    translated = await this.TranslatePrivateServerAsync(
                        protectedText, "en", "ja", item.Title, "更新履歴", cancellationToken).ConfigureAwait(false);
                }
                translated = PostProcessJapanese(RestoreTechnicalText(translated, tokens));

                if (!string.IsNullOrWhiteSpace(translated) && !string.Equals(translated, item.Text, StringComparison.Ordinal))
                    this.completedChangelogTranslations.Enqueue(new ChangelogTranslationResult(item.Entry, item.Text, translated, null));
                else
                    this.completedChangelogTranslations.Enqueue(new ChangelogTranslationResult(item.Entry, item.Text, null, "翻訳結果に変化なし"));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("429", StringComparison.OrdinalIgnoreCase))
            {
                this.completedChangelogTranslations.Enqueue(new ChangelogTranslationResult(item.Entry, item.Text, null, ex.Message));
                break;
            }
            catch (Exception ex)
            {
                this.completedChangelogTranslations.Enqueue(new ChangelogTranslationResult(item.Entry, item.Text, null, ex.Message));
            }
        }

        if (cancellationToken.IsCancellationRequested)
            this.changelogTranslationStatus = $"停止しました（{this.changelogTranslationCompleted} / {this.changelogTranslationTotal}件）";
    }

    private void ApplyCompletedChangelogTranslations()
    {
        while (this.completedChangelogTranslations.TryDequeue(out var result))
        {
            if (result.TranslatedText != null)
            {
                lock (this.sync)
                {
                    if (!this.originalChangelogTexts.ContainsKey(result.Entry))
                        this.originalChangelogTexts[result.Entry] = result.OriginalText;
                }

                if (TryWriteChangelogText(result.Entry, result.TranslatedText))
                {
                    this.changelogTranslationCompleted++;
                    this.AddUiLog("更新履歴", "翻訳を反映", ReadString(result.Entry.GetType(), result.Entry, "Title"));
                }
                else
                {
                    this.changelogTranslationFailed++;
                    this.AddUiLog("更新履歴エラー", "更新履歴Textへ反映できませんでした");
                }
            }
            else
            {
                this.changelogTranslationFailed++;
                this.AddUiLog("更新履歴エラー", result.Error ?? "翻訳失敗", ReadString(result.Entry.GetType(), result.Entry, "Title"));
            }
        }

        if (this.changelogTranslationTask is { IsCompleted: true })
        {
            var remaining = Math.Max(0, this.changelogUntranslatedBeforeBatch - this.changelogTranslationCompleted);
            if (this.changelogTranslationCts?.IsCancellationRequested == true)
                this.changelogTranslationStatus = $"停止 {this.changelogTranslationCompleted}/{this.changelogTranslationTotal}件 / 残り未翻訳 {remaining}件";
            else if (this.changelogTranslationFailed > 0)
                this.changelogTranslationStatus = $"今回 {this.changelogTranslationCompleted}件完了 / 失敗・対象外 {this.changelogTranslationFailed}件 / 残り未翻訳 {remaining}件";
            else
                this.changelogTranslationStatus = $"今回 {this.changelogTranslationCompleted}件完了 / 残り未翻訳 {remaining}件";
        }
        else if (this.changelogTranslationTask is { IsCompleted: false })
        {
            var processed = this.changelogTranslationCompleted + this.changelogTranslationFailed;
            this.changelogTranslationStatus = $"未翻訳 {this.changelogUntranslatedBeforeBatch}件 / 今回 {this.changelogTranslationTotal}件 / 翻訳中 {processed}/{this.changelogTranslationTotal}";
        }
    }

    private static List<ChangelogWorkItem> CollectCurrentChangelogEntries(object installer)
    {
        var result = new List<ChangelogWorkItem>();
        var installerType = installer.GetType();
        var managerField = installerType.GetField("dalamudChangelogManager", BindingFlags.Instance | BindingFlags.NonPublic);
        var manager = managerField?.GetValue(installer);
        if (manager == null) return result;

        var changelogs = manager.GetType().GetProperty("Changelogs", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(manager) as IEnumerable;
        if (changelogs == null) return result;

        foreach (var entry in changelogs)
        {
            if (entry == null) continue;
            var t = entry.GetType();
            var text = ReadString(t, entry, "Text");
            if (string.IsNullOrWhiteSpace(text)) continue;
            result.Add(new ChangelogWorkItem(
                entry,
                ReadString(t, entry, "Title"),
                ReadString(t, entry, "Author"),
                text));
        }
        return result;
    }

    private static bool TryWriteChangelogText(object entry, string value)
    {
        try
        {
            var type = entry.GetType();
            var property = type.GetProperty("Text", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var setter = property?.GetSetMethod(true);
            if (setter != null)
            {
                setter.Invoke(entry, new object?[] { value });
                return true;
            }

            var backingField = type.GetField("<Text>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
            if (backingField != null)
            {
                backingField.SetValue(entry, value);
                return true;
            }
        }
        catch { }
        return false;
    }

    private bool TryResolvePluginInstaller(out object installer)
    {
        if (this.pluginInstallerWindow != null)
        {
            installer = this.pluginInstallerWindow;
            return true;
        }

        installer = null!;
        var dalamudAssembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => string.Equals(a.GetName().Name, "Dalamud", StringComparison.Ordinal));
        if (dalamudAssembly == null)
        {
            this.reflectionStatus = "Dalamud.dllを取得できません";
            return false;
        }

        this.serviceGenericType ??= dalamudAssembly.GetTypes().FirstOrDefault(t => t.IsGenericTypeDefinition && t.Name == "Service`1" && t.Namespace == "Dalamud");
        var dalamudInterfaceType = dalamudAssembly.GetType("Dalamud.Interface.Internal.DalamudInterface");
        if (this.serviceGenericType == null || dalamudInterfaceType == null)
        {
            this.reflectionStatus = "Dalamud内部Serviceを取得できません";
            return false;
        }

        var serviceType = this.serviceGenericType.MakeGenericType(dalamudInterfaceType);
        var getMethod = serviceType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(m => m.Name == "Get" && m.GetParameters().Length == 0);
        var dalamudInterface = getMethod?.Invoke(null, null);
        if (dalamudInterface == null)
        {
            this.reflectionStatus = "DalamudInterfaceを取得できません";
            return false;
        }

        var pluginWindowField = dalamudInterfaceType.GetField("pluginWindow", BindingFlags.Instance | BindingFlags.NonPublic);
        this.pluginInstallerWindow = pluginWindowField?.GetValue(dalamudInterface);
        if (this.pluginInstallerWindow == null)
        {
            this.reflectionStatus = "PluginInstallerWindowを取得できません";
            return false;
        }

        installer = this.pluginInstallerWindow;
        return true;
    }

    private void SaveManifestSnapshot(List<ManifestSnapshotEntry> snapshot)
    {
        try
        {
            var ordered = snapshot
                .GroupBy(x => x.InternalName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(x => x.InternalName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            File.WriteAllText(this.manifestSnapshotPath, JsonSerializer.Serialize(ordered, JsonOptions), new UTF8Encoding(false));
            this.log.Information("[PJH/PluginInstaller] Manifestスナップショット保存: {Count}件 / {Path}", ordered.Count, this.manifestSnapshotPath);
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[PJH/PluginInstaller] Manifestスナップショット保存に失敗");
        }
    }

    private void LoadDictionary()
    {
        try
        {
            if (!File.Exists(this.dictionaryPath))
            {
                this.dictionaryCount = 0;
                return;
            }
            var json = File.ReadAllText(this.dictionaryPath, Encoding.UTF8);
            var items = JsonSerializer.Deserialize<List<TranslationDictionaryEntry>>(json, JsonOptions) ?? [];
            foreach (var item in items.Where(x => !string.IsNullOrWhiteSpace(x.InternalName)))
                this.dictionary[item.InternalName] = item;
            this.dictionaryCount = this.dictionary.Count;
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[PJH/PluginInstaller] 翻訳辞書の読み込みに失敗");
        }
    }

    private void SaveDictionary()
    {
        try
        {
            List<TranslationDictionaryEntry> snapshot;
            lock (this.sync)
                snapshot = this.dictionary.Values.OrderBy(x => x.InternalName, StringComparer.OrdinalIgnoreCase).ToList();
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            File.WriteAllText(this.dictionaryPath, json, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[PJH/PluginInstaller] 翻訳辞書の保存に失敗");
        }
    }

    private void LoadTerminology()
    {
        try
        {
            foreach (var pair in DefaultTerminology)
                this.terminology[pair.Key] = pair.Value;

            if (File.Exists(this.terminologyPath))
            {
                var json = File.ReadAllText(this.terminologyPath, Encoding.UTF8);
                var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions);
                if (saved != null)
                    foreach (var pair in saved)
                        this.terminology[pair.Key] = pair.Value;
            }
            File.WriteAllText(this.terminologyPath, JsonSerializer.Serialize(this.terminology, JsonOptions), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[PJH/PluginInstaller] 用語辞書の読み込みに失敗");
        }
    }

    private async Task SearchJapaneseAsync()
    {
        var input = this.searchJapanese.Trim();
        if (string.IsNullOrEmpty(input))
        {
            this.searchEnglish = string.Empty;
            this.pendingInstallerSearch.Enqueue(string.Empty);
            return;
        }

        try
        {
            if (!SearchGlossary.TryGetValue(input, out var english))
                english = await this.TranslateSelectedAsync(input, "ja", "en", "検索補助", "Search").ConfigureAwait(false);
            this.searchEnglish = english.Trim();
            this.pendingInstallerSearch.Enqueue(this.searchEnglish);
        }
        catch (Exception ex)
        {
            this.searchEnglish = "変換エラー: " + ex.Message;
        }
    }

    private void SetInstallerSearchText(string text)
    {
        try
        {
            if (!this.TryResolvePluginInstaller(out var installer))
                return;
            var installerType = installer.GetType();
            installerType.GetMethod("SetSearchText", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(installer, [text]);
            var isOpen = installerType.GetProperty("IsOpen", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            isOpen?.SetValue(installer, true);
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "[PJH/PluginInstaller] Plugin Installer検索設定に失敗");
        }
    }

    private string PostProcessJapanese(string text)
    {
        // Google sometimes returns literal/unnatural UI wording even after protected terminology.
        // Keep this intentionally narrow: Plugin Installer descriptions should read naturally,
        // without broad replacements that could damage plugin names or commands.
        foreach (var pair in NaturalJapaneseFixups)
            text = text.Replace(pair.Key, pair.Value, StringComparison.OrdinalIgnoreCase);

        return text
            .Replace("ハンティングログ", "討伐手帳", StringComparison.OrdinalIgnoreCase)
            .Replace("狩猟ログ", "討伐手帳", StringComparison.OrdinalIgnoreCase)
            .Replace("リテーナー", "リテイナー", StringComparison.Ordinal)
            .Replace("ダラムド", "Dalamud", StringComparison.OrdinalIgnoreCase)
            .Replace("ファイナルファンタジーXIV", "FFXIV", StringComparison.OrdinalIgnoreCase)
            .Replace("プラグインを使用すると", "このプラグインでは", StringComparison.Ordinal)
            .Replace("このプラグインは、あなたに", "このプラグインは", StringComparison.Ordinal)
            .Replace("プレイヤーに能力を与えます", "プレイヤーが利用できるようにします", StringComparison.Ordinal);
    }

    private static readonly KeyValuePair<string, string>[] NaturalJapaneseFixups =
    [
        new("allows you to", "～できます"),
        new("lets you", "～できます"),
        new("used to", "～するための"),
    ];

    private string ProtectTechnicalText(string text, string internalName, out Dictionary<string, string> tokens, IReadOnlyCollection<string>? protectedNames = null)
    {
        var tokenMap = new Dictionary<string, string>(StringComparer.Ordinal);
        tokens = tokenMap;
        if (string.IsNullOrEmpty(text)) return text;

        var index = 0;
        string AddToken(string value)
        {
            var token = $"PJHTOKEN{index++}X";
            tokenMap[token] = value;
            return token;
        }

        string ReplaceTechnical(Match m) => AddToken(m.Value);

        // For command help lines such as
        //   /bmrai movedelay X: Sets AI movement decision delay.
        //   /artisian -> Opens the Artisan menu.
        //   /airships → Opens the allagan tools airships window
        // protect the command prefix including its arguments, but leave the description
        // after ':', '：', '->', or '→' available for translation.
        var result = Regex.Replace(
            text,
            @"^\s*/[A-Za-z0-9_\-]+.*?(?=\s*(?::|：|->|→))",
            ReplaceTechnical,
            RegexOptions.Multiline);

        // Then protect URLs, slash command names appearing in normal prose, and ImGui IDs.
        result = Regex.Replace(result, @"https?://\S+|/[A-Za-z0-9_\-]+|###[A-Za-z0-9_\-]+|##[A-Za-z0-9_\-]+", ReplaceTechnical);

        // Protect plugin/internal names before generic terminology.
        if (!string.IsNullOrWhiteSpace(internalName))
        {
            var rx = new Regex(Regex.Escape(internalName), RegexOptions.IgnoreCase);
            result = rx.Replace(result, _ => AddToken(internalName));
        }
        if (protectedNames != null)
        {
            foreach (var protectedName in protectedNames.Where(x => !string.IsNullOrWhiteSpace(x)).OrderByDescending(x => x.Length))
            {
                var rx = new Regex(Regex.Escape(protectedName), RegexOptions.IgnoreCase);
                result = rx.Replace(result, _ => AddToken(protectedName));
            }
        }

        // IMPORTANT: protect FFXIV/Dalamud terms BEFORE Google translation.
        // The token is restored directly to our chosen Japanese term, so Google cannot turn
        // e.g. "Hunting Log" into an inconsistent literal translation.
        foreach (var pair in this.terminology.OrderByDescending(x => x.Key.Length))
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                continue;
            var rx = new Regex(@"(?<![A-Za-z0-9_])" + Regex.Escape(pair.Key) + @"(?![A-Za-z0-9_])", RegexOptions.IgnoreCase);
            result = rx.Replace(result, _ => AddToken(pair.Value));
        }

        return result;
    }

    private static string RestoreTechnicalText(string text, Dictionary<string, string> tokens)
    {
        foreach (var pair in tokens)
            text = text.Replace(pair.Key, pair.Value, StringComparison.Ordinal);
        return text;
    }

    private static List<string> SplitTranslationText(string text, int maxChars)
    {
        var result = new List<string>();
        var pos = 0;
        while (pos < text.Length)
        {
            var take = Math.Min(maxChars, text.Length - pos);
            if (pos + take < text.Length)
            {
                var searchStart = Math.Max(pos, pos + take - 250);
                var cut = -1;
                for (var i = pos + take; i > searchStart; i--)
                {
                    var c = text[i - 1];
                    if (c == '\n' || c == '.' || c == '!' || c == '?' || char.IsWhiteSpace(c))
                    {
                        cut = i;
                        break;
                    }
                }
                if (cut > pos)
                    take = cut - pos;
            }
            result.Add(text.Substring(pos, take));
            pos += take;
        }
        return result;
    }

    private static object? ReadObject(Type t, object obj, string name) =>
        t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(obj);
    private static string ReadString(Type t, object obj, string name) => ReadObject(t, obj, name)?.ToString() ?? string.Empty;
    private static void WriteString(object obj, string name, string value)
    {
        var p = obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (p?.CanWrite == true) p.SetValue(obj, value);
    }

    private void SaveConfig() => this.config.Save(this.settingsPath, this.log);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private sealed unsafe class MainWindow : Window
    {
        private readonly PluginInstallerModule owner;
        public MainWindow(PluginInstallerModule owner) : base("Plugin Installer 日本語化###PluginInstallerIntegrated")
        {
            this.owner = owner;
            Size = new System.Numerics.Vector2(900, 680);
            SizeCondition = ImGuiCond.FirstUseEver;
            AllowBackgroundBlur = true;
            AllowPinning = true;
            AllowClickthrough = true;
        }

        public override void Draw()
        {
            if (ImGui.BeginTabBar("##PIJTTabs"))
            {
                if (ImGui.BeginTabItem("メイン"))
                {
                    this.DrawMainTab();
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("ログ"))
                {
                    this.DrawLogTab();
                    ImGui.EndTabItem();
                }
                ImGui.EndTabBar();
            }
        }

        private void DrawMainTab()
        {
            ImGui.TextUnformatted("日本語検索補助");
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputTextWithHint("##JapaneseSearch", "例：手配書 / 討伐手帳 / 製作 / リテイナー", ref this.owner.searchJapanese, 200, ImGuiInputTextFlags.EnterReturnsTrue))
                _ = this.owner.SearchJapaneseAsync();
            if (ColoredButton("日本語から検索", ButtonKind.Primary)) _ = this.owner.SearchJapaneseAsync();
            ImGui.SameLine();
            if (ColoredButton("検索をクリア", ButtonKind.Danger))
            {
                this.owner.searchJapanese = string.Empty;
                this.owner.searchEnglish = string.Empty;
                this.owner.SetInstallerSearchText(string.Empty);
            }
            if (!string.IsNullOrWhiteSpace(this.owner.searchEnglish))
                ImGui.TextWrapped("Dalamudへ渡した英語: " + this.owner.searchEnglish);

            ImGui.Separator();
            var enabled = this.owner.config.TranslateInstallerDescriptions;
            if (ImGui.Checkbox("Plugin Installerの説明を辞書で日本語化", ref enabled))
            {
                this.owner.config.TranslateInstallerDescriptions = enabled;
                if (!enabled) this.owner.RestoreAll(); else this.owner.forceDiffCheck = true;
                this.owner.SaveConfig();
            }
            ImGui.SameLine();
            if (this.owner.config.TranslateInstallerDescriptions)
                ImGui.TextColored(new System.Numerics.Vector4(0.45f, 1f, 0.55f, 1f), "[ON] 日本語化中");
            else
                ImGui.TextColored(new System.Numerics.Vector4(1f, 0.45f, 0.45f, 1f), "[OFF] 日本語化停止");

            var showOriginal = this.owner.config.ShowOriginalBelowTranslation;
            if (ImGui.Checkbox("翻訳の下に原文も表示", ref showOriginal))
            {
                this.owner.config.ShowOriginalBelowTranslation = showOriginal;
                this.owner.RestoreAll();
                this.owner.forceDiffCheck = true;
                this.owner.SaveConfig();
            }

            var minutes = this.owner.config.DiffCheckMinutes;
            ImGui.SetNextItemWidth(110);
            if (ImGui.InputInt("差分チェック間隔（分）", ref minutes))
            {
                this.owner.config.DiffCheckMinutes = Math.Clamp(minutes, 1, 1440);
                this.owner.SaveConfig();
            }
            ImGui.SameLine();
            if (ColoredButton("今すぐ差分チェック", ButtonKind.Warning)) this.owner.forceDiffCheck = true;

            ImGui.Separator();
            ImGui.TextUnformatted("翻訳方式");
            if (ColoredButton("Google翻訳", this.owner.UsePrivateServer ? ButtonKind.Neutral : ButtonKind.Success))
                this.owner.SetTranslationProvider(false);
            ImGui.SameLine();
            if (ColoredButton("PJH中継サーバー", this.owner.UsePrivateServer ? ButtonKind.Success : ButtonKind.Neutral))
                this.owner.SetTranslationProvider(true);
            if (this.owner.UsePrivateServer)
            {
                if (this.owner.EffectiveTranslationProviderName == "Google翻訳")
                    ImGui.TextColored(new System.Numerics.Vector4(0.45f, 1f, 0.55f, 1f), "現在の翻訳先: Google翻訳（PJH中継サーバー選択中）");
                else
                    ImGui.TextUnformatted("現在の翻訳先: PJH中継サーバー");
            }

            ImGui.TextUnformatted(this.owner.TranslationProviderName + "状態");
            DrawStatusText(this.owner.googleStatus, this.owner.googleStatus.Contains("429") || this.owner.googleStatus.Contains("エラー"));
            if (!string.IsNullOrWhiteSpace(this.owner.googleStatusDetail)) ImGui.TextWrapped(this.owner.googleStatusDetail);
            if (ColoredButton("利用可能チェック", ButtonKind.Primary))
                _ = this.owner.CheckSelectedTranslationAvailabilityAsync("Plugin Installer");
            ImGui.SameLine();
            if (ColoredButton("翻訳待ちを開始", ButtonKind.Success))
                this.owner.StartPendingTranslations();
            ImGui.SameLine();
            if (ColoredButton("コマンド説明を再取得", ButtonKind.Primary))
                this.owner.ReacquireCommandDescriptions();
            ImGui.TextDisabled("差分チェックや再取得だけでは翻訳サービスへ送信しません。『翻訳待ちを開始』を押した時だけ送信します。");
            ImGui.TextUnformatted("コマンド説明: " + this.owner.commandReacquireStatus);

            if (ColoredButton("更新履歴を翻訳", ButtonKind.Primary))
                this.owner.StartChangelogTranslation();
            if (this.owner.changelogTranslationTask is { IsCompleted: false })
            {
                ImGui.SameLine();
                if (ColoredButton("更新履歴の翻訳を停止", ButtonKind.Danger))
                    this.owner.StopChangelogTranslation();
            }
            ImGui.SameLine();
            ImGui.TextUnformatted("更新履歴: " + this.owner.changelogTranslationStatus);

            if (this.owner.rateLimitedUntil is { } until && until > DateTimeOffset.Now)
                ImGui.TextWrapped($"[制限中] 次回再試行まで約 {Math.Ceiling((until - DateTimeOffset.Now).TotalSeconds)} 秒");
            if (this.owner.googleFailureCount > 0) ImGui.TextUnformatted($"今回の翻訳失敗: {this.owner.googleFailureCount} 件");

            ImGui.Separator();
            ImGui.TextUnformatted($"Manifest: Remote {this.owner.availableManifestCount} / 対象 {this.owner.manifestCount} / 導入済 {this.owner.installedManifestCount}");
            ImGui.TextUnformatted($"翻訳辞書: {this.owner.dictionaryCount} / 新規: {this.owner.missingCount} / 変更: {this.owner.changedCount} / 要確認: {this.owner.manualReviewCount} / 翻訳待ち: {this.owner.queuedCount}");
            ImGui.TextWrapped("翻訳状態: " + this.owner.translationStatus);

            ImGui.SetNextItemOpen(this.owner.config.DictionarySectionOpen, ImGuiCond.Appearing);
            var dictOpen = ImGui.CollapsingHeader("辞書ファイル設定");
            if (dictOpen != this.owner.config.DictionarySectionOpen)
            {
                this.owner.config.DictionarySectionOpen = dictOpen;
                this.owner.SaveConfig();
            }
            if (dictOpen)
            {
                if (ColoredButton("翻訳ファイルを選択", ButtonKind.Primary)) this.owner.SelectDictionaryFile();
                ImGui.SameLine();
                ImGui.TextDisabled("JSONファイルを選択すると、そのまま適用して次回も使用します");
                ImGui.SetNextItemWidth(-1);
                ImGui.InputText("##TranslationDictionaryPath", ref this.owner.dictionaryPathInput, 1024);
                if (ColoredButton("この辞書を使用", ButtonKind.Success)) this.owner.ApplyManualDictionaryPath();
                ImGui.SameLine();
                if (ColoredButton("再読み込み", ButtonKind.Primary)) this.owner.ReloadDictionaryFromDisk();
                ImGui.SameLine();
                if (ColoredButton("辞書ファイルを表示", ButtonKind.Neutral)) this.owner.RevealDictionaryFile();
                ImGui.SameLine();
                if (ColoredButton("フォルダーを開く", ButtonKind.Neutral)) this.owner.OpenDictionaryDirectory();
                ImGui.TextWrapped("使用中: " + this.owner.dictionaryPath);
                ImGui.TextUnformatted($"読込件数: {this.owner.dictionaryCount} プラグイン");
            }

            ImGui.SetNextItemOpen(this.owner.config.DetailsSectionOpen, ImGuiCond.Appearing);
            var detailsOpen = ImGui.CollapsingHeader("詳細情報");
            if (detailsOpen != this.owner.config.DetailsSectionOpen)
            {
                this.owner.config.DetailsSectionOpen = detailsOpen;
                this.owner.SaveConfig();
            }
            if (detailsOpen)
            {
                ImGui.TextWrapped("Dalamud接続: " + this.owner.reflectionStatus);
                ImGui.TextWrapped("Manifest一覧: " + this.owner.manifestSnapshotPath);
                ImGui.TextWrapped("用語辞書: " + this.owner.terminologyPath);
                ImGui.TextWrapped("Remote Manifestのみを対象にします。Local Manifestは翻訳・差分判定・Google送信の対象外です。既存辞書を優先し、FFXIV/Dalamud用語は送信前に保護します。");
            }
        }

        private void DrawLogTab()
        {
            ImGui.TextUnformatted("更新があったプラグイン");
            if (ColoredButton("一覧をクリア", ButtonKind.Danger)) this.owner.ClearUpdateRows();
            ImGui.SameLine();
            if (ColoredButton("列幅をリセット", ButtonKind.Neutral)) this.owner.updateTableGeneration++;

            var flags = ImGuiTableFlags.ScrollX | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoKeepColumnsVisible |
                        ImGuiTableFlags.Resizable | ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg;
            if (ImGui.BeginTable($"##UpdatedPlugins_{this.owner.updateTableGeneration}", 6, flags, new System.Numerics.Vector2(0, 260)))
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("プラグイン", ImGuiTableColumnFlags.WidthFixed, 220);
                ImGui.TableSetupColumn("種別", ImGuiTableColumnFlags.WidthFixed, 90);
                ImGui.TableSetupColumn("変更箇所", ImGuiTableColumnFlags.WidthFixed, 190);
                ImGui.TableSetupColumn("翻訳", ImGuiTableColumnFlags.WidthFixed, 110);
                ImGui.TableSetupColumn("辞書", ImGuiTableColumnFlags.WidthFixed, 110);
                ImGui.TableSetupColumn("更新時刻", ImGuiTableColumnFlags.WidthFixed, 100);
                DrawSortableHeaders(this.owner);

                foreach (var row in this.owner.GetUpdateRowsSnapshot())
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(row.Name);
                    ImGui.TableSetColumnIndex(1); DrawLabel(row.ChangeType);
                    ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(row.Fields);
                    ImGui.TableSetColumnIndex(3); DrawLabel(row.Translation);
                    ImGui.TableSetColumnIndex(4); DrawLabel(row.SaveState);
                    ImGui.TableSetColumnIndex(5); ImGui.TextUnformatted(row.LastUpdated.ToString("HH:mm:ss"));
                }
                ImGui.EndTable();
            }

            ImGui.Separator();
            ImGui.TextUnformatted("詳細ログ");
            if (ColoredButton("ログをクリア", ButtonKind.Danger)) this.owner.ClearUiLogs();
            ImGui.SameLine();
            if (ColoredButton("ログをコピー", ButtonKind.Primary)) ImGui.SetClipboardText(this.owner.BuildUiLogText());

            if (ImGui.BeginChild("##PIJTLog", new System.Numerics.Vector2(0, 0), true, ImGuiWindowFlags.HorizontalScrollbar))
            {
                foreach (var item in this.owner.GetUiLogsSnapshot())
                {
                    var prefix = $"{item.Time:HH:mm:ss} [{item.Level}]";
                    if (item.Level.Contains("エラー") || item.Level == "429")
                        ImGui.TextColored(new System.Numerics.Vector4(1f, 0.45f, 0.45f, 1f), prefix);
                    else if (item.Level is "完了" or "Google成功")
                        ImGui.TextColored(new System.Numerics.Vector4(0.45f, 1f, 0.55f, 1f), prefix);
                    else
                        ImGui.TextUnformatted(prefix);
                    ImGui.SameLine();
                    ImGui.TextWrapped((string.IsNullOrWhiteSpace(item.Plugin) ? "" : item.Plugin + " - ") + item.Message);
                }
                ImGui.EndChild();
            }
        }


        private enum ButtonKind
        {
            Primary,
            Success,
            Warning,
            Danger,
            Neutral,
        }

        private static bool ColoredButton(string label, ButtonKind kind)
        {
            var baseColor = kind switch
            {
                ButtonKind.Success => new System.Numerics.Vector4(0.16f, 0.52f, 0.28f, 1f),
                ButtonKind.Warning => new System.Numerics.Vector4(0.70f, 0.48f, 0.10f, 1f),
                ButtonKind.Danger => new System.Numerics.Vector4(0.65f, 0.20f, 0.20f, 1f),
                ButtonKind.Neutral => new System.Numerics.Vector4(0.34f, 0.37f, 0.42f, 1f),
                _ => new System.Numerics.Vector4(0.18f, 0.42f, 0.68f, 1f),
            };

            static System.Numerics.Vector4 Shift(System.Numerics.Vector4 c, float amount)
                => new(
                    Math.Clamp(c.X + amount, 0f, 1f),
                    Math.Clamp(c.Y + amount, 0f, 1f),
                    Math.Clamp(c.Z + amount, 0f, 1f),
                    c.W);

            ImGui.PushStyleColor(ImGuiCol.Button, baseColor);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Shift(baseColor, 0.10f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, Shift(baseColor, -0.08f));
            var pressed = ImGui.Button(label);
            ImGui.PopStyleColor(3);
            return pressed;
        }

        private static void DrawSortableHeaders(PluginInstallerModule owner)
        {
            var labels = new[] { "プラグイン", "種別", "変更箇所", "翻訳", "辞書", "更新時刻" };
            ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
            for (var column = 0; column < labels.Length; column++)
            {
                ImGui.TableSetColumnIndex(column);
                ImGui.AlignTextToFramePadding();
                var arrow = owner.updateSortColumn == column ? (owner.updateSortAscending ? " ▲" : " ▼") : string.Empty;
                ImGui.TextUnformatted(labels[column] + arrow);
                if (ImGui.IsItemClicked(ImGuiMouseButton.Left)) owner.ToggleUpdateSort(column);
                DrawColumnSizingMenu(column, $"##PIJTColMenu_{column}");
            }
        }

        private static void DrawColumnSizingMenu(int column, string popupId)
        {
            var table = ImGuiP.GetCurrentTable();
            if (!ImGui.BeginPopupContextItem(popupId)) return;
            try
            {
                if (table.Handle != null && ImGui.MenuItem("この列を内容に合わせる"))
                    ImGuiP.TableSetColumnWidthAutoSingle(table, column);
                if (table.Handle != null && ImGui.MenuItem("すべての列を内容に合わせる"))
                    ImGuiP.TableSetColumnWidthAutoAll(table);
            }
            finally
            {
                ImGui.EndPopup();
            }
        }

        private static void DrawStatusText(string text, bool error)
        {
            if (error) ImGui.TextColored(new System.Numerics.Vector4(1f, 0.45f, 0.45f, 1f), "[エラー/制限] " + text);
            else if (text == "利用可能") ImGui.TextColored(new System.Numerics.Vector4(0.45f, 1f, 0.55f, 1f), "[正常] " + text);
            else ImGui.TextUnformatted("[状態] " + text);
        }

        private static void DrawLabel(string text)
        {
            if (text.Contains("エラー") || text.Contains("制限"))
                ImGui.TextColored(new System.Numerics.Vector4(1f, 0.45f, 0.45f, 1f), text);
            else if (text.Contains("保存済") || text.Contains("翻訳済"))
                ImGui.TextColored(new System.Numerics.Vector4(0.45f, 1f, 0.55f, 1f), text);
            else if (text.Contains("保護"))
                ImGui.TextColored(new System.Numerics.Vector4(1f, 0.85f, 0.35f, 1f), text);
            else
                ImGui.TextUnformatted(text);
        }
    }

    private sealed record UiLogEntry(DateTimeOffset Time, string Level, string Plugin, string Message);

    private sealed class UpdatePluginRow
    {
        public string InternalName { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string ChangeType { get; set; } = string.Empty;
        public string Fields { get; set; } = string.Empty;
        public string Translation { get; set; } = string.Empty;
        public string SaveState { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public DateTimeOffset FirstDetected { get; set; }
        public DateTimeOffset LastUpdated { get; set; }
        public UpdatePluginRow Clone() => (UpdatePluginRow)this.MemberwiseClone();
    }

    private sealed record CommandTranslationWork(object CommandInfo, string InternalName, string Command, string Source, string QueueKey);
    private sealed record CommandTranslationResult(object CommandInfo, string QueueKey, CommandTranslationEntry? Entry, string? Error);
    private sealed class CommandTranslationEntry
    {
        public string InternalName { get; set; } = string.Empty;
        public string Command { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string Japanese { get; set; } = string.Empty;
        public DateTimeOffset UpdatedAt { get; set; }
    }

    private sealed record OriginalManifest(object Manifest, string Punchline, string Description);

    private sealed record ChangelogWorkItem(object Entry, string Title, string Author, string Text);
    private sealed record ChangelogTranslationResult(object Entry, string OriginalText, string? TranslatedText, string? Error);

    private sealed record TranslationWork(
        object Manifest,
        string InternalName,
        string Name,
        string Version,
        string Punchline,
        string Description,
        bool TranslatePunchline,
        bool TranslateDescription,
        TranslationDictionaryEntry? Existing,
        string QueueKey = "");

    private sealed record TranslationResult(object Manifest, string QueueKey, TranslationDictionaryEntry? Entry, string? Error);

    private sealed class TranslationDictionaryEntry
    {
        public string InternalName { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string AssemblyVersion { get; set; } = string.Empty;
        public string PunchlineSource { get; set; } = string.Empty;
        public string PunchlineJapanese { get; set; } = string.Empty;
        public string DescriptionSource { get; set; } = string.Empty;
        public string DescriptionJapanese { get; set; } = string.Empty;
        public bool AutoTranslated { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    private sealed class ManifestSnapshotEntry
    {
        public string InternalName { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string AssemblyVersion { get; set; } = string.Empty;
        public string Punchline { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }
}
