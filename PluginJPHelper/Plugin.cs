using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Net.Http;
using System.Security.Cryptography;
using System.Reflection;
using PluginJPHelper.Data;
using PluginJPHelper.Plugins.Behaviors;
using PluginJPHelper.Plugins.Profiles;
using Dalamud.Bindings.ImGui;
using Dalamud.Configuration;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace PluginJPHelper;

public sealed unsafe class Plugin : IDalamudPlugin
{
    // Protect structural changes/enumeration of config.Plugins from overlapping async/UI work.
    private readonly object pluginConfigSync = new();

    // 辞書と設定への書き込みは描画スレッドへ集約する。
    //
    // 翻訳フックは描画スレッドから、画面に出る文字列すべてに対して毎フレーム
    // config.Plugins / UserOverrides / OfficialOverrides / DeletedKeys / Locations と
    // dictionaryCatalogCache を読む。これらは Dictionary<,> でスレッドセーフではない。
    // 一方でダウンロード処理は Task.Run の中から同じ辞書を Clear() して数千件を
    // 挿入し直していたため、リサイズと読み取りが重なると
    // IndexOutOfRangeException / NullReferenceException / 無限ループになりうる。
    // lock を足す方式は読み側が 30 箇所以上あって取りこぼしやすいので、
    // 「書き込みスレッドを 1 本に固定する」方式にする。
    // 背景側はダウンロードとパースだけを行い、適用はこのキュー経由で描画スレッドが行う。
    private readonly ConcurrentQueue<Action> mainThreadWork = new();

    private void RunOnMainThread(Action work) => mainThreadWork.Enqueue(work);

    private void DrainMainThreadWork()
    {
        while (mainThreadWork.TryDequeue(out var work))
        {
            try { work(); }
            catch (Exception ex) { log.Error(ex, "[PluginJPHelper] 描画スレッドへ委譲した処理で例外"); }
        }
    }

    // v0.4.20: 文字化け警告は3択のまま、判定ルール全表示とホワイトリスト優先判定を追加。
    // Hook内では重いUI/保存処理を行わず、対象翻訳だけ即時OFFにして通知をキューへ積む。
    private readonly ConcurrentQueue<MojibakeIncident> pendingMojibakeIncidents = new();
    private readonly HashSet<string> queuedMojibakeIncidentKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> allowedMojibakeIncidentKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> allowedMojibakeTargets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> mojibakePluginLoadStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly object mojibakeSafetySync = new();
    private MojibakeIncident? activeMojibakeIncident;
    private string mojibakeCorrectionBuffer = string.Empty;
    private string requestedMainTab = string.Empty;
    private bool requestOpenInstallerRepairHistory;
    private readonly HashSet<string> selectedInstallerRepairHistory = new(StringComparer.Ordinal);
    private readonly MojibakeAlertWindow mojibakeAlertWindow;
    private readonly Dictionary<string, long> mojibakeTargetedScanTicks = new(StringComparer.Ordinal);
    private const long MojibakeTargetedScanIntervalMs = 5000;
    private string mojibakeRuleAddInput = string.Empty;
    private string mojibakeRuleAddLabel = string.Empty;
    private int mojibakeRuleAddMinimum = 1;
    private string mojibakeWhitelistAddInput = string.Empty;
    private string mojibakeWhitelistAddLabel = string.Empty;
    private bool mojibakeWhitelistAddPartial;

    private const string Command = "/pjph";
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly IGameInteropProvider interop;
    private readonly IPluginLog log;
    private readonly IContextMenu contextMenu;
    private readonly Configuration config;

    private readonly ConcurrentDictionary<string, CapturedItem> backgroundCaptured = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ConcurrentDictionary<string, CapturedItem>> pluginCaptured = new(StringComparer.Ordinal);
    private volatile bool baselineCaptureEnabled;
    private readonly Dictionary<string, string> editBuffers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> openCommandBuffers = new(StringComparer.Ordinal);
    private readonly HttpClient officialDictionaryHttp = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly object officialDictionaryLock = new();
    private List<OfficialDictionaryEntry> officialDictionaryEntries = new();
    private readonly HashSet<string> selectedOfficialDictionaries = new(StringComparer.Ordinal);
    private volatile bool officialDictionaryBusy;
    private string officialDictionaryStatus = "未取得";
    private volatile bool officialNoticeBusy;
    private string officialNoticeText = string.Empty;
    private string officialNoticeSha = string.Empty;
    private long officialNoticeLastAttemptTick;
    private long communityLastAttemptTick;
    private const long UpdateCheckIntervalMs = 3_600_000; // 起動時 + 1時間ごとに確認
    private const string OfficialDictionaryApiUrl = "https://api.github.com/repos/elpapityo/PluginJPHelper/contents/Dictionaries/Official?ref=main";
    private const string OfficialNoticeUrl = "https://raw.githubusercontent.com/elpapityo/PluginJPHelper/main/Dictionaries/notice.txt";
    private const string OfficialNoticeApiUrl = "https://api.github.com/repos/elpapityo/PluginJPHelper/contents/Dictionaries/notice.txt?ref=main";

    // 辞書CSVの予約メタ行。
    // Plugin,Menu,Section,Type,English,Japanese
    // Pawprint,,,@Window,ArenaNodeEditor,
    private const string DictionaryWindowMetaType = "@Window";
    // v0.0.66: 辞書画面の全カタログ再構築を毎フレーム行わない。
    private readonly Dictionary<string, KeyValuePair<string, string>[]> dictionaryCatalogCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, int>> dictionaryMenuCountCache = new(StringComparer.Ordinal);
    private readonly WindowSystem windowSystem;
    private readonly MainWindow mainWindow;
    private readonly PluginInstallerModule pluginInstallerModule;
    private volatile bool captureEnabled;
    private long translatedCount;
    private string filter = string.Empty;
    private string captureTargetFilter = string.Empty;
    private string captureOtherFilter = string.Empty;
    private bool showUntranslatedOnly;
    private int dictionarySortMode; // 0=原文, 1=画面内項目, 2=日本語訳, 3=未訳優先
    private bool dictionarySortAscending = true;
    // v0.3.3: 公式／コミュニティ／未翻訳一覧のソート。
    private int officialDictionarySortMode;
    private bool officialDictionarySortAscending = true;
    private int communityDictionarySortMode;
    private bool communityDictionarySortAscending = true;
    private int captureSortMode;
    private bool captureSortAscending = true;
    private string customPluginName = string.Empty;
    private string customWindowKeyword = string.Empty;
    // v0.4.9: 独立ImGuiウィンドウの手動関連付け + 検出テストモード。
    // 特定プラグインのウィンドウ名を決め打ちせず、ユーザーが「検出開始」後に開いた
    // 新規ウィンドウを候補として表示し、選択した翻訳対象へ関連付ける。
    private string additionalWindowKeywordInput = string.Empty;
    private string detectedWindowSelection = string.Empty;
    private bool additionalWindowDetectMode;
    private string additionalWindowDetectPlugin = string.Empty;
    private long additionalWindowDetectStartedAt;
    private HashSet<string> additionalWindowDetectBaseline = new(StringComparer.Ordinal);
    private string additionalWindowNewSelection = string.Empty;
    private string additionalWindowSearch = string.Empty;
    private bool showInternalDetectedWindows;
    private string installedPluginSelection = string.Empty;
    private string csvStatus = string.Empty;
    private string dictionaryAutoTranslateStatus = string.Empty;
    private Task<(Dictionary<string, string> Translations, int Skipped, int Failed)>? dictionaryAutoTranslateTask;
    private CancellationTokenSource? dictionaryAutoTranslateCts;
    private Task<string>? dictionaryGoogleAvailabilityTask;
    private string dictionaryGoogleAvailabilityStatus = "未確認";
    private readonly Dictionary<string, string> dictionaryAutoTranslateDrafts = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> dictionaryAutoTranslateLogs = new();
    private const int MaxDictionaryAutoTranslateLogs = 500;
    private string hookStatus = "未初期化";
    // フック結果を UI から確認できるようにするための内訳。
    // hookStatus はこれまで代入されるだけで一度も表示されず、cimgui のエクスポート名が
    // ImGui のバージョンで変わってフックが外れていても、Dalamudログを開かない限り気づけなかった。
    private string[] hookInstalledNames = [];
    private string[] hookFailedNames = [];
    // 診断ログの記録は既定で停止しておく。BeginCombo / SliderInt / TableSetupColumn の
    // detour は毎フレーム走るため、誰も見ないデータのために
    // Marshal.PtrToStringUTF8 とレコード確保を常時通したくない。
    private bool hookDiagnosticsRecording;
    private string selectedPlugin = "RSR";
    private string capturePlugin = "RSR";
    private string communityPosterNameBuffer = string.Empty;
    private string communityFilter = string.Empty;
    // v0.3.3: 公式／コミュニティ辞書の状態列を必要なときだけ表示。
    // Default is the compact view (dictionary / supported plugin / state).
    private bool showStatusTranslationRegistered;
    private bool showStatusCaptureRegistered;
    private bool showStatusTranslationEnabled;
    private bool showStatusInstalled;
    private bool showStatusPluginSwitch;
    private string communityStatus = "未取得";
    private bool communityListLoaded;
    private bool communityNoticePending;
    private volatile bool communityDictionaryBusy;
    private readonly object communityDictionaryLock = new();
    private List<CommunityDictionaryEntry> communityDictionaryEntries = new();
    private readonly HashSet<string> selectedCommunityDictionaries = new(StringComparer.Ordinal);
    private const string CommunityIndexApiUrl = "https://api.github.com/repos/elpapityo/PluginJPHelper/contents/Dictionaries/Community/community-index.json?ref=main";
    [ThreadStatic] private static bool drawingOwnUi;

    // v0.3.4 local test: translate custom game ContextMenu items before Dalamud renders them.
    // Plugin identity + prefix + original text must all match exactly.
    private static readonly ContextMenuTranslationEntry[] ContextMenuTranslations =
    [
        new("MarketBoardPlugin", SeIconChar.BoxedLetterM, "Search in Market Board", "マーケットボードで検索"),
        // GatherBuddyReborn upstream currently uses C, but some installed builds present the same exact item with V.
        // Only this exact original text accepts the V fallback; all other matching remains plugin + prefix + exact text.
        new("GatherBuddyReborn", SeIconChar.BoxedLetterC, "Add to Crafting List", "製作リストに追加"),
        new("GatherBuddyReborn", SeIconChar.BoxedLetterV, "Add to Crafting List", "製作リストに追加"),
        new("GatherBuddyReborn", SeIconChar.BoxedLetterV, "Open in Vulcan", "Vulcanで開く"),
        new(ArtisanBehavior.PluginName, SeIconChar.BoxedLetterA, "Artisan Crafting List", "Artisan 製作リスト"),
    ];

    private Hook<TextUnformattedDelegate>? textHook;
    private Hook<TextWrappedDelegate>? textWrappedHook;
    private Hook<TextVDelegate>? textVHook;
    private Hook<TextColoredVDelegate>? textColoredVHook;
    private Hook<TextDisabledVDelegate>? textDisabledVHook;
    private Hook<CheckboxDelegate>? checkboxHook;
    private Hook<ButtonDelegate>? buttonHook;
    private Hook<ButtonExDelegate>? buttonExHook;
    private Hook<SelectableDelegate>? selectableHook;
    private Hook<SelectablePtrDelegate>? selectablePtrHook;
    private Hook<ComboStrArrDelegate>? comboStrArrHook;
    private Hook<ComboStrDelegate>? comboStrHook;
    private Hook<ComboFnPtrDelegate>? comboFnPtrHook;
    private Hook<BeginComboDelegate>? beginComboHook;
    private Hook<EndComboDelegate>? endComboHook;
    private Hook<SliderIntDelegate>? sliderIntHook;
    private Hook<TableSetupColumnDelegate>? tableSetupColumnHook;
    private Hook<SeparatorTextDelegate>? separatorTextHook;
    private Hook<BeginDelegate>? beginHook;
    private Hook<EndDelegate>? endHook;
    private Hook<BeginPopupDelegate>? beginPopupHook;
    private Hook<BeginPopupModalDelegate>? beginPopupModalHook;
    private Hook<EndPopupDelegate>? endPopupHook;
    private Hook<RadioButtonBoolDelegate>? radioButtonBoolHook;
    private Hook<RadioButtonIntPtrDelegate>? radioButtonIntPtrHook;
    private Hook<TreeNodeStrDelegate>? treeNodeStrHook;
    private Hook<TreeNodeExStrDelegate>? treeNodeExStrHook;
    private Hook<CollapsingHeaderTreeNodeFlagsDelegate>? collapsingHeaderTreeNodeFlagsHook;
    private Hook<CollapsingHeaderBoolPtrDelegate>? collapsingHeaderBoolPtrHook;
    private Hook<BulletTextDelegate>? bulletTextHook;
    private Hook<RenderTextDelegate>? renderTextHook;
    private Hook<RenderTextWrappedDelegate>? renderTextWrappedHook;
    private Hook<RenderTextClippedDelegate>? renderTextClippedHook;
    private Hook<DrawListAddTextVec2Delegate>? drawListAddTextVec2Hook;
    private Hook<DrawListAddTextFontPtrDelegate>? drawListAddTextFontPtrHook;
    private Hook<BeginTabItemDelegate>? beginTabItemHook;
    private Hook<MenuItemBoolDelegate>? menuItemBoolHook;
    private Hook<MenuItemBoolPtrDelegate>? menuItemBoolPtrHook;
    private Hook<BeginMenuDelegate>? beginMenuHook;
    [ThreadStatic] private static Stack<string>? windowStack;
    [ThreadStatic] private static Stack<bool>? popupOwnerFrameStack;
    [ThreadStatic] private static int comboOpenDepth;
    private long comboHookCalls;
    private long comboTranslatedItems;
    private long comboStrCalls;
    private long comboFnCalls;
    private long beginComboCalls;
    private long sliderIntCalls;
    private long tableSetupColumnCalls;
    private long comboSelectableCalls;
    private readonly ConcurrentQueue<HookDiagnosticEntry> hookDiagnosticEntries = new();
    private const int MaxHookDiagnosticEntries = 80;
    private readonly ConcurrentDictionary<string, int> seenWindows = new(StringComparer.Ordinal);
    // v0.4.9: Begin() が直近のフレームで呼ばれているウィンドウだけを
    // 「現在表示中」とみなすための最終検出時刻。seenWindows は履歴として残す。
    private readonly ConcurrentDictionary<string, long> liveWindowLastSeen = new(StringComparer.Ordinal);
    private static Stack<string>? windowOwnerStack;
    private string lastExplicitWindowOwner = string.Empty;
    private long lastExplicitWindowOwnerTick;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void TextUnformattedDelegate(byte* text, byte* textEnd);
    // Dalamud Bindings (2025+) の ImGui.TextWrapped(string) は安全化された cimgui 経路を使用する。
    // RSR のチェック項目ラベルは TextUnformatted ではなくこの経路に来る。
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void TextWrappedDelegate(byte* text);
    // ImGui.Text / TextColored / TextDisabled のV系経路。
    // va_list はそのままOriginalへ渡し、書式指定子を含まない固定ラベルだけ翻訳する。
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void TextVDelegate(byte* fmt, nint args);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void TextColoredVDelegate(Vector4 col, byte* fmt, nint args);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void TextDisabledVDelegate(byte* fmt, nint args);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte CheckboxDelegate(byte* label, byte* value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte ButtonDelegate(byte* label, Vector2 size);
    // ImSharp Im.Button() calls cimgui igButtonEx directly instead of igButton.
    // Penumbra/Luna use this path for support buttons and tutorial controls.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte ButtonExDelegate(byte* label, Vector2 size, int flags);
    // cimgui: bool igSelectable_Bool(const char* label, bool selected, ImGuiSelectableFlags flags, const ImVec2 size)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte SelectableDelegate(byte* label, byte selected, int flags, Vector2 size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte SelectablePtrDelegate(byte* label, byte* selected, int flags, Vector2 size);
    // cimgui: bool igCombo_Str_arr(const char* label, int* current_item, const char* const items[], int items_count, int popup_max_height_in_items)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte ComboStrArrDelegate(byte* label, int* currentItem, byte** items, int itemsCount, int popupMaxHeightInItems);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte ComboStrDelegate(byte* label, int* currentItem, byte* itemsSeparatedByZeros, int popupMaxHeightInItems);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte ComboFnPtrDelegate(byte* label, int* currentItem, nint getter, void* userData, int itemsCount, int popupMaxHeightInItems);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte BeginComboDelegate(byte* label, byte* previewValue, int flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void EndComboDelegate();
    // cimgui: bool igSliderInt(const char* label, int* v, int v_min, int v_max, const char* format, ImGuiSliderFlags flags)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte SliderIntDelegate(byte* label, int* value, int min, int max, byte* format, int flags);
    // cimgui: void igTableSetupColumn(const char* label, ImGuiTableColumnFlags flags, float init_width_or_weight, ImGuiID user_id)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void TableSetupColumnDelegate(byte* label, int flags, float initWidthOrWeight, uint userId);
    // cimgui: bool igBeginTabItem(const char* label, bool* p_open, ImGuiTabItemFlags flags)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte BeginTabItemDelegate(byte* label, byte* pOpen, int flags);
    // cimgui: bool igMenuItem_Bool(const char* label, const char* shortcut, bool selected, bool enabled)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte MenuItemBoolDelegate(byte* label, byte* shortcut, byte selected, byte enabled);
    // cimgui: bool igMenuItem_BoolPtr(const char* label, const char* shortcut, bool* p_selected, bool enabled)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte MenuItemBoolPtrDelegate(byte* label, byte* shortcut, byte* pSelected, byte enabled);
    // cimgui: bool igBeginMenu(const char* label, bool enabled)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte BeginMenuDelegate(byte* label, byte enabled);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SeparatorTextDelegate(byte* label);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte BeginDelegate(byte* name, byte* pOpen, int flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void EndDelegate();
    // cimgui popup tracking. Popup content is rendered after BeginPopup/BeginPopupModal returns,
    // so keep the parent plugin owner active until igEndPopup.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte BeginPopupDelegate(byte* strId, int flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte BeginPopupModalDelegate(byte* name, byte* pOpen, int flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void EndPopupDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte RadioButtonBoolDelegate(byte* label, byte active);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte RadioButtonIntPtrDelegate(byte* label, int* v, int vButton);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte TreeNodeStrDelegate(byte* label);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte TreeNodeExStrDelegate(byte* label, int flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte CollapsingHeaderTreeNodeFlagsDelegate(byte* label, int flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte CollapsingHeaderBoolPtrDelegate(byte* label, byte* pVisible, int flags);
    // Dalamud.Bindings.ImGui の ImGui.BulletText(string) が利用する cimgui 経路。
    // RSRのツールチップ説明文はこの経路で描画される。
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void BulletTextDelegate(byte* text);

    // Dalamud.Bindings.ImGui の TextWrapped / TreeNode / BulletText は、API15では
    // cimgui の公開 TextWrapped/TreeNode/BulletText を通らず ImGuiP.RenderText 系へ直接描画する。
    // 表示段階だけを差し替えるため、Widget ID や設定値には一切触れない。
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RenderTextDelegate(Vector2 pos, byte* text, byte* textEnd, byte hideTextAfterHash);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RenderTextWrappedDelegate(Vector2 pos, byte* text, byte* textEnd, float wrapWidth);

    // v0.0.72: ImGui.LabelText() の表示値は RenderText/RenderTextWrapped ではなく
    // RenderTextClipped を通る。Allagan Tools の設定ラベルはこの経路を多用する。
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeImRect
    {
        public Vector2 Min;
        public Vector2 Max;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RenderTextClippedDelegate(
        Vector2 posMin, Vector2 posMax, byte* text, byte* textEnd,
        Vector2* textSizeIfKnown, Vector2 align, NativeImRect* clipRect);

    private delegate void DrawListAddTextVec2Delegate(void* drawList, Vector2 pos, uint col, byte* textBegin, byte* textEnd);

    // ImDrawList::AddText(font, size, ...) overload. Some plugins use this path for colored/custom-font labels.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DrawListAddTextFontPtrDelegate(
        void* drawList, void* font, float fontSize, Vector2 pos, uint col,
        byte* textBegin, byte* textEnd, float wrapWidth, Vector4* cpuFineClipRect);

    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commandManager, IGameInteropProvider interop, IPluginLog log, IContextMenu contextMenu)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.interop = interop;
        this.log = log;
        this.contextMenu = contextMenu;
        windowSystem = new WindowSystem("PluginJPHelper");
        mainWindow = new MainWindow(this);
        mojibakeAlertWindow = new MojibakeAlertWindow(this);
        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(mojibakeAlertWindow);
        config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        var mojibakeSchemaBefore = config.MojibakeRulesSchemaVersion;
        var mojibakeRulesBefore = config.MojibakeMarkerRules?.Count ?? 0;
        var mojibakeWhitelistBefore = config.MojibakeWhitelistRules?.Count ?? 0;
        var installerRepairHistoryBefore = config.InstallerMojibakeRepairHistory?.Count ?? 0;
        config.EnsurePlugins();
        if (mojibakeSchemaBefore != config.MojibakeRulesSchemaVersion
            || mojibakeRulesBefore != (config.MojibakeMarkerRules?.Count ?? 0)
            || mojibakeWhitelistBefore != (config.MojibakeWhitelistRules?.Count ?? 0)
            || installerRepairHistoryBefore != (config.InstallerMojibakeRepairHistory?.Count ?? 0))
            pluginInterface.SavePluginConfig(config);
        MojibakeSafety.Configure(config);
        pluginInstallerModule = new PluginInstallerModule(pluginInterface, commandManager, log, QueueMojibakeIncident, RecordInstallerMojibakeRepair);
        communityPosterNameBuffer = config.CommunityPosterName ?? string.Empty;
        // GitHub辞書テストv3で公式CSVを直接適用していたデータを解除する。
        // v4以降は公式フォルダーを配布置き場としてのみ扱い、ローカルへコピーしたCSVだけを利用する。
        var clearedLegacyOfficialOverrides = false;
        foreach (var state in GetPluginConfigSnapshot().Select(x => x.Value))
        {
            if (state.OfficialOverrides.Count == 0) continue;
            state.OfficialOverrides.Clear();
            clearedLegacyOfficialOverrides = true;
        }
        if (clearedLegacyOfficialOverrides) pluginInterface.SavePluginConfig(config);
        EnsureCaptureDictionaries();
        // v0.0.23: 完全リセット版。更新時に一度だけ旧データを全消去する。
        // バンドル済み旧標準辞書も CleanSlateMode で無効化し、今回の再取得結果だけを新基準にする。
        if (config.DataResetVersion < 23)
        {
            foreach (var state in GetPluginConfigSnapshot().Select(x => x.Value))
            {
                state.UserOverrides.Clear();
                state.Locations.Clear();
                state.Enabled = false;
            }
            config.Plugins["RSR"].Enabled = true;
            config.CaptureSchemaVersion = 3;
            config.CleanSlateMode = true;
            config.DataResetVersion = 24;
            pluginInterface.SavePluginConfig(config);
        }
        if (config.DataResetVersion < 24)
        {
            // v0.0.25: 既存のv0.0.23所属情報は保持。再取得は要求しない。
            config.DataResetVersion = 24;
            pluginInterface.SavePluginConfig(config);
        }
        if (config.DataResetVersion < 26)
        {
            // v0.0.26: バンドル済みRSR標準辞書を復元。既存ユーザー訳・所属情報は保持する。
            config.CleanSlateMode = false;
            config.Plugins["RSR"].Enabled = true;
            config.DataResetVersion = 26;
            pluginInterface.SavePluginConfig(config);
        }
        if (config.DataResetVersion < 27)
        {
            // v0.0.28: v0.0.23で取得済みの所属を標準辞書へ復元。
            // ユーザー翻訳は保持し、所属だけをクリーンに入れ直す。
            var rsr = config.Plugins["RSR"];
            rsr.Locations.Clear();
            foreach (var kv in RsrBundledLocations.ByKey)
                rsr.Locations[kv.Key] = new DictionaryLocation { Menu = kv.Value.Menu, Section = kv.Value.Section };
            config.CleanSlateMode = false;
            rsr.Enabled = true;
            config.DataResetVersion = 27;
            pluginInterface.SavePluginConfig(config);
            _ = ExportDictionaryCsv("RSR");
        }
        if (config.DataResetVersion < 42)
        {
            // v0.0.66: 翻訳済み/未翻訳を問わず、取得済みの所属情報を差分で復元する。
            // 既存のユーザー訳は保持し、翻訳が空欄の項目も辞書一覧へ出るよう Locations を追加する。
            var rsr = config.Plugins["RSR"];
            foreach (var kv in RsrBundledLocations.ByKey)
                rsr.Locations[kv.Key] = new DictionaryLocation { Menu = kv.Value.Menu, Section = kv.Value.Section };
            foreach (var kv in RsrCapturedLocationsV42.ByKey)
                rsr.Locations[kv.Key] = new DictionaryLocation { Menu = kv.Value.Menu, Section = kv.Value.Section };
            config.CleanSlateMode = false;
            rsr.Enabled = true;
            config.DataResetVersion = 42;
            pluginInterface.SavePluginConfig(config);
            _ = ExportDictionaryCsv("RSR");
        }

        // v0.0.73: InventoryTools の同梱差分CSVから最大バージョンを起動時に1回だけ確認する。
        // 毎フレーム検索はせず、同じCSVが既に読込済みなら再読込もしない。
        _ = EnsureLatestBundledInventoryToolsCsv();

        EnsureInitialRsrCsv();
        // v0.5.0: 起動直後の自動HTTP通信は行わない。
        // Dalamud/Plugin Installerのrepo読込や他プラグイン通信を最優先し、
        // 公式・コミュニティ辞書の自動確認はPJHのメイン画面を開いた時だけ、
        // PluginManagerが十分安定してから行う。手動取得ボタンは従来どおり利用できる。

        commandManager.AddHandler(Command, new CommandInfo(OnCommand) { HelpMessage = "Plugin JP Helper を開きます。" });
        // 背景処理から委譲された適用処理を毎フレーム最初に流し込む。
        // windowSystem.Draw より前に登録し、UI が参照する前に反映されるようにする。
        pluginInterface.UiBuilder.Draw += DrainMainThreadWork;
        pluginInterface.UiBuilder.Draw += windowSystem.Draw;
        pluginInterface.UiBuilder.Draw += pluginInstallerModule.Tick;
        pluginInterface.UiBuilder.Draw += ProcessMojibakeSafetyIncidents;
        pluginInterface.UiBuilder.OpenConfigUi += OpenUi;
        pluginInterface.UiBuilder.OpenMainUi += OpenUi;
        contextMenu.OnMenuOpened += OnContextMenuOpened;
        InstallHooks();
    }

    // ImGui のバージョンによって cimgui のエクスポート名が変わる関数がある。
    // 例: Combo(getter) は ImGui 1.89.7 で getter のシグネチャが変わり、
    //     cimgui 側の名前も igCombo_FnBoolPtr から igCombo_FnStrPtr になった。
    //       旧 (〜1.89.6) bool (*)(void* data, int idx, const char** out_text)  -> igCombo_FnBoolPtr
    //       新 (1.89.7〜) const char* (*)(void* user_data, int idx)             -> igCombo_FnStrPtr
    // どちらも関数ポインタを nint で受けるため ABI は同じ。
    // Dalamud がどの ImGui を同梱していても刺さるよう、候補を順に試す。
    private Hook<T> HookFirstAvailable<T>(string[] symbols, T detour) where T : Delegate
    {
        Exception? lastError = null;
        foreach (var symbol in symbols)
        {
            try { return interop.HookFromSymbol("cimgui.dll", symbol, detour); }
            catch (Exception ex) { lastError = ex; }
        }

        throw lastError ?? new InvalidOperationException(
            $"cimgui.dll に該当するシンボルがありません: {string.Join(" / ", symbols)}");
    }

    private void InstallHooks()
    {
        var installed = new List<string>();
        var failed = new List<string>();
        try { textHook = interop.HookFromSymbol<TextUnformattedDelegate>("cimgui.dll", "igTextUnformatted", TextUnformattedDetour); textHook.Enable(); installed.Add("Text"); }
        catch (Exception ex) { failed.Add("Text"); log.Warning(ex, "[PluginJPHelper] igTextUnformatted hook failed"); }
        try { textWrappedHook = interop.HookFromSymbol<TextWrappedDelegate>("cimgui.dll", "igTextWrapped", TextWrappedDetour); textWrappedHook.Enable(); installed.Add("TextWrapped"); }
        catch (Exception ex) { failed.Add("TextWrapped"); log.Warning(ex, "[PluginJPHelper] igTextWrapped hook failed"); }
        try { textVHook = interop.HookFromSymbol<TextVDelegate>("cimgui.dll", "igTextV", TextVDetour); textVHook.Enable(); installed.Add("TextV"); }
        catch (Exception ex) { failed.Add("TextV"); log.Warning(ex, "[PluginJPHelper] igTextV hook failed"); }
        try { textColoredVHook = interop.HookFromSymbol<TextColoredVDelegate>("cimgui.dll", "igTextColoredV", TextColoredVDetour); textColoredVHook.Enable(); installed.Add("TextColoredV"); }
        catch (Exception ex) { failed.Add("TextColoredV"); log.Warning(ex, "[PluginJPHelper] igTextColoredV hook failed"); }
        try { textDisabledVHook = interop.HookFromSymbol<TextDisabledVDelegate>("cimgui.dll", "igTextDisabledV", TextDisabledVDetour); textDisabledVHook.Enable(); installed.Add("TextDisabledV"); }
        catch (Exception ex) { failed.Add("TextDisabledV"); log.Warning(ex, "[PluginJPHelper] igTextDisabledV hook failed"); }
        try { checkboxHook = interop.HookFromSymbol<CheckboxDelegate>("cimgui.dll", "igCheckbox", CheckboxDetour); checkboxHook.Enable(); installed.Add("Checkbox"); }
        catch (Exception ex) { failed.Add("Checkbox"); log.Warning(ex, "[PluginJPHelper] igCheckbox hook failed"); }
        try { buttonHook = interop.HookFromSymbol<ButtonDelegate>("cimgui.dll", "igButton", ButtonDetour); buttonHook.Enable(); installed.Add("Button"); }
        catch (Exception ex) { failed.Add("Button"); log.Warning(ex, "[PluginJPHelper] igButton hook failed"); }
        try { buttonExHook = interop.HookFromSymbol<ButtonExDelegate>("cimgui.dll", "igButtonEx", ButtonExDetour); buttonExHook.Enable(); installed.Add("ButtonEx"); }
        catch (Exception ex) { failed.Add("ButtonEx"); log.Warning(ex, "[PluginJPHelper] igButtonEx hook failed"); }
        try { selectableHook = interop.HookFromSymbol<SelectableDelegate>("cimgui.dll", "igSelectable_Bool", SelectableDetour); selectableHook.Enable(); installed.Add("Selectable"); }
        catch (Exception ex) { failed.Add("Selectable"); log.Warning(ex, "[PluginJPHelper] igSelectable_Bool hook failed"); }
        try { selectablePtrHook = interop.HookFromSymbol<SelectablePtrDelegate>("cimgui.dll", "igSelectable_BoolPtr", SelectablePtrDetour); selectablePtrHook.Enable(); installed.Add("SelectablePtr"); }
        catch (Exception ex) { failed.Add("SelectablePtr"); log.Warning(ex, "[PluginJPHelper] igSelectable_BoolPtr hook failed"); }
        try { comboStrArrHook = interop.HookFromSymbol<ComboStrArrDelegate>("cimgui.dll", "igCombo_Str_arr", ComboStrArrDetour); comboStrArrHook.Enable(); installed.Add("ComboStrArr"); }
        catch (Exception ex) { failed.Add("ComboStrArr"); log.Warning(ex, "[PluginJPHelper] igCombo_Str_arr hook failed"); }
        try { comboStrHook = interop.HookFromSymbol<ComboStrDelegate>("cimgui.dll", "igCombo_Str", ComboStrDetour); comboStrHook.Enable(); installed.Add("ComboStr"); }
        catch (Exception ex) { failed.Add("ComboStr"); log.Warning(ex, "[PluginJPHelper] igCombo_Str hook failed"); }
        // Combo(getter) は ImGui のバージョンでエクスポート名が変わる。新旧どちらでも刺さるよう順に試す。
        try { comboFnPtrHook = HookFirstAvailable<ComboFnPtrDelegate>(["igCombo_FnStrPtr", "igCombo_FnBoolPtr"], ComboFnPtrDetour); comboFnPtrHook.Enable(); installed.Add("ComboFn"); }
        catch (Exception ex) { failed.Add("ComboFn"); log.Warning(ex, "[PluginJPHelper] Combo(getter) hook failed"); }
        try { beginComboHook = interop.HookFromSymbol<BeginComboDelegate>("cimgui.dll", "igBeginCombo", BeginComboDetour); beginComboHook.Enable(); installed.Add("BeginCombo"); }
        catch (Exception ex) { failed.Add("BeginCombo"); log.Warning(ex, "[PluginJPHelper] igBeginCombo hook failed"); }
        try { endComboHook = interop.HookFromSymbol<EndComboDelegate>("cimgui.dll", "igEndCombo", EndComboDetour); endComboHook.Enable(); installed.Add("EndCombo"); }
        catch (Exception ex) { failed.Add("EndCombo"); log.Warning(ex, "[PluginJPHelper] igEndCombo hook failed"); }
        try { sliderIntHook = interop.HookFromSymbol<SliderIntDelegate>("cimgui.dll", "igSliderInt", SliderIntDetour); sliderIntHook.Enable(); installed.Add("SliderInt"); }
        catch (Exception ex) { failed.Add("SliderInt"); log.Warning(ex, "[PluginJPHelper] igSliderInt hook failed"); }
        try { tableSetupColumnHook = interop.HookFromSymbol<TableSetupColumnDelegate>("cimgui.dll", "igTableSetupColumn", TableSetupColumnDetour); tableSetupColumnHook.Enable(); installed.Add("TableSetupColumn"); }
        catch (Exception ex) { failed.Add("TableSetupColumn"); log.Warning(ex, "[PluginJPHelper] igTableSetupColumn hook failed"); }
        try { beginTabItemHook = interop.HookFromSymbol<BeginTabItemDelegate>("cimgui.dll", "igBeginTabItem", BeginTabItemDetour); beginTabItemHook.Enable(); installed.Add("BeginTabItem"); }
        catch (Exception ex) { failed.Add("BeginTabItem"); log.Warning(ex, "[PluginJPHelper] igBeginTabItem hook failed"); }
        try { menuItemBoolHook = interop.HookFromSymbol<MenuItemBoolDelegate>("cimgui.dll", "igMenuItem_Bool", MenuItemBoolDetour); menuItemBoolHook.Enable(); installed.Add("MenuItem"); }
        catch (Exception ex) { failed.Add("MenuItem"); log.Warning(ex, "[PluginJPHelper] igMenuItem_Bool hook failed"); }
        try { menuItemBoolPtrHook = interop.HookFromSymbol<MenuItemBoolPtrDelegate>("cimgui.dll", "igMenuItem_BoolPtr", MenuItemBoolPtrDetour); menuItemBoolPtrHook.Enable(); installed.Add("MenuItemPtr"); }
        catch (Exception ex) { failed.Add("MenuItemPtr"); log.Warning(ex, "[PluginJPHelper] igMenuItem_BoolPtr hook failed"); }
        try { beginMenuHook = interop.HookFromSymbol<BeginMenuDelegate>("cimgui.dll", "igBeginMenu", BeginMenuDetour); beginMenuHook.Enable(); installed.Add("BeginMenu"); }
        catch (Exception ex) { failed.Add("BeginMenu"); log.Warning(ex, "[PluginJPHelper] igBeginMenu hook failed"); }
        try { separatorTextHook = interop.HookFromSymbol<SeparatorTextDelegate>("cimgui.dll", "igSeparatorText", SeparatorTextDetour); separatorTextHook.Enable(); installed.Add("SeparatorText"); }
        // igSeparatorText は ImGui 1.89 で追加された API。
        // 現行 Dalamud が同梱する cimgui (v1.65.4-543 / ImGui 1.88 相当) には存在しないため
        // ここは失敗するのが正常。Dalamud が ImGui を上げれば自動的に有効になる。
        catch (Exception ex) { failed.Add("SeparatorText"); log.Information(ex, "[PluginJPHelper] igSeparatorText hook unavailable (ImGui 1.89 未満では正常)"); }
        try { beginHook = interop.HookFromSymbol<BeginDelegate>("cimgui.dll", "igBegin", BeginDetour); beginHook.Enable(); installed.Add("Begin"); }
        catch (Exception ex) { failed.Add("Begin"); log.Warning(ex, "[PluginJPHelper] igBegin hook failed"); }
        try { endHook = interop.HookFromSymbol<EndDelegate>("cimgui.dll", "igEnd", EndDetour); endHook.Enable(); installed.Add("End"); }
        catch (Exception ex) { failed.Add("End"); log.Warning(ex, "[PluginJPHelper] igEnd hook failed"); }
        try { beginPopupHook = interop.HookFromSymbol<BeginPopupDelegate>("cimgui.dll", "igBeginPopup", BeginPopupDetour); beginPopupHook.Enable(); installed.Add("BeginPopup"); }
        catch (Exception ex) { failed.Add("BeginPopup"); log.Warning(ex, "[PluginJPHelper] igBeginPopup hook failed"); }
        try { beginPopupModalHook = interop.HookFromSymbol<BeginPopupModalDelegate>("cimgui.dll", "igBeginPopupModal", BeginPopupModalDetour); beginPopupModalHook.Enable(); installed.Add("BeginPopupModal"); }
        catch (Exception ex) { failed.Add("BeginPopupModal"); log.Warning(ex, "[PluginJPHelper] igBeginPopupModal hook failed"); }
        try { endPopupHook = interop.HookFromSymbol<EndPopupDelegate>("cimgui.dll", "igEndPopup", EndPopupDetour); endPopupHook.Enable(); installed.Add("EndPopup"); }
        catch (Exception ex) { failed.Add("EndPopup"); log.Warning(ex, "[PluginJPHelper] igEndPopup hook failed"); }
        try { radioButtonBoolHook = interop.HookFromSymbol<RadioButtonBoolDelegate>("cimgui.dll", "igRadioButton_Bool", RadioButtonBoolDetour); radioButtonBoolHook.Enable(); installed.Add("RadioButtonBool"); }
        catch (Exception ex) { failed.Add("RadioButtonBool"); log.Warning(ex, "[PluginJPHelper] igRadioButton_Bool hook failed"); }
        try { radioButtonIntPtrHook = interop.HookFromSymbol<RadioButtonIntPtrDelegate>("cimgui.dll", "igRadioButton_IntPtr", RadioButtonIntPtrDetour); radioButtonIntPtrHook.Enable(); installed.Add("RadioButtonIntPtr"); }
        catch (Exception ex) { failed.Add("RadioButtonIntPtr"); log.Warning(ex, "[PluginJPHelper] igRadioButton_IntPtr hook failed"); }
        try { treeNodeStrHook = interop.HookFromSymbol<TreeNodeStrDelegate>("cimgui.dll", "igTreeNode_Str", TreeNodeStrDetour); treeNodeStrHook.Enable(); installed.Add("TreeNode"); }
        catch (Exception ex) { failed.Add("TreeNode"); log.Warning(ex, "[PluginJPHelper] igTreeNode_Str hook failed"); }
        try { treeNodeExStrHook = interop.HookFromSymbol<TreeNodeExStrDelegate>("cimgui.dll", "igTreeNodeEx_Str", TreeNodeExStrDetour); treeNodeExStrHook.Enable(); installed.Add("TreeNodeEx"); }
        catch (Exception ex) { failed.Add("TreeNodeEx"); log.Warning(ex, "[PluginJPHelper] igTreeNodeEx_Str hook failed"); }
        try { collapsingHeaderTreeNodeFlagsHook = interop.HookFromSymbol<CollapsingHeaderTreeNodeFlagsDelegate>("cimgui.dll", "igCollapsingHeader_TreeNodeFlags", CollapsingHeaderTreeNodeFlagsDetour); collapsingHeaderTreeNodeFlagsHook.Enable(); installed.Add("CollapsingHeader"); }
        catch (Exception ex) { failed.Add("CollapsingHeader"); log.Warning(ex, "[PluginJPHelper] igCollapsingHeader_TreeNodeFlags hook failed"); }
        try { collapsingHeaderBoolPtrHook = interop.HookFromSymbol<CollapsingHeaderBoolPtrDelegate>("cimgui.dll", "igCollapsingHeader_BoolPtr", CollapsingHeaderBoolPtrDetour); collapsingHeaderBoolPtrHook.Enable(); installed.Add("CollapsingHeaderPtr"); }
        catch (Exception ex) { failed.Add("CollapsingHeaderPtr"); log.Warning(ex, "[PluginJPHelper] igCollapsingHeader_BoolPtr hook failed"); }
        try { bulletTextHook = interop.HookFromSymbol<BulletTextDelegate>("cimgui.dll", "igBulletText", BulletTextDetour); bulletTextHook.Enable(); installed.Add("BulletText"); }
        catch (Exception ex) { failed.Add("BulletText"); log.Warning(ex, "[PluginJPHelper] igBulletText hook failed"); }
        try { renderTextHook = interop.HookFromSymbol<RenderTextDelegate>("cimgui.dll", "igRenderText", RenderTextDetour); renderTextHook.Enable(); installed.Add("RenderText"); }
        catch (Exception ex) { failed.Add("RenderText"); log.Warning(ex, "[PluginJPHelper] igRenderText hook failed"); }
        try { renderTextWrappedHook = interop.HookFromSymbol<RenderTextWrappedDelegate>("cimgui.dll", "igRenderTextWrapped", RenderTextWrappedDetour); renderTextWrappedHook.Enable(); installed.Add("RenderTextWrapped"); }
        catch (Exception ex) { failed.Add("RenderTextWrapped"); log.Warning(ex, "[PluginJPHelper] igRenderTextWrapped hook failed"); }
        try { renderTextClippedHook = interop.HookFromSymbol<RenderTextClippedDelegate>("cimgui.dll", "igRenderTextClipped", RenderTextClippedDetour); renderTextClippedHook.Enable(); installed.Add("RenderTextClipped"); }
        catch (Exception ex) { failed.Add("RenderTextClipped"); log.Warning(ex, "[PluginJPHelper] igRenderTextClipped hook failed"); }
        try { drawListAddTextVec2Hook = interop.HookFromSymbol<DrawListAddTextVec2Delegate>("cimgui.dll", "ImDrawList_AddText_Vec2", DrawListAddTextVec2Detour); drawListAddTextVec2Hook.Enable(); installed.Add("DrawListAddText"); }
        catch (Exception ex) { failed.Add("DrawListAddText"); log.Warning(ex, "[PluginJPHelper] ImDrawList_AddText_Vec2 hook failed"); }
        try { drawListAddTextFontPtrHook = interop.HookFromSymbol<DrawListAddTextFontPtrDelegate>("cimgui.dll", "ImDrawList_AddText_FontPtr", DrawListAddTextFontPtrDetour); drawListAddTextFontPtrHook.Enable(); installed.Add("DrawListAddTextFont"); }
        catch (Exception ex) { failed.Add("DrawListAddTextFont"); log.Warning(ex, "[PluginJPHelper] ImDrawList_AddText_FontPtr hook failed"); }
        hookStatus = installed.Count == 0 ? "フック失敗（Dalamudログを確認）" : $"有効: {string.Join(", ", installed)}" + (failed.Count > 0 ? $" / 失敗: {string.Join(", ", failed)}" : string.Empty);
        hookInstalledNames = [.. installed];
        hookFailedNames = [.. failed];
    }

    private void TextUnformattedDetour(byte* text, byte* textEnd)
    {
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled)) CapturePointer(text, textEnd, "Text"); } catch { }
        if (!drawingOwnUi && TryTranslatePointer(text, textEnd, false, out var translated)) { CallTextOriginal(translated); Interlocked.Increment(ref translatedCount); return; }
        textHook!.Original(text, textEnd);
    }

    private void TextWrappedDetour(byte* text)
    {
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled)) CapturePointer(text, null, "TextWrapped"); } catch { }

        if (!drawingOwnUi && TryTranslatePointer(text, null, false, out var translated))
        {
            Interlocked.Increment(ref translatedCount);
            // igTextWrapped は printf 形式のAPI。Dalamud の安全ラッパーと同様、% はリテラル扱いにする。
            var bytes = Encoding.UTF8.GetBytes(EscapeFormatLiteral(translated) + "\0");
            fixed (byte* p = bytes) { textWrappedHook!.Original(p); return; }
        }
        textWrappedHook!.Original(text);
    }

    // igTextWrapped / igTextV / igTextColoredV / igTextDisabledV は printf 形式のAPI。
    // IsSafeFixedTextFormat が見るのは「原文」に % が無いことだけなので、訳文側の % は素通りする。
    // 例: 同梱の Pawprint 辞書には原文に % が無く訳文に "50%未満" を含む行が実在する。
    // これをそのまま書式文字列として渡すと %未 が不正な変換指定子となり、
    // vsnprintf の挙動は未定義（表示化け〜可変長引数の読み違いによるクラッシュ）になる。
    private static string EscapeFormatLiteral(string text)
        => text.Replace("%", "%%", StringComparison.Ordinal);

    private static bool IsSafeFixedTextFormat(byte* fmt)
    {
        if (fmt == null) return false;
        try
        {
            var source = Marshal.PtrToStringUTF8((nint)fmt);
            return !string.IsNullOrEmpty(source) && !source.Contains('%');
        }
        catch { return false; }
    }

    private void TextVDetour(byte* fmt, nint args)
    {
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled) && IsSafeFixedTextFormat(fmt)) CapturePointer(fmt, null, "TextV"); } catch { }
        if (!drawingOwnUi && IsSafeFixedTextFormat(fmt) && TryTranslatePointer(fmt, null, false, out var translated))
        {
            Interlocked.Increment(ref translatedCount);
            // igTextV は printf 形式のAPI。TextWrapped と同様、訳文の % はリテラル扱いにする。
            var bytes = Encoding.UTF8.GetBytes(EscapeFormatLiteral(translated) + "\0");
            fixed (byte* p = bytes) { textVHook!.Original(p, args); return; }
        }
        textVHook!.Original(fmt, args);
    }

    private void TextColoredVDetour(Vector4 col, byte* fmt, nint args)
    {
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled) && IsSafeFixedTextFormat(fmt)) CapturePointer(fmt, null, "TextColoredV"); } catch { }
        if (!drawingOwnUi && IsSafeFixedTextFormat(fmt) && TryTranslatePointer(fmt, null, false, out var translated))
        {
            Interlocked.Increment(ref translatedCount);
            // igTextColoredV は printf 形式のAPI。訳文の % はリテラル扱いにする。
            var bytes = Encoding.UTF8.GetBytes(EscapeFormatLiteral(translated) + "\0");
            fixed (byte* p = bytes) { textColoredVHook!.Original(col, p, args); return; }
        }
        textColoredVHook!.Original(col, fmt, args);
    }

    private void TextDisabledVDetour(byte* fmt, nint args)
    {
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled) && IsSafeFixedTextFormat(fmt)) CapturePointer(fmt, null, "TextDisabledV"); } catch { }
        if (!drawingOwnUi && IsSafeFixedTextFormat(fmt) && TryTranslatePointer(fmt, null, false, out var translated))
        {
            Interlocked.Increment(ref translatedCount);
            // igTextDisabledV は printf 形式のAPI。訳文の % はリテラル扱いにする。
            var bytes = Encoding.UTF8.GetBytes(EscapeFormatLiteral(translated) + "\0");
            fixed (byte* p = bytes) { textDisabledVHook!.Original(p, args); return; }
        }
        textDisabledVHook!.Original(fmt, args);
    }

    private byte CheckboxDetour(byte* label, byte* value)
    {
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled)) CapturePointer(label, null, "Checkbox"); } catch { }
        if (!drawingOwnUi && TryTranslatePointer(label, null, true, out var translated)) { Interlocked.Increment(ref translatedCount); return CallCheckboxOriginal(MakeDisplayOnlyInteractiveLabel(label, translated), value); }
        return checkboxHook!.Original(label, value);
    }

    private byte ButtonDetour(byte* label, Vector2 size)
    {
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled)) CapturePointer(label, null, "Button"); } catch { }
        if (!drawingOwnUi && TryTranslatePointer(label, null, true, out var translated)) { Interlocked.Increment(ref translatedCount); return CallButtonOriginal(MakeDisplayOnlyInteractiveLabel(label, translated), size); }
        return buttonHook!.Original(label, size);
    }

    // v0.4.3: ImSharp (Penumbra/Luna等) の通常ボタンは igButton ではなく igButtonEx を直接呼ぶ。
    // 既存のButtonフックと同じ完全一致・ImGui ID保持の処理だけを適用する。
    private byte ButtonExDetour(byte* label, Vector2 size, int flags)
    {
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled)) CapturePointer(label, null, "ButtonEx"); } catch { }

        if (!drawingOwnUi && TryTranslatePointer(label, null, true, out var translated))
        {
            Interlocked.Increment(ref translatedCount);
            var display = MakeDisplayOnlyInteractiveLabel(label, translated);
            var bytes = Encoding.UTF8.GetBytes(display + "\0");
            fixed (byte* p = bytes)
                return buttonExHook!.Original(p, size, flags);
        }

        return buttonExHook!.Original(label, size, flags);
    }


    private byte SelectableDetour(byte* label, byte selected, int flags, Vector2 size)
    {
        string raw = string.Empty;
        try { if (label != null) raw = Marshal.PtrToStringUTF8((nint)label) ?? string.Empty; } catch { }
        try { if (!drawingOwnUi) UpdateRsrNavigationContext(label, selected != 0); } catch { }
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled)) CapturePointer(label, null, "Selectable"); } catch { }

        byte result;
        if (!drawingOwnUi && TryTranslateSelectableLabel(label, out var displayOnly))
        {
            Interlocked.Increment(ref translatedCount);
            if (comboOpenDepth > 0) Interlocked.Increment(ref comboTranslatedItems);
            var bytes = Encoding.UTF8.GetBytes(displayOnly + "\0");
            fixed (byte* ptr = bytes) result = selectableHook!.Original(ptr, selected, flags, size);
        }
        else
        {
            result = selectableHook!.Original(label, selected, flags, size);
        }

        if (!drawingOwnUi && comboOpenDepth > 0) Interlocked.Increment(ref comboSelectableCalls);
        try { if (!drawingOwnUi && result != 0) UpdateRsrNavigationContextAfterClick(raw); } catch { }
        return result;
    }

    private byte SelectablePtrDetour(byte* label, byte* selected, int flags, Vector2 size)
    {
        string raw = string.Empty;
        try { if (label != null) raw = Marshal.PtrToStringUTF8((nint)label) ?? string.Empty; } catch { }
        try { if (!drawingOwnUi) UpdateRsrNavigationContext(label, selected != null && *selected != 0); } catch { }
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled)) CapturePointer(label, null, "SelectablePtr"); } catch { }

        byte result;
        if (!drawingOwnUi && TryTranslateSelectableLabel(label, out var displayOnly))
        {
            Interlocked.Increment(ref translatedCount);
            if (comboOpenDepth > 0) Interlocked.Increment(ref comboTranslatedItems);
            var bytes = Encoding.UTF8.GetBytes(displayOnly + "\0");
            fixed (byte* ptr = bytes) result = selectablePtrHook!.Original(ptr, selected, flags, size);
        }
        else
        {
            result = selectablePtrHook!.Original(label, selected, flags, size);
        }

        if (!drawingOwnUi && comboOpenDepth > 0) Interlocked.Increment(ref comboSelectableCalls);
        try { if (!drawingOwnUi && result != 0) UpdateRsrNavigationContextAfterClick(raw); } catch { }
        return result;
    }

    private bool TryTranslateSelectableLabel(byte* label, out string displayOnly)
    {
        try
        {
            return TryTranslateSelectableLabelCore(label, out displayOnly);
        }
        catch (Exception ex)
        {
            displayOnly = string.Empty;
            LogDetourFailure(nameof(TryTranslateSelectableLabel), ex);
            return false;
        }
    }

    private bool TryTranslateSelectableLabelCore(byte* label, out string displayOnly)
    {
        displayOnly = string.Empty;
        if (label == null) return false;
        string? raw;
        try { raw = Marshal.PtrToStringUTF8((nint)label); }
        catch { return false; }
        if (string.IsNullOrEmpty(raw)) return false;

        // 通常Selectableは完全一致だけ。Combo内だけは表示部(##/###より前)で辞書照合し、
        // Widget IDは元ラベルをそのまま保持する。
        if (TryTranslate(raw, out var exact))
        {
            displayOnly = MakeDisplayOnlyInteractiveLabel(label, exact);
            return true;
        }
        // v0.0.66: Selectableも動的表示文字列の部分翻訳を許可する。
        // 内部IDは MakeDisplayOnlyInteractiveLabel() で元ラベルを保持する。
        if (TryTranslatePointer(label, null, true, out var partial))
        {
            displayOnly = MakeDisplayOnlyInteractiveLabel(label, partial);
            return true;
        }

        if (comboOpenDepth <= 0) return false;

        var visible = VisibleLabel(raw);
        if (string.Equals(visible, raw, StringComparison.Ordinal) || !TryTranslate(visible, out var ja)) return false;
        displayOnly = MakeDisplayOnlyInteractiveLabel(label, ja);
        return true;
    }

    private byte ComboStrArrDetour(byte* label, int* currentItem, byte** items, int itemsCount, int popupMaxHeightInItems)
    {
        // RSRは TargetingType 等のプルダウンを ImGui.Combo(string[]) で描画する。
        // この経路では内部で作られる Selectable は外側のSelectableフックを通らないため、
        // 配列自体を「日本語表示###英語ID」に差し替える。currentItem(選択index)は一切変更しない。
        if (drawingOwnUi || items == null || itemsCount <= 0 || itemsCount > 512)
            return comboStrArrHook!.Original(label, currentItem, items, itemsCount, popupMaxHeightInItems);

        Interlocked.Increment(ref comboHookCalls);
        var allocated = new List<nint>();
        try
        {
            byte** translatedItems = stackalloc byte*[itemsCount];
            var changed = false;
            for (var i = 0; i < itemsCount; i++)
            {
                translatedItems[i] = items[i];
                if (items[i] == null) continue;
                var source = Marshal.PtrToStringUTF8((nint)items[i]);
                if (string.IsNullOrEmpty(source)) continue;
                if (!TryTranslate(source, out var translated)) continue;

                // Comboは選択値をindexで保持しているため、候補文字列を日本語へ置換しても
                // RSR内部の値・保存値は変化しない。候補側に###IDを付けるとpreview表示で
                // そのまま見える経路があるため、ここでは表示文字列だけを渡す。
                var bytes = Encoding.UTF8.GetBytes(translated + "\0");
                var mem = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, mem, bytes.Length);
                allocated.Add(mem);
                translatedItems[i] = (byte*)mem;
                changed = true;
                Interlocked.Increment(ref comboTranslatedItems);
            }

            return changed
                ? comboStrArrHook!.Original(label, currentItem, translatedItems, itemsCount, popupMaxHeightInItems)
                : comboStrArrHook!.Original(label, currentItem, items, itemsCount, popupMaxHeightInItems);
        }
        finally
        {
            foreach (var mem in allocated) Marshal.FreeHGlobal(mem);
        }
    }


    private byte ComboStrDetour(byte* label, int* currentItem, byte* itemsSeparatedByZeros, int popupMaxHeightInItems)
    {
        if (drawingOwnUi || itemsSeparatedByZeros == null)
            return comboStrHook!.Original(label, currentItem, itemsSeparatedByZeros, popupMaxHeightInItems);

        Interlocked.Increment(ref comboStrCalls);
        try
        {
            // NUL区切り文字列を最大64KiBまで安全に読み、最後の\0\0で終了する。
            var items = new List<string>();
            var p = itemsSeparatedByZeros;
            var total = 0;
            while (total < 65536)
            {
                var len = 0;
                while (total + len < 65536 && p[len] != 0) len++;
                if (len == 0) break;
                var src = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(p, len));
                items.Add(src);
                p += len + 1;
                total += len + 1;
            }
            if (items.Count == 0)
                return comboStrHook!.Original(label, currentItem, itemsSeparatedByZeros, popupMaxHeightInItems);

            var changed = false;
            var translated = new List<string>(items.Count);
            foreach (var src in items)
            {
                if (TryTranslate(src, out var ja))
                {
                    translated.Add(ja);
                    changed = true;
                    Interlocked.Increment(ref comboTranslatedItems);
                }
                else translated.Add(src);
            }
            if (!changed)
                return comboStrHook!.Original(label, currentItem, itemsSeparatedByZeros, popupMaxHeightInItems);

            using var ms = new MemoryStream();
            foreach (var item in translated)
            {
                var b = Encoding.UTF8.GetBytes(item);
                ms.Write(b); ms.WriteByte(0);
            }
            ms.WriteByte(0);
            var packed = ms.ToArray();
            fixed (byte* pp = packed)
                return comboStrHook!.Original(label, currentItem, pp, popupMaxHeightInItems);
        }
        catch
        {
            return comboStrHook!.Original(label, currentItem, itemsSeparatedByZeros, popupMaxHeightInItems);
        }
    }

    private byte ComboFnPtrDetour(byte* label, int* currentItem, nint getter, void* userData, int itemsCount, int popupMaxHeightInItems)
    {
        // まず実機でこの経路を使っているかだけ確認する。getter差し替えは誤動作リスクがあるためまだ行わない。
        if (!drawingOwnUi) Interlocked.Increment(ref comboFnCalls);
        return comboFnPtrHook!.Original(label, currentItem, getter, userData, itemsCount, popupMaxHeightInItems);
    }

    private void RecordHookDiagnostic(string hookKind, byte* textPtr)
    {
        // 記録がOFFのときは文字列化すら行わない。毎フレーム通る経路なので、
        // ここで抜けないと誰も見ないログのために確保コストを払い続けることになる。
        if (!hookDiagnosticsRecording || drawingOwnUi || textPtr == null) return;
        try
        {
            var text = Marshal.PtrToStringUTF8((nint)textPtr) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text)) return;
            hookDiagnosticEntries.Enqueue(new HookDiagnosticEntry(
                DateTime.Now.ToString("HH:mm:ss"),
                hookKind,
                text,
                CurrentWindowName,
                CurrentWindowOwner,
                captureEnabled ? capturePlugin : string.Empty,
                captureEnabled && IsCurrentWindowOwnedBy(capturePlugin)));
            while (hookDiagnosticEntries.Count > MaxHookDiagnosticEntries && hookDiagnosticEntries.TryDequeue(out _)) { }
        }
        catch { }
    }

    private byte BeginComboDetour(byte* label, byte* previewValue, int flags)
    {
        // 自前UIぶんを数えると、診断画面を開いているだけで件数が伸びて実態が読めなくなる。
        if (!drawingOwnUi) Interlocked.Increment(ref beginComboCalls);
        RecordHookDiagnostic("BeginComboLabel", label);
        if (previewValue != null) RecordHookDiagnostic("BeginComboPreview", previewValue);

        // BeginCombo は「左側ラベル」と「現在選択中の preview」の2つを別々に描画する。
        // preview だけでは Select Route / Select Mode 等のラベルが英語のまま残るため、両方を翻訳する。
        try
        {
            if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled))
            {
                CapturePointer(label, null, "BeginComboLabel");
                if (previewValue != null) CapturePointer(previewValue, null, "BeginComboPreview");
            }
        }
        catch { }

        byte* callLabel = label;
        byte* callPreview = previewValue;
        byte[]? labelBytes = null;
        byte[]? previewBytes = null;

        try
        {
            if (!drawingOwnUi && label != null && TryTranslatePointer(label, null, true, out var labelJa))
            {
                var display = MakeDisplayOnlyInteractiveLabel(label, labelJa);
                labelBytes = Encoding.UTF8.GetBytes(display + "\0");
            }

            if (!drawingOwnUi && previewValue != null && TryTranslatePointer(previewValue, null, false, out var previewJa))
                previewBytes = Encoding.UTF8.GetBytes(previewJa + "\0");

            byte result;
            if (labelBytes != null && previewBytes != null)
            {
                fixed (byte* lp = labelBytes)
                fixed (byte* pp = previewBytes)
                    result = beginComboHook!.Original(lp, pp, flags);
            }
            else if (labelBytes != null)
            {
                fixed (byte* lp = labelBytes)
                    result = beginComboHook!.Original(lp, callPreview, flags);
            }
            else if (previewBytes != null)
            {
                fixed (byte* pp = previewBytes)
                    result = beginComboHook!.Original(callLabel, pp, flags);
            }
            else
            {
                result = beginComboHook!.Original(callLabel, callPreview, flags);
            }

            if (!drawingOwnUi && result != 0) comboOpenDepth++;
            return result;
        }
        catch
        {
            var result = beginComboHook!.Original(label, previewValue, flags);
            if (!drawingOwnUi && result != 0) comboOpenDepth++;
            return result;
        }
    }

    private byte SliderIntDetour(byte* label, int* value, int min, int max, byte* format, int flags)
    {
        if (!drawingOwnUi) Interlocked.Increment(ref sliderIntCalls);
        RecordHookDiagnostic("SliderInt", label);
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled)) CapturePointer(label, null, "SliderInt"); } catch { }

        if (!drawingOwnUi && label != null && TryTranslatePointer(label, null, true, out var translated))
        {
            var display = MakeDisplayOnlyInteractiveLabel(label, translated);
            var bytes = Encoding.UTF8.GetBytes(display + "\0");
            fixed (byte* p = bytes)
            {
                Interlocked.Increment(ref translatedCount);
                return sliderIntHook!.Original(p, value, min, max, format, flags);
            }
        }

        return sliderIntHook!.Original(label, value, min, max, format, flags);
    }

    private void TableSetupColumnDetour(byte* label, int flags, float initWidthOrWeight, uint userId)
    {
        if (!drawingOwnUi) Interlocked.Increment(ref tableSetupColumnCalls);
        RecordHookDiagnostic("TableSetupColumn", label);
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled)) CapturePointer(label, null, "TableSetupColumn"); } catch { }

        if (!drawingOwnUi && label != null && TryTranslatePointer(label, null, true, out var translated))
        {
            // Table列IDはラベルから生成される場合があるため、###original で元IDを維持して表示だけ差し替える。
            var display = MakeDisplayOnlyInteractiveLabel(label, translated);
            var bytes = Encoding.UTF8.GetBytes(display + "\0");
            fixed (byte* p = bytes)
            {
                tableSetupColumnHook!.Original(p, flags, initWidthOrWeight, userId);
                Interlocked.Increment(ref translatedCount);
                return;
            }
        }

        tableSetupColumnHook!.Original(label, flags, initWidthOrWeight, userId);
    }

    private void EndComboDetour()
    {
        try { endComboHook!.Original(); }
        finally
        {
            if (!drawingOwnUi && comboOpenDepth > 0) comboOpenDepth--;
        }
    }

    private byte RadioButtonBoolDetour(byte* label, byte active)
    {
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled)) CapturePointer(label, null, "RadioButtonBool"); } catch { }
        if (!drawingOwnUi && TryTranslatePointer(label, null, true, out var translated))
        {
            Interlocked.Increment(ref translatedCount);
            var display = MakeDisplayOnlyInteractiveLabel(label, translated);
            var bytes = Encoding.UTF8.GetBytes(display + "\0");
            fixed (byte* p = bytes) return radioButtonBoolHook!.Original(p, active);
        }
        return radioButtonBoolHook!.Original(label, active);
    }

    private byte RadioButtonIntPtrDetour(byte* label, int* v, int vButton)
    {
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled)) CapturePointer(label, null, "RadioButtonIntPtr"); } catch { }
        if (!drawingOwnUi && TryTranslatePointer(label, null, true, out var translated))
        {
            Interlocked.Increment(ref translatedCount);
            var display = MakeDisplayOnlyInteractiveLabel(label, translated);
            var bytes = Encoding.UTF8.GetBytes(display + "\0");
            fixed (byte* p = bytes) return radioButtonIntPtrHook!.Original(p, v, vButton);
        }
        return radioButtonIntPtrHook!.Original(label, v, vButton);
    }

    private byte TreeNodeStrDetour(byte* label)
    {
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled)) CapturePointer(label, null, "TreeNode"); } catch { }
        if (!drawingOwnUi && TryTranslatePointer(label, null, true, out var translated))
        {
            Interlocked.Increment(ref translatedCount);
            var display = MakeDisplayOnlyInteractiveLabel(label, translated);
            var bytes = Encoding.UTF8.GetBytes(display + "\0");
            fixed (byte* p = bytes) return treeNodeStrHook!.Original(p);
        }
        return treeNodeStrHook!.Original(label);
    }

    private byte TreeNodeExStrDetour(byte* label, int flags)
    {
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled)) CapturePointer(label, null, "TreeNodeEx"); } catch { }
        if (!drawingOwnUi && TryTranslatePointer(label, null, true, out var translated))
        {
            Interlocked.Increment(ref translatedCount);
            var display = MakeDisplayOnlyInteractiveLabel(label, translated);
            var bytes = Encoding.UTF8.GetBytes(display + "\0");
            fixed (byte* p = bytes) return treeNodeExStrHook!.Original(p, flags);
        }
        return treeNodeExStrHook!.Original(label, flags);
    }


    private byte CollapsingHeaderTreeNodeFlagsDetour(byte* label, int flags)
    {
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled)) CapturePointer(label, null, "CollapsingHeader"); } catch { }

        if (!drawingOwnUi && TryTranslatePointer(label, null, true, out var translated))
        {
            Interlocked.Increment(ref translatedCount);
            var display = MakeDisplayOnlyInteractiveLabel(label, translated);
            var bytes = Encoding.UTF8.GetBytes(display + "\0");
            fixed (byte* p = bytes)
                return collapsingHeaderTreeNodeFlagsHook!.Original(p, flags);
        }

        return collapsingHeaderTreeNodeFlagsHook!.Original(label, flags);
    }

    private byte CollapsingHeaderBoolPtrDetour(byte* label, byte* pVisible, int flags)
    {
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled)) CapturePointer(label, null, "CollapsingHeaderPtr"); } catch { }

        if (!drawingOwnUi && TryTranslatePointer(label, null, true, out var translated))
        {
            Interlocked.Increment(ref translatedCount);
            var display = MakeDisplayOnlyInteractiveLabel(label, translated);
            var bytes = Encoding.UTF8.GetBytes(display + "\0");
            fixed (byte* p = bytes)
                return collapsingHeaderBoolPtrHook!.Original(p, pVisible, flags);
        }

        return collapsingHeaderBoolPtrHook!.Original(label, pVisible, flags);
    }

    private void BulletTextDetour(byte* text)
    {
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled)) CapturePointer(text, null, "Tooltip"); } catch { }
        if (!drawingOwnUi && TryTranslatePointer(text, null, false, out var translated))
        {
            Interlocked.Increment(ref translatedCount);
            // igBulletText は printf形式なので、日本語訳中の % はリテラルとしてエスケープする。
            var safe = translated.Replace("%", "%%", StringComparison.Ordinal);
            var bytes = Encoding.UTF8.GetBytes(safe + "\0");
            fixed (byte* p = bytes) { bulletTextHook!.Original(p); return; }
        }
        bulletTextHook!.Original(text);
    }

    // detour はネイティブ cimgui から呼ばれる。例外がここを抜けると
    // マネージド→ネイティブ境界を巻き戻すことになり、ゲームごと落ちる。
    // 翻訳に失敗したときは「原文のまま描画する」だけの劣化に留める。
    // 失敗の記録は毎フレーム出ないよう間隔を空ける。
    private long lastDetourFailureLogTick;
    private const long DetourFailureLogIntervalMs = 5_000;

    private void LogDetourFailure(string where, Exception ex)
    {
        try
        {
            var now = Environment.TickCount64;
            if (lastDetourFailureLogTick != 0 && now - lastDetourFailureLogTick < DetourFailureLogIntervalMs) return;
            lastDetourFailureLogTick = now;
            log.Error(ex, "[PluginJPHelper] {Where} で例外。原文表示にフォールバックします。", where);
        }
        catch { }
    }

    private bool TryGetTranslationForPlugin(string pluginName, string source, out string translated)
    {
        translated = string.Empty;
        try
        {
            if (!config.Plugins.TryGetValue(pluginName, out var state) || !state.Enabled) return false;
            if (state.DeletedKeys.Contains(source)) return false;

            if (state.UserOverrides.TryGetValue(source, out var userTranslated) && !string.IsNullOrWhiteSpace(userTranslated))
                return TryAcceptPluginTranslation(pluginName, source, userTranslated, "ユーザー訳", state, out translated);

            if (state.OfficialOverrides.TryGetValue(source, out var officialTranslated) && !string.IsNullOrWhiteSpace(officialTranslated))
                return TryAcceptPluginTranslation(pluginName, source, officialTranslated, "公式上書き", state, out translated);

            if (GetActiveStandardDictionary(pluginName).TryGetValue(source, out var standardTranslated) && !string.IsNullOrWhiteSpace(standardTranslated))
            {
                var dictionarySource = string.IsNullOrWhiteSpace(state.LastCsvPath) ? "標準辞書" : state.LastCsvPath;
                return TryAcceptPluginTranslation(pluginName, source, standardTranslated, dictionarySource, state, out translated);
            }

            return false;
        }
        catch (Exception ex)
        {
            translated = string.Empty;
            LogDetourFailure(nameof(TryGetTranslationForPlugin), ex);
            return false;
        }
    }

    private bool TryAcceptPluginTranslation(string pluginName, string source, string candidate, string dictionarySource, PluginDictionaryState state, out string translated)
    {
        translated = string.Empty;
        if (!MojibakeSafety.TryDetectTranslatedText(candidate, out var reason))
        {
            translated = candidate;
            return true;
        }

        var incident = new MojibakeIncident(
            MojibakeTargetKind.Plugin,
            pluginName,
            "日本語訳",
            source,
            candidate,
            reason,
            dictionarySource);
        if (IsMojibakeAllowedForSession(incident))
        {
            translated = candidate;
            return true;
        }

        // そのフレームから対象プラグインの翻訳だけ停止する。設定保存/UI表示は次のUI Tickで行う。
        state.Enabled = false;
        QueueMojibakeIncident(incident);
        return false;
    }

    // v0.4.9:
    // 個別OFFは「そのプラグイン自身の画面を原文表示にする」設定として扱う。
    // 辞書データは削除せず、別のONプラグイン画面で同じ原文が使われた場合の
    // 共通完全一致辞書として引き続き参照できるようにする。
    private bool TryGetSharedTranslationForPlugin(string pluginName, string source, out string translated)
    {
        translated = string.Empty;
        try
        {
            if (!config.Plugins.TryGetValue(pluginName, out var state) || !state.TranslationTarget) return false;
            if (state.DeletedKeys.Contains(source)) return false;

            string candidate;
            string dictionarySource;
            if (state.UserOverrides.TryGetValue(source, out candidate!) && !string.IsNullOrWhiteSpace(candidate))
                dictionarySource = "ユーザー訳";
            else if (state.OfficialOverrides.TryGetValue(source, out candidate!) && !string.IsNullOrWhiteSpace(candidate))
                dictionarySource = "公式上書き";
            else if (GetActiveStandardDictionary(pluginName).TryGetValue(source, out candidate!) && !string.IsNullOrWhiteSpace(candidate))
                dictionarySource = string.IsNullOrWhiteSpace(state.LastCsvPath) ? "標準辞書" : state.LastCsvPath;
            else
                return false;

            if (!MojibakeSafety.TryDetectTranslatedText(candidate, out var reason))
            {
                translated = candidate;
                return true;
            }

            var incident = new MojibakeIncident(
                MojibakeTargetKind.Plugin, pluginName, "共有辞書", source, candidate, reason, dictionarySource);
            if (IsMojibakeAllowedForSession(incident))
            {
                translated = candidate;
                return true;
            }
            QueueMojibakeIncident(incident);
            return false;
        }
        catch (Exception ex)
        {
            translated = string.Empty;
            LogDetourFailure(nameof(TryGetSharedTranslationForPlugin), ex);
            return false;
        }
    }

    // v0.4.9:
    // Tooltip/Combo/Popupなどでは CurrentWindowOwner が一時的に空になる場合があるため、
    // 直前の明示的な親ウィンドウ所有者まで含めて「実効所有者」を求める。
    // これにより日本語化OFF直後でも、Tooltipや別描画経路だけ翻訳が残るのを防ぐ。
    private KeyValuePair<string, PluginDictionaryState>[] GetPluginConfigSnapshot()
    {
        lock (pluginConfigSync)
            return config.Plugins.ToArray();
    }

    private string GetEffectiveCurrentWindowOwner()
    {
        var owner = CurrentWindowOwner;
        if (!string.IsNullOrWhiteSpace(owner))
            return owner;

        owner = ResolveWindowOwner(CurrentWindowName);
        if (!string.IsNullOrWhiteSpace(owner))
            return owner;

        if (IsTransientImGuiWindow(CurrentWindowName)
            && !string.IsNullOrWhiteSpace(lastExplicitWindowOwner)
            && Environment.TickCount64 - lastExplicitWindowOwnerTick <= 1000)
        {
            return lastExplicitWindowOwner;
        }

        return string.Empty;
    }

    // 所有者を確定できた画面だけを対象にする。
    // 所有者不明の画面をOFF扱いにして他プラグインを巻き込まない。
    // Plugin Installer はプラグイン本体とは別系統の専用翻訳で処理する。
    // 通常プラグイン辞書（公式/ユーザー/コミュニティ）を Installer へ流用しない。
    // Dalamud 側の固定 ImGui ID `###XlPluginInstaller` を使って判定するため、
    // 表示言語やウィンドウタイトル文字列には依存しない。
    private static bool IsDalamudPluginInstallerWindow(string? windowName)
        => !string.IsNullOrWhiteSpace(windowName)
           && windowName.Contains("###XlPluginInstaller", StringComparison.Ordinal);

    private static bool IsInsideDalamudPluginInstaller()
    {
        if (windowStack == null || windowStack.Count == 0) return false;
        foreach (var windowName in windowStack)
            if (IsDalamudPluginInstallerWindow(windowName)) return true;
        return false;
    }

    private bool IsCurrentWindowTranslationExplicitlyDisabled()
    {
        // Plugin Installer 内では通常プラグイン辞書を完全に遮断する。
        // PluginInstallerModule が Manifest/更新履歴へ直接適用する専用翻訳は影響を受けない。
        if (IsInsideDalamudPluginInstaller()) return true;

        var owner = GetEffectiveCurrentWindowOwner();

        // v0.4.9:
        // owner が空でもここで return しない。
        // CurrentWindowName から対象プラグインを判定できるため、
        // Pawprint###BeastmasterMain のような画面は OFF を必ず優先する。
        foreach (var (pluginName, state) in GetPluginConfigSnapshot())
        {
            if (!state.TranslationTarget) continue;

            if (!string.IsNullOrWhiteSpace(owner) && IsSamePluginIdentity(owner, pluginName))
                return !state.Enabled;

            if (!state.Enabled && IsTargetWindow(pluginName, CurrentWindowName))
                return true;
        }

        return false;
    }

    private static bool AllowsPartialTranslation(string pluginName)
        => string.Equals(pluginName, "DalamudACT", StringComparison.OrdinalIgnoreCase);


    // v0.0.66: RenderText系は「現在のウィンドウがどの対象プラグインか」を確定してから、
    // そのプラグインの辞書だけを参照する。設定値・ImGui ID・入力状態には触れない。
    private bool TryTranslateRenderPointerForCurrentWindow(byte* begin, byte* end, out string translated, out string pluginName)
    {
        try
        {
            return TryTranslateRenderPointerForCurrentWindowCore(begin, end, out translated, out pluginName);
        }
        catch (Exception ex)
        {
            translated = string.Empty;
            pluginName = string.Empty;
            LogDetourFailure(nameof(TryTranslateRenderPointerForCurrentWindow), ex);
            return false;
        }
    }

    private bool TryTranslateRenderPointerForCurrentWindowCore(byte* begin, byte* end, out string translated, out string pluginName)
    {
        translated = string.Empty;
        pluginName = string.Empty;
        if (begin == null) return false;
        if (IsCurrentWindowTranslationExplicitlyDisabled()) return false;

        var currentWindow = CurrentWindowName;
        if (string.IsNullOrWhiteSpace(currentWindow)) return false;

        var effectiveOwner = GetEffectiveCurrentWindowOwner();
        foreach (var (name, state) in GetPluginConfigSnapshot())
        {
            if (!state.Enabled || !(IsSamePluginIdentity(effectiveOwner, name) || IsTargetWindow(name, currentWindow))) continue;
            pluginName = name;
            break;
        }
        if (string.IsNullOrEmpty(pluginName)) return false;

        string? source;
        if (end != null && end >= begin)
        {
            var len = end - begin;
            if (len <= 0 || len > 4096) return false;
            source = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(begin, (int)len));
        }
        else source = Marshal.PtrToStringUTF8((nint)begin);

        if (string.IsNullOrEmpty(source)) return false;

        // まず従来どおり完全一致。
        if (TryGetTranslationForPlugin(pluginName, source, out translated)) return true;
        // 文字化け検知でこの呼び出し中にOFFになった場合、別経路の翻訳へフォールバックしない。
        if (config.Plugins.TryGetValue(pluginName, out var safetyStateAfterExact) && !safetyStateAfterExact.Enabled) return false;

        // v0.4.9: 表示文字列###内部ID / ##内部ID の形で渡される特殊描画にも対応。
        // 辞書は表示部分の完全一致だけを使い、内部IDは原文のまま保持する。
        var safeIdMarker = source.IndexOf("###", StringComparison.Ordinal);
        if (safeIdMarker < 0) safeIdMarker = source.IndexOf("##", StringComparison.Ordinal);
        if (safeIdMarker > 0)
        {
            var visibleSource = source[..safeIdMarker];
            if (TryGetTranslationForPlugin(pluginName, visibleSource, out var visibleTranslated) &&
                !string.IsNullOrWhiteSpace(visibleTranslated))
            {
                translated = visibleTranslated + source[safeIdMarker..];
                return true;
            }
            if (config.Plugins.TryGetValue(pluginName, out var safetyStateAfterVisible) && !safetyStateAfterVisible.Enabled) return false;
        }

        // v0.4.9: 末尾が動的に変化する固定接頭辞を翻訳する。
        // 完全一致辞書を最優先し、該当しない場合だけ接頭辞部分を差し替える。
        if (TryTranslateDynamicPrefix(source, false, out translated)) return true;
        if (InventoryToolsBehavior.TryTranslateDynamic(pluginName, source, out translated)) return true;
        if (ArtisanBehavior.TryTranslateDynamic(pluginName, source, false, out translated)) return true;

        // v0.0.66: 部分一致はDalamudACTの動的ラベルだけに限定する。
        // ICEなど通常のプラグインは完全一致だけで処理し、辞書全件走査を行わない。
        if (!AllowsPartialTranslation(pluginName)) return false;

        // v0.0.66: 動的な表示文字列の中に辞書原文が含まれる場合だけ、
        // 「表示部分」に限定して部分置換する。
        // 例: "01/21 白魔法师" -> "01/21 白魔道士"
        // ImGui の ## / ### 以降は Widget ID / 内部ID なので絶対に変更しない。
        var idMarker = source.IndexOf("##", StringComparison.Ordinal);
        var visible = idMarker >= 0 ? source[..idMarker] : source;
        var idSuffix = idMarker >= 0 ? source[idMarker..] : string.Empty;
        if (string.IsNullOrEmpty(visible)) return false;

        if (!config.Plugins.TryGetValue(pluginName, out var pluginState)) return false;

        var candidates = GetDictionaryCatalog(pluginName)
            .Select(kv => (Source: kv.Key, Ja: pluginState.UserOverrides.TryGetValue(kv.Key, out var userJa) && !string.IsNullOrWhiteSpace(userJa) ? userJa : (pluginState.OfficialOverrides.TryGetValue(kv.Key, out var officialJa) && !string.IsNullOrWhiteSpace(officialJa) ? officialJa : kv.Value)))
            .Where(x => !string.IsNullOrWhiteSpace(x.Source) && !string.IsNullOrWhiteSpace(x.Ja))
            .Where(x => !x.Source.Contains("##", StringComparison.Ordinal) && !x.Ja.Contains("##", StringComparison.Ordinal))
            .Where(x => visible.Contains(x.Source, StringComparison.Ordinal))
            .OrderByDescending(x => x.Source.Length)
            .ThenBy(x => x.Source, StringComparer.Ordinal)
            .ToArray();

        if (candidates.Length == 0) return false;
        foreach (var x in candidates)
        {
            if (!MojibakeSafety.TryDetectTranslatedText(x.Ja, out var reason)) continue;
            var incident = new MojibakeIncident(
                MojibakeTargetKind.Plugin, pluginName, "部分一致辞書", x.Source, x.Ja, reason,
                string.IsNullOrWhiteSpace(pluginState.LastCsvPath) ? "標準辞書" : pluginState.LastCsvPath);
            if (IsMojibakeAllowedForSession(incident)) continue;
            pluginState.Enabled = false;
            QueueMojibakeIncident(incident);
            return false;
        }
        var replaced = visible;
        var changed = false;
        foreach (var x in candidates)
        {
            var next = replaced.Replace(x.Source, x.Ja, StringComparison.Ordinal);
            if (!string.Equals(next, replaced, StringComparison.Ordinal)) changed = true;
            replaced = next;
        }
        if (!changed) return false;

        translated = replaced + idSuffix;
        return true;
    }

    private void DrawListAddTextVec2Detour(void* drawList, Vector2 pos, uint col, byte* textBegin, byte* textEnd)
    {
        // v0.4.9: ImDrawList.AddText() を直接使うプラグインも、
        // 現在のImGuiウィンドウ所有者を確定できる場合は通常のRenderText系と同じ辞書で翻訳する。
        // 特定プラグイン名には依存せず、対象ウィンドウ内だけを処理する。
        if (drawingOwnUi || textBegin == null)
        {
            drawListAddTextVec2Hook!.Original(drawList, pos, col, textBegin, textEnd);
            return;
        }

        try
        {
            // 取得中は従来どおりDrawList経路も記録する。CapturePointer側で対象ウィンドウを判定する。
            if (captureEnabled || baselineCaptureEnabled)
                CapturePointer(textBegin, textEnd, "DrawListAddText");

            if (TryTranslateRenderPointerForCurrentWindow(textBegin, textEnd, out var translated, out _) &&
                !string.IsNullOrWhiteSpace(translated))
            {
                var bytes = Encoding.UTF8.GetBytes(translated + "\0");
                fixed (byte* p = bytes)
                {
                    drawListAddTextVec2Hook!.Original(drawList, pos, col, p, null);
                    Interlocked.Increment(ref translatedCount);
                    return;
                }
            }
        }
        catch
        {
            // 失敗時は元の描画をそのまま通す。
        }

        drawListAddTextVec2Hook!.Original(drawList, pos, col, textBegin, textEnd);
    }


    private void DrawListAddTextFontPtrDetour(
        void* drawList, void* font, float fontSize, Vector2 pos, uint col,
        byte* textBegin, byte* textEnd, float wrapWidth, Vector4* cpuFineClipRect)
    {
        if (drawingOwnUi || textBegin == null)
        {
            drawListAddTextFontPtrHook!.Original(drawList, font, fontSize, pos, col, textBegin, textEnd, wrapWidth, cpuFineClipRect);
            return;
        }

        try
        {
            if (captureEnabled || baselineCaptureEnabled)
                CapturePointer(textBegin, textEnd, "DrawListAddTextFont");

            if (TryTranslateRenderPointerForCurrentWindow(textBegin, textEnd, out var translated, out _))
            {
                // v0.4.11 safety: FontPtr経路では呼び出し側が日本語グリフを持たない
                // 専用フォントを指定している場合がある。ASCII原文を非ASCIIへ置き換える必要が
                // ある場合は、文字化けを避けるため元表示を優先する。
                var source = textEnd != null && textEnd >= textBegin
                    ? Encoding.UTF8.GetString(new ReadOnlySpan<byte>(textBegin, (int)(textEnd - textBegin)))
                    : Marshal.PtrToStringUTF8((nint)textBegin) ?? string.Empty;
                var sourceIsAscii = source.All(c => c <= 0x7F);
                var translatedNeedsNonAscii = translated.Any(c => c > 0x7F);
                if (sourceIsAscii && translatedNeedsNonAscii)
                {
                    drawListAddTextFontPtrHook!.Original(drawList, font, fontSize, pos, col, textBegin, textEnd, wrapWidth, cpuFineClipRect);
                    return;
                }

                var bytes = Encoding.UTF8.GetBytes(translated + "\0");
                fixed (byte* p = bytes)
                {
                    drawListAddTextFontPtrHook!.Original(drawList, font, fontSize, pos, col, p, null, wrapWidth, cpuFineClipRect);
                    Interlocked.Increment(ref translatedCount);
                    return;
                }
            }
        }
        catch
        {
            // 失敗時は元の描画をそのまま通す。
        }

        drawListAddTextFontPtrHook!.Original(drawList, font, fontSize, pos, col, textBegin, textEnd, wrapWidth, cpuFineClipRect);
    }

    private void RenderTextDetour(Vector2 pos, byte* text, byte* textEnd, byte hideTextAfterHash)
    {
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled)) CapturePointer(text, textEnd, "RenderText"); } catch { }

        if (!drawingOwnUi && TryTranslateRenderPointerForCurrentWindow(text, textEnd, out var translated, out var pluginName))
        {
            Interlocked.Increment(ref translatedCount);
            var bytes = Encoding.UTF8.GetBytes(translated);
            fixed (byte* p = bytes)
            {
                renderTextHook!.Original(pos, p, p + bytes.Length, hideTextAfterHash);
                return;
            }
        }
        renderTextHook!.Original(pos, text, textEnd, hideTextAfterHash);
    }

    private void RenderTextWrappedDetour(Vector2 pos, byte* text, byte* textEnd, float wrapWidth)
    {
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled)) CapturePointer(text, textEnd, "RenderTextWrapped"); } catch { }

        if (!drawingOwnUi && TryTranslateRenderPointerForCurrentWindow(text, textEnd, out var translated, out var pluginName))
        {
            Interlocked.Increment(ref translatedCount);
            var bytes = Encoding.UTF8.GetBytes(translated);
            fixed (byte* p = bytes)
            {
                renderTextWrappedHook!.Original(pos, p, p + bytes.Length, wrapWidth);
                return;
            }
        }
        renderTextWrappedHook!.Original(pos, text, textEnd, wrapWidth);
    }

    private void RenderTextClippedDetour(
        Vector2 posMin, Vector2 posMax, byte* text, byte* textEnd,
        Vector2* textSizeIfKnown, Vector2 align, NativeImRect* clipRect)
    {
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled)) CapturePointer(text, textEnd, "RenderTextClipped"); } catch { }

        if (!drawingOwnUi && TryTranslateRenderPointerForCurrentWindow(text, textEnd, out var translated, out var pluginName))
        {
            Interlocked.Increment(ref translatedCount);
            var bytes = Encoding.UTF8.GetBytes(translated);
            fixed (byte* p = bytes)
            {
                // 翻訳後は元の英語文字列用サイズキャッシュを使わず再計算させる。
                renderTextClippedHook!.Original(posMin, posMax, p, p + bytes.Length, null, align, clipRect);
                return;
            }
        }

        renderTextClippedHook!.Original(posMin, posMax, text, textEnd, textSizeIfKnown, align, clipRect);
    }

    private bool IsCurrentWindowForAnyEnabledPlugin()
    {
        if (windowStack == null || windowStack.Count == 0) return false;
        foreach (var (pluginName, state) in GetPluginConfigSnapshot())
        {
            if (!state.Enabled) continue;
            foreach (var windowName in windowStack)
                if (IsTargetWindow(pluginName, windowName)) return true;
        }
        return false;
    }

    private byte BeginTabItemDetour(byte* label, byte* pOpen, int flags)
    {
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled)) CapturePointer(label, null, "TabItem"); } catch { }
        if (!drawingOwnUi && TryTranslatePointer(label, null, true, out var translated))
        {
            Interlocked.Increment(ref translatedCount);
            var display = MakeDisplayOnlyInteractiveLabel(label, translated);
            var bytes = Encoding.UTF8.GetBytes(display + "\0");
            fixed (byte* p = bytes) return beginTabItemHook!.Original(p, pOpen, flags);
        }
        return beginTabItemHook!.Original(label, pOpen, flags);
    }

    // v0.0.69: Allagan Tools の設定画面はウィンドウ名が汎用的な "Configuration" のため、
    // ウィンドウ名だけでは所有プラグインを安全に判定できない。
    // メニューバー固有の "Wizard" を同一フレームで確認した時だけ InventoryTools と確定する。
    // 辞書全走査や部分一致は行わず、文字列完全一致1回だけの軽量判定。
    private void DetectInventoryToolsConfigurationOwner(byte* label)
    {
        // BeginMenuDetour から igBeginMenu の前に呼ばれる。ここで例外を出すと
        // igBeginMenu が呼ばれないまま抜け、呼び出し側の EndMenu と対応しなくなる。
        try
        {
            DetectInventoryToolsConfigurationOwnerCore(label);
        }
        catch (Exception ex) { LogDetourFailure(nameof(DetectInventoryToolsConfigurationOwner), ex); }
    }

    private void DetectInventoryToolsConfigurationOwnerCore(byte* label)
    {
        if (drawingOwnUi || label == null) return;
        if (!InventoryToolsBehavior.IsConfigurationWindow(CurrentWindowName)) return;
        if (!config.Plugins.TryGetValue(InventoryToolsBehavior.PluginName, out var state) || !state.Enabled) return;

        string? source;
        try { source = Marshal.PtrToStringUTF8((nint)label); }
        catch { return; }
        if (!InventoryToolsBehavior.IsConfigurationOwnerMenu(source)) return;

        if (windowOwnerStack is not { Count: > 0 }) return;
        windowOwnerStack.Pop();
        windowOwnerStack.Push(InventoryToolsBehavior.PluginName);
        lastExplicitWindowOwner = InventoryToolsBehavior.PluginName;
        lastExplicitWindowOwnerTick = Environment.TickCount64;
    }

    private byte BeginMenuDetour(byte* label, byte enabled)
    {
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled)) CapturePointer(label, null, "BeginMenu"); } catch { }
        DetectInventoryToolsConfigurationOwner(label);

        if (!drawingOwnUi && TryTranslatePointer(label, null, true, out var translated))
        {
            Interlocked.Increment(ref translatedCount);
            var display = MakeDisplayOnlyInteractiveLabel(label, translated);
            var bytes = Encoding.UTF8.GetBytes(display + "\0");
            fixed (byte* p = bytes) return beginMenuHook!.Original(p, enabled);
        }
        return beginMenuHook!.Original(label, enabled);
    }

    private byte MenuItemBoolDetour(byte* label, byte* shortcut, byte selected, byte enabled)
    {
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled)) CapturePointer(label, null, "MenuItem"); } catch { }
        if (!drawingOwnUi && TryTranslatePointer(label, null, true, out var translated))
        {
            Interlocked.Increment(ref translatedCount);
            var display = MakeDisplayOnlyInteractiveLabel(label, translated);
            var bytes = Encoding.UTF8.GetBytes(display + "\0");
            fixed (byte* p = bytes) return menuItemBoolHook!.Original(p, shortcut, selected, enabled);
        }
        return menuItemBoolHook!.Original(label, shortcut, selected, enabled);
    }

    private byte MenuItemBoolPtrDetour(byte* label, byte* shortcut, byte* pSelected, byte enabled)
    {
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled)) CapturePointer(label, null, "MenuItemPtr"); } catch { }
        if (!drawingOwnUi && TryTranslatePointer(label, null, true, out var translated))
        {
            Interlocked.Increment(ref translatedCount);
            var display = MakeDisplayOnlyInteractiveLabel(label, translated);
            var bytes = Encoding.UTF8.GetBytes(display + "\0");
            fixed (byte* p = bytes) return menuItemBoolPtrHook!.Original(p, shortcut, pSelected, enabled);
        }
        return menuItemBoolPtrHook!.Original(label, shortcut, pSelected, enabled);
    }

    private void SeparatorTextDetour(byte* label)
    {
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled)) CapturePointer(label, null, "SeparatorText"); } catch { }
        if (!drawingOwnUi && TryTranslatePointer(label, null, false, out var translated))
        {
            Interlocked.Increment(ref translatedCount);
            var bytes = Encoding.UTF8.GetBytes(translated + "\0");
            fixed (byte* ptr = bytes) separatorTextHook!.Original(ptr);
            return;
        }
        separatorTextHook!.Original(label);
    }

    // v0.0.18: ImGuiウィンドウ追跡。
    // igBegin/igEnd をフックして、現在どのウィンドウから描画された文字列かを判定する。
    private byte BeginDetour(byte* name, byte* pOpen, int flags)
    {
        string windowName = string.Empty;
        try
        {
            if (name != null)
                windowName = Marshal.PtrToStringUTF8((nint)name) ?? string.Empty;
        }
        catch { }

        windowStack ??= new Stack<string>();
        windowOwnerStack ??= new Stack<string>();

        // 所有者判定とタイトル翻訳は失敗しても描画を止めない。
        // ここで例外を外へ出すと igBegin が呼ばれないまま抜けるが、呼び出し側は必ず
        // End() を呼ぶため、ImGui のウィンドウスタックと PJH 側スタックの両方が破綻する。
        var owner = string.Empty;
        string? translatedWindowName = null;
        try
        {
            // 所有者判定は必ず「原文のウィンドウ名」で行う。
            // Dalamud Plugin Installer は専用翻訳系統のため、通常プラグイン所有者を割り当てない。
            var isDalamudPluginInstaller = IsDalamudPluginInstallerWindow(windowName);
            owner = isDalamudPluginInstaller ? string.Empty : ResolveWindowOwner(windowName);

            // Child Window は親の所有者を継承。
            if (string.IsNullOrWhiteSpace(owner) && windowOwnerStack.Count > 0)
                owner = windowOwnerStack.Peek();

            if (IsTransientImGuiWindow(windowName))
            {
                if (string.IsNullOrWhiteSpace(owner) &&
                    !string.IsNullOrWhiteSpace(lastExplicitWindowOwner) &&
                    Environment.TickCount64 - lastExplicitWindowOwnerTick <= 150)
                {
                    owner = lastExplicitWindowOwner;
                }
            }
            else if (!string.IsNullOrWhiteSpace(owner))
            {
                lastExplicitWindowOwner = owner;
                lastExplicitWindowOwnerTick = Environment.TickCount64;

                // v0.4.16: 実際に対象プラグインのウィンドウが表示された時だけ、
                // そのプラグインの現在有効な辞書を限定確認する。
                // 起動時の全辞書走査は行わない。通常の翻訳フックで拾えない描画経路の保険。
                TryTargetedMojibakeScanForVisiblePlugin(owner);
            }

            // v0.4.9:
            // igBegin の name は「ウィンドウタイトル」そのもの。
            // これまでは Original() を先に呼んでいたため、内容が翻訳できても
            // `节点设置###ArenaNodeEditor` のタイトルだけ中国語のまま残っていた。
            // 関連付け済みの所有者が分かる場合は、辞書でタイトルも翻訳してから Begin() へ渡す。
            if (!drawingOwnUi
                && !isDalamudPluginInstaller
                && !string.IsNullOrWhiteSpace(owner)
                && !string.IsNullOrWhiteSpace(windowName)
                && TryGetTranslationForPlugin(owner, windowName, out var translated)
                && !string.IsNullOrWhiteSpace(translated)
                && !string.Equals(windowName, translated, StringComparison.Ordinal))
            {
                translatedWindowName = translated;
            }
        }
        catch (Exception ex)
        {
            owner = string.Empty;
            translatedWindowName = null;
            LogDetourFailure(nameof(BeginDetour), ex);
        }

        byte result;
        if (translatedWindowName != null)
        {
            var bytes = Encoding.UTF8.GetBytes(translatedWindowName + "\0");
            fixed (byte* translatedName = bytes)
                result = beginHook!.Original(translatedName, pOpen, flags);
        }
        else
        {
            result = beginHook!.Original(name, pOpen, flags);
        }

        windowStack.Push(windowName);
        windowOwnerStack.Push(owner);

        if (!drawingOwnUi && !string.IsNullOrWhiteSpace(windowName))
        {
            seenWindows.AddOrUpdate(windowName, 1, (_, count) => count + 1);
            liveWindowLastSeen[windowName] = Environment.TickCount64;
        }

        return result;
    }

    // v0.4.9: Popup/Modal ownership inheritance.
    // cimgui's igBeginPopup* enters an ImGui popup internally; that internal Begin does not
    // necessarily pass through our igBegin hook. Inherit the parent window owner explicitly
    // and keep it on the PJH owner stack until igEndPopup.
    private byte BeginPopupDetour(byte* strId, int flags)
    {
        var inheritedOwner = CurrentWindowOwner;
        var result = beginPopupHook!.Original(strId, flags);
        PushPopupOwnerFrame(result != 0, strId, inheritedOwner);
        return result;
    }

    private byte BeginPopupModalDetour(byte* name, byte* pOpen, int flags)
    {
        var inheritedOwner = CurrentWindowOwner;
        var result = beginPopupModalHook!.Original(name, pOpen, flags);
        PushPopupOwnerFrame(result != 0, name, inheritedOwner);
        return result;
    }

    private void PushPopupOwnerFrame(bool opened, byte* namePtr, string inheritedOwner)
    {
        // ImGui は BeginPopup* が false を返したとき EndPopup() を呼ばない。
        // 開かなかった分まで push すると対応する pop が来ないため、
        // 閉じている popup を描画するたびに 1 要素ずつ積み上がり続ける。
        //
        // さらに、開いている popup の内側で別の BeginPopup が false を返すと、
        // 外側の EndPopup がその false を pop してしまい、windowStack /
        // windowOwnerStack が戻らないまま所有者判定がずれる。
        //
        // 開いたときだけ積む。これで EndPopup と 1 対 1 に対応する。
        if (!opened) return;

        popupOwnerFrameStack ??= new Stack<bool>();
        popupOwnerFrameStack.Push(true);

        string popupName = string.Empty;
        try
        {
            if (namePtr != null) popupName = Marshal.PtrToStringUTF8((nint)namePtr) ?? string.Empty;
        }
        catch { }

        // 所有者解決が失敗しても popup 自体の描画は止めない。
        // ここで例外を出すと下の Push が行われず、EndPopup 側でスタックがずれる。
        var owner = string.Empty;
        try
        {
            owner = !string.IsNullOrWhiteSpace(inheritedOwner)
                ? inheritedOwner
                : ResolveWindowOwner(popupName);
        }
        catch (Exception ex) { LogDetourFailure(nameof(PushPopupOwnerFrame), ex); }

        // If parent ownership is known, that always wins for the popup. This is the key
        // difference from guessing ownership from translated/Chinese popup titles.
        windowStack ??= new Stack<string>();
        windowOwnerStack ??= new Stack<string>();
        windowStack.Push(popupName);
        windowOwnerStack.Push(owner);
    }

    private void EndPopupDetour()
    {
        try
        {
            endPopupHook!.Original();
        }
        finally
        {
            if (popupOwnerFrameStack is { Count: > 0 })
            {
                var opened = popupOwnerFrameStack.Pop();
                if (opened)
                {
                    if (windowStack is { Count: > 0 }) windowStack.Pop();
                    if (windowOwnerStack is { Count: > 0 }) windowOwnerStack.Pop();
                }
            }
        }
    }

    private void EndDetour()
    {
        try
        {
            endHook!.Original();
        }
        finally
        {
            if (windowStack is { Count: > 0 })
            {
                // RSRは1画面を複数の子ウィンドウで構成する。
                // 子ウィンドウのEndごとにメニュー状態を消すと所属情報が失われるため、
                // ここではウィンドウスタックだけ戻し、ナビゲーション状態は取得停止/クリア時まで保持する。
                windowStack.Pop();
                if (windowOwnerStack is { Count: > 0 }) windowOwnerStack.Pop();
            }
        }
    }

    private static string CurrentWindowName
        => windowStack is { Count: > 0 } ? windowStack.Peek() : string.Empty;

    private static string CurrentWindowOwner
        => windowOwnerStack is { Count: > 0 } ? windowOwnerStack.Peek() : string.Empty;

    private static bool IsTransientImGuiWindow(string? windowName)
    {
        if (string.IsNullOrWhiteSpace(windowName)) return false;
        var w = windowName.Trim();
        return w.StartsWith("##Combo_", StringComparison.Ordinal)
            || w.StartsWith("##Tooltip_", StringComparison.Ordinal)
            || w.StartsWith("##Popup_", StringComparison.Ordinal)
            || w.StartsWith("##Menu_", StringComparison.Ordinal)
            || w.StartsWith("##ContextMenu_", StringComparison.Ordinal);
    }

    private static bool IsNoisyInternalDetectedWindow(string? windowName)
    {
        if (string.IsNullOrWhiteSpace(windowName)) return false;
        var w = windowName.Trim();

        // Umbra はツールバー/補助バーの各部品を個別の ImGui Window として大量に生成する。
        // ユーザーが「別ウィンドウ」として関連付けたい対象ではないことが多いので標準では隠す。
        if (w.StartsWith("Umbra:AuxBar#", StringComparison.OrdinalIgnoreCase)
            || w.StartsWith("Umbra:Toolbar#", StringComparison.OrdinalIgnoreCase)
            || w.Contains("ui-widget-instance", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private IEnumerable<string> GetLiveDetectedWindows(long liveMs = 500)
    {
        var now = Environment.TickCount64;
        foreach (var item in liveWindowLastSeen)
        {
            if (now - item.Value <= liveMs)
                yield return item.Key;
        }
    }

    private string ResolveWindowOwner(string? windowName)
    {
        if (string.IsNullOrWhiteSpace(windowName)) return string.Empty;
        foreach (var (pluginName, state) in GetPluginConfigSnapshot())
        {
            if (!state.TranslationTarget && !state.Enabled) continue;
            if (IsTargetWindow(pluginName, windowName)) return pluginName;
        }
        return string.Empty;
    }

    private static bool IsSamePluginIdentity(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal)) return true;
        return (string.Equals(a, "ICE", StringComparison.Ordinal) && string.Equals(b, "ExplorersIcebox", StringComparison.Ordinal))
            || (string.Equals(a, "ExplorersIcebox", StringComparison.Ordinal) && string.Equals(b, "ICE", StringComparison.Ordinal));
    }

    private bool IsCurrentWindowOwnedBy(string pluginName)
        => IsSamePluginIdentity(CurrentWindowOwner, pluginName)
           || IsTargetWindow(pluginName, CurrentWindowName);

    private bool IsTargetWindow(string pluginName, string? windowName)
    {
        if (string.IsNullOrWhiteSpace(windowName)) return false;

        var customWindowKeyword = config.Plugins.TryGetValue(pluginName, out var state) ? state.WindowKeyword : null;
        return PluginProfileRegistry.MatchesWindow(pluginName, windowName.Trim(), customWindowKeyword);
    }

    private void UpdateRsrNavigationContext(byte* label, bool selected)
        => RsrNavigationTracker.Observe(label, selected, captureEnabled, capturePlugin, CurrentWindowName);

    private void UpdateRsrNavigationContextAfterClick(string raw)
        => RsrNavigationTracker.ObserveAfterClick(raw, captureEnabled, capturePlugin);

    private static string VisibleLabel(string source)
    {
        var marker = source.IndexOf("##", StringComparison.Ordinal);
        return marker > 0 ? source[..marker] : source;
    }

    private static string MakeDisplayOnlyInteractiveLabel(byte* originalLabel, string translatedVisible)
    {
        var original = originalLabel == null ? string.Empty : Marshal.PtrToStringUTF8((nint)originalLabel) ?? string.Empty;
        if (string.IsNullOrEmpty(original)) return translatedVisible;

        // ImGuiの ### は「表示文字」と「ID」を分離できる。
        // translated###original とすることで、見た目だけ翻訳しWidget IDは原文と同じに保つ。
        return translatedVisible + "###" + original;
    }

    // v0.0.69: InventoryTools の Source/Use category HelpText は毎回動的に組み立てられる。
    // 全辞書の部分一致は行わず、既知の定型文だけを O(文字列長) で処理する。
    // v0.4.9:
    // 「版本：v1.2.3」のように後半だけが動的に変わる表示を、辞書を
    // バージョンごとに増やさず固定接頭辞だけ翻訳する。
    // 実機で確認した固定接頭辞を、所有プラグインを問わず同じ規則で扱う。
    private static bool TryTranslateDynamicPrefix(string source, bool interactiveLabel, out string translated)
    {
        translated = string.Empty;
        if (string.IsNullOrEmpty(source)) return false;

        var idMarker = source.IndexOf("###", StringComparison.Ordinal);
        if (idMarker < 0) idMarker = source.IndexOf("##", StringComparison.Ordinal);
        var visible = idMarker >= 0 ? source[..idMarker] : source;
        var idSuffix = idMarker >= 0 ? source[idMarker..] : string.Empty;

        ReadOnlySpan<(string Source, string Japanese)> prefixes = new (string, string)[]
        {
            ("参考PR版本：", "参考PRバージョン："),
            ("版本：", "バージョン："),
            ("描述：", "説明："),
            ("作者：", "作者："),
        };

        foreach (var (prefix, jaPrefix) in prefixes)
        {
            if (!visible.StartsWith(prefix, StringComparison.Ordinal) || visible.Length <= prefix.Length)
                continue;

            var replaced = jaPrefix + visible[prefix.Length..];
            translated = interactiveLabel ? replaced : replaced + idSuffix;
            return !string.Equals(translated, source, StringComparison.Ordinal);
        }

        return false;
    }

    // v0.3.1: Artisan の List Editor / Processing List で毎回値が変わる表示だけを
    // 定型部分で安全に翻訳する。数値・アイテム名・ImGui ID は変更しない。
    private bool TryTranslatePointer(byte* begin, byte* end, bool preserveImGuiId, out string translated)
    {
        try
        {
            return TryTranslatePointerCore(begin, end, preserveImGuiId, out translated);
        }
        catch (Exception ex)
        {
            translated = string.Empty;
            LogDetourFailure(nameof(TryTranslatePointer), ex);
            return false;
        }
    }

    private bool TryTranslatePointerCore(byte* begin, byte* end, bool preserveImGuiId, out string translated)
    {
        translated = string.Empty;
        if (begin == null) return false;
        if (IsCurrentWindowTranslationExplicitlyDisabled()) return false;
        string? source;
        if (end != null && end >= begin)
        {
            var len = end - begin; if (len <= 0 || len > 4096) return false;
            source = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(begin, (int)len));
        }
        else source = Marshal.PtrToStringUTF8((nint)begin);
        if (string.IsNullOrEmpty(source)) return false;

        // v0.4.9:
        // まず現在の所有プラグインを優先して完全一致辞書を引く。
        // Popup/Modalのように別ウィンドウ名へ切り替わる場合でも、owner が
        // 引き継がれていれば対象プラグインの辞書を確実に参照できる。
        var ownerForExact = GetEffectiveCurrentWindowOwner();
        if (!string.IsNullOrWhiteSpace(ownerForExact) &&
            TryGetTranslationForPlugin(ownerForExact, source, out translated))
        {
            return true;
        }
        if (!string.IsNullOrWhiteSpace(ownerForExact)
            && config.Plugins.TryGetValue(ownerForExact, out var safetyOwnerState)
            && !safetyOwnerState.Enabled)
            return false;

        // 従来どおり全有効辞書の完全一致も残す。
        if (TryTranslate(source, out translated))
        {
            return true;
        }

        // v0.0.66:
        // TreeNode/Button/Checkbox/RadioButton 等の動的表示文字列にも部分翻訳を適用する。
        // 例: "01/21 白魔法师" -> "01/21 白魔道士"
        //
        // 重要:
        // - 現在のウィンドウに所属する有効プラグインの辞書だけを使う
        // - ## / ### 以降の ImGui ID は置換対象にしない
        // - 設定値、Enum、保存値、コマンドには触れず「表示ラベル」だけを差し替える
        var currentWindow = CurrentWindowName;
        if (string.IsNullOrWhiteSpace(currentWindow)) return false;

        string pluginName = string.Empty;
        var effectiveOwnerForPointer = GetEffectiveCurrentWindowOwner();
        foreach (var (name, state) in GetPluginConfigSnapshot())
        {
            if (!state.Enabled) continue;
            if (IsSamePluginIdentity(effectiveOwnerForPointer, name) || IsTargetWindow(name, currentWindow))
            {
                pluginName = name;
                break;
            }
        }
        if (string.IsNullOrEmpty(pluginName)) return false;

        // v0.4.9: バージョン番号・作者名・説明文など、
        // 後半が動的な文字列は固定接頭辞だけ翻訳する。
        if (TryTranslateDynamicPrefix(source, preserveImGuiId, out translated)) return true;
        if (InventoryToolsBehavior.TryTranslateDynamic(pluginName, source, out translated)) return true;
        if (ArtisanBehavior.TryTranslateDynamic(pluginName, source, preserveImGuiId, out translated)) return true;

        // v0.0.66: Button/Checkbox/TreeNode/Selectable等も、部分一致はDalamudACTだけ。
        // 通常プラグインは上の完全一致辞書検索だけで終了する。
        if (!AllowsPartialTranslation(pluginName)) return false;

        if (!config.Plugins.TryGetValue(pluginName, out var pluginState)) return false;

        var idMarker = source.IndexOf("##", StringComparison.Ordinal);
        var visible = idMarker >= 0 ? source[..idMarker] : source;
        if (string.IsNullOrEmpty(visible)) return false;

        var candidates = GetDictionaryCatalog(pluginName)
            .Select(kv => (
                Source: kv.Key,
                Ja: pluginState.UserOverrides.TryGetValue(kv.Key, out var userJa) && !string.IsNullOrWhiteSpace(userJa)
                    ? userJa
                    : (pluginState.OfficialOverrides.TryGetValue(kv.Key, out var officialJa) && !string.IsNullOrWhiteSpace(officialJa)
                        ? officialJa
                        : kv.Value)))
            .Where(x => !string.IsNullOrWhiteSpace(x.Source) && !string.IsNullOrWhiteSpace(x.Ja))
            .Where(x => !x.Source.Contains("##", StringComparison.Ordinal) && !x.Source.Contains("###", StringComparison.Ordinal))
            .Where(x => !x.Ja.Contains("##", StringComparison.Ordinal) && !x.Ja.Contains("###", StringComparison.Ordinal))
            .Where(x => visible.Contains(x.Source, StringComparison.Ordinal))
            .OrderByDescending(x => x.Source.Length)
            .ThenBy(x => x.Source, StringComparer.Ordinal)
            .ToArray();

        if (candidates.Length == 0) return false;

        foreach (var x in candidates)
        {
            if (!MojibakeSafety.TryDetectTranslatedText(x.Ja, out var reason)) continue;
            var incident = new MojibakeIncident(
                MojibakeTargetKind.Plugin, pluginName, "部分一致辞書", x.Source, x.Ja, reason,
                config.Plugins.TryGetValue(pluginName, out var sourceState) && !string.IsNullOrWhiteSpace(sourceState.LastCsvPath) ? sourceState.LastCsvPath : "標準辞書");
            if (IsMojibakeAllowedForSession(incident)) continue;
            if (config.Plugins.TryGetValue(pluginName, out var partialState)) partialState.Enabled = false;
            QueueMojibakeIncident(incident);
            return false;
        }

        var replaced = visible;
        foreach (var x in candidates)
            replaced = replaced.Replace(x.Source, x.Ja, StringComparison.Ordinal);

        if (string.Equals(replaced, visible, StringComparison.Ordinal)) return false;

        // interactive系は呼び出し側 MakeDisplayOnlyInteractiveLabel() が
        // 元ラベル全体を ###original として保持するため、ここでは表示部分だけ返す。
        translated = replaced;
        return true;
    }

    private bool TryTranslate(string source, out string translated)
    {
        try
        {
            return TryTranslateCore(source, out translated);
        }
        catch (Exception ex)
        {
            translated = string.Empty;
            LogDetourFailure(nameof(TryTranslate), ex);
            return false;
        }
    }

    private bool TryTranslateCore(string source, out string translated)
    {
        if (IsCurrentWindowTranslationExplicitlyDisabled())
        {
            translated = string.Empty;
            return false;
        }

        const string counterPrefix = "RSR has helped you by clicking actions ";
        const string counterSuffix = " times.";
        if (source.StartsWith(counterPrefix, StringComparison.Ordinal) && source.EndsWith(counterSuffix, StringComparison.Ordinal))
        {
            var count = source[counterPrefix.Length..^counterSuffix.Length];
            translated = $"RSRがアクションを実行した回数: {count}回";
            return true;
        }

        // v0.4.9:
        // まず「現在の画面に属する有効プラグイン」があるかを確認する。
        // Pawprint自身をOFFにした画面では、PawprintのOFF辞書を共有辞書として
        // 再利用してしまわないようにする。
        var currentOwner = GetEffectiveCurrentWindowOwner();
        var currentWindow = CurrentWindowName;

        var hasEnabledOwner = GetPluginConfigSnapshot().Any(kv =>
            kv.Value.TranslationTarget
            && kv.Value.Enabled
            && (
                (!string.IsNullOrWhiteSpace(currentOwner) && IsSamePluginIdentity(currentOwner, kv.Key))
                || IsTargetWindow(kv.Key, currentWindow)
            ));

        // 1) 有効プラグインの辞書を優先。
        foreach (var (pluginName, state) in GetPluginConfigSnapshot())
        {
            if (!state.TranslationTarget || !state.Enabled) continue;
            var wasEnabled = state.Enabled;
            if (TryGetTranslationForPlugin(pluginName, source, out translated)) return true;
            if (wasEnabled && !state.Enabled
                && ((!string.IsNullOrWhiteSpace(currentOwner) && IsSamePluginIdentity(currentOwner, pluginName))
                    || IsTargetWindow(pluginName, currentWindow)))
            {
                translated = string.Empty;
                return false;
            }
        }

        // 2) 現在の画面に「有効な所有プラグイン」が確定している場合だけ、
        //    OFFプラグインの辞書を共通完全一致辞書として利用してよい。
        //    これで A=OFF / B=ON のとき、B画面ではA辞書を共有利用できる一方、
        //    A自身のOFF画面ではA辞書が復活しない。
        if (hasEnabledOwner)
        {
            foreach (var (pluginName, state) in GetPluginConfigSnapshot())
            {
                if (!state.TranslationTarget || state.Enabled) continue;

                // 現在の画面そのものに属するOFFプラグインは絶対に共有利用しない。
                if ((!string.IsNullOrWhiteSpace(currentOwner) && IsSamePluginIdentity(currentOwner, pluginName))
                    || IsTargetWindow(pluginName, currentWindow))
                    continue;

                if (TryGetSharedTranslationForPlugin(pluginName, source, out translated)) return true;
            }
        }

        translated = string.Empty; return false;
    }

    private void CallTextOriginal(string translated) { var bytes = Encoding.UTF8.GetBytes(translated); fixed (byte* ptr = bytes) textHook!.Original(ptr, ptr + bytes.Length); }
    private byte CallCheckboxOriginal(string translated, byte* value) { var bytes = Encoding.UTF8.GetBytes(translated + "\0"); fixed (byte* ptr = bytes) return checkboxHook!.Original(ptr, value); }
    private byte CallButtonOriginal(string translated, Vector2 size) { var bytes = Encoding.UTF8.GetBytes(translated + "\0"); fixed (byte* ptr = bytes) return buttonHook!.Original(ptr, size); }

    private void CapturePointer(byte* begin, byte* end, string kind)
    {
        if (begin == null) return;
        string? text;
        if (end != null && end >= begin) { var len = end - begin; if (len <= 0 || len > 4096) return; text = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(begin, (int)len)); }
        else text = Marshal.PtrToStringUTF8((nint)begin);
        if (!LooksUseful(text)) return;

        // v0.0.14: 背景と対象プラグインを、文字列差分ではなく ImGui ウィンドウ単位で同時分離する。
        var currentWindow = CurrentWindowName;
        var isTargetWindow = captureEnabled && IsCurrentWindowOwnedBy(capturePlugin);

        // v0.0.25: RSRサイドバーは「現在メニュー判定専用」。本文辞書へは登録しない。
        // これで Actions / Auto / Basic ... が各メニュー配下に重複する現象を除外する。
        if (isTargetWindow && RsrProfile.IsSideBarWindow(capturePlugin, currentWindow))
            return;

        if (captureEnabled && isTargetWindow)
        {
            // RSR左メニューは直前に描画されるため、最後に見つけたselectedな
            // 確認済みメニュー候補を、RSR本体の文字列を取得する瞬間に確定する。
            RsrNavigationTracker.CommitPendingMenu(capturePlugin);

            var isRsr = RsrProfile.MatchesPluginName(capturePlugin);
            var menu = isRsr ? RsrNavigationTracker.CurrentMenu : string.Empty;
            var section = isRsr ? RsrNavigationTracker.CurrentSection : string.Empty;
            var target = pluginCaptured[capturePlugin];
            var contextKey = string.Concat(menu, "\u001f", section, "\u001f", text);
            target.AddOrUpdate(contextKey,
                _ => new CapturedItem(text!, kind, 1, menu, section, currentWindow),
                (_, old) => old with { Count = old.Count + 1, Kind = MergeKind(old.Kind, kind) });
            return;
        }

        if (baselineCaptureEnabled)
        {
            var contextKey = string.Concat(currentWindow, "\u001f", text);
            backgroundCaptured.AddOrUpdate(contextKey,
                _ => new CapturedItem(text!, kind, 1, string.Empty, string.Empty, currentWindow),
                (_, old) => old with { Count = old.Count + 1, Kind = MergeKind(old.Kind, kind) });
        }
    }

    private static string MergeKind(string oldKind, string newKind) => oldKind.Contains(newKind, StringComparison.Ordinal) ? oldKind : $"{oldKind}/{newKind}";
    private static bool LooksUseful(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim(); if (s.Length < 2 || s.Length > 500 || s.StartsWith("##", StringComparison.Ordinal)) return false;
        // v0.0.28: 英語だけでなく、中国語などのCJK文字も原文候補として取得する。
        return s.Any(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' || (c >= '\u3400' && c <= '\u9fff'));
    }

    private void OnCommand(string command, string args)
    {
        mainWindow.IsOpen = true;
        mainWindow.BringToFront();
    }

    private void OpenUi()
    {
        mainWindow.IsOpen = true;
        mainWindow.BringToFront();
    }

    private enum ButtonRole { Primary, Success, Warning, Danger, Neutral }

    private static bool ActionButton(string label, ButtonRole role)
    {
        var color = role switch
        {
            ButtonRole.Primary => new Vector4(0.18f, 0.42f, 0.68f, 1.00f),
            ButtonRole.Success => new Vector4(0.18f, 0.52f, 0.34f, 1.00f),
            ButtonRole.Warning => new Vector4(0.72f, 0.43f, 0.12f, 1.00f),
            ButtonRole.Danger => new Vector4(0.66f, 0.22f, 0.22f, 1.00f),
            _ => new Vector4(0.30f, 0.33f, 0.38f, 1.00f),
        };
        var hovered = new Vector4(MathF.Min(color.X + 0.10f, 1f), MathF.Min(color.Y + 0.10f, 1f), MathF.Min(color.Z + 0.10f, 1f), color.W);
        var active = new Vector4(MathF.Max(color.X - 0.06f, 0f), MathF.Max(color.Y - 0.06f, 0f), MathF.Max(color.Z - 0.06f, 0f), color.W);
        ImGui.PushStyleColor(ImGuiCol.Button, color);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hovered);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, active);
        var pressed = ImGui.Button(label);
        ImGui.PopStyleColor(3);
        return pressed;
    }

    // 操作を続けられない状態を見落とさないようにする警告表示。
    // 色だけに依存せず、記号と「操作待ち」の文字も常に表示する。
    private static void DrawBlinkingWorkflowWarning(string message)
    {
        var seconds = Environment.TickCount64 / 1000.0;
        var pulse = (float)((Math.Sin(seconds * Math.PI * 2.4) + 1.0) * 0.5);
        // 警告側の赤と、読みやすい白を往復させる。本文は固定色のまま。
        var warningRed = new Vector4(1.00f, 0.12f, 0.12f, 1.00f);
        var warningWhite = new Vector4(1.00f, 1.00f, 1.00f, 1.00f);
        var warning = Vector4.Lerp(warningRed, warningWhite, pulse);

        ImGui.PushStyleColor(ImGuiCol.Text, warning);
        ImGui.TextUnformatted("● 操作待ち");
        ImGui.PopStyleColor();
        ImGui.SameLine();
        ImGui.TextWrapped(message);
    }

    private bool IsMojibakeAllowedForSession(MojibakeIncident incident)
    {
        lock (mojibakeSafetySync)
            return allowedMojibakeIncidentKeys.Contains(incident.DedupeKey);
    }

    private void AllowMojibakeAndResume(MojibakeIncident incident)
    {
        lock (mojibakeSafetySync)
        {
            allowedMojibakeIncidentKeys.Add(incident.DedupeKey);
            allowedMojibakeTargets[incident.DedupeKey] = incident.TargetName;
        }

        if (incident.TargetKind == MojibakeTargetKind.Plugin)
        {
            lock (pluginConfigSync)
            {
                if (config.Plugins.TryGetValue(incident.TargetName, out var state))
                    state.Enabled = true;
            }
            SaveConfig();
        }
        else
        {
            pluginInstallerModule.AllowMojibakeForSessionAndResume(incident);
        }
    }

    private void RecordInstallerMojibakeRepair(MojibakeIncident incident)
    {
        if (incident.TargetKind != MojibakeTargetKind.PluginInstaller) return;

        lock (pluginConfigSync)
        {
            config.InstallerMojibakeRepairHistory ??= new List<InstallerMojibakeRepairRecord>();

            var pluginName = incident.TargetName ?? string.Empty;
            var fieldName = incident.FieldName ?? string.Empty;
            var reason = incident.Reason ?? string.Empty;
            var detectedText = incident.TranslatedText ?? string.Empty;
            var restoredSourceText = incident.SourceText ?? string.Empty;

            // 同じ内容は再検知しても履歴件数へ加算しない。
            // 保存せずPJHを再起動した場合でも、同一内容は最初の1件だけ残す。
            var alreadyRecorded = config.InstallerMojibakeRepairHistory.Any(x => x != null
                && string.Equals(x.PluginName ?? string.Empty, pluginName, StringComparison.Ordinal)
                && string.Equals(x.FieldName ?? string.Empty, fieldName, StringComparison.Ordinal)
                && string.Equals(x.Reason ?? string.Empty, reason, StringComparison.Ordinal)
                && string.Equals(x.DetectedText ?? string.Empty, detectedText, StringComparison.Ordinal)
                && string.Equals(x.RestoredSourceText ?? string.Empty, restoredSourceText, StringComparison.Ordinal));
            if (alreadyRecorded) return;

            config.InstallerMojibakeRepairHistory.Add(new InstallerMojibakeRepairRecord
            {
                RepairedAt = DateTimeOffset.Now,
                PluginName = pluginName,
                FieldName = fieldName,
                Reason = reason,
                DetectedText = detectedText,
                RestoredSourceText = restoredSourceText,
            });

            // 設定ファイルが無制限に肥大化しないよう、新しい200件だけ保持する。
            const int maxHistory = 200;
            if (config.InstallerMojibakeRepairHistory.Count > maxHistory)
                config.InstallerMojibakeRepairHistory.RemoveRange(0, config.InstallerMojibakeRepairHistory.Count - maxHistory);
        }

        SaveConfig();
    }

    private void QueueMojibakeIncident(MojibakeIncident incident)
    {
        lock (mojibakeSafetySync)
        {
            if (!queuedMojibakeIncidentKeys.Add(incident.DedupeKey)) return;
        }
        pendingMojibakeIncidents.Enqueue(incident);
    }

    private void RefreshMojibakeAllowancesForPluginRestarts()
    {
        string[] targets;
        lock (mojibakeSafetySync)
            targets = allowedMojibakeTargets.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        foreach (var target in targets)
        {
            if (!TryGetInstalledPluginState(target, out var isLoaded)) continue;

            if (mojibakePluginLoadStates.TryGetValue(target, out var previous) && !previous && isLoaded)
            {
                lock (mojibakeSafetySync)
                {
                    var remove = allowedMojibakeTargets
                        .Where(x => string.Equals(x.Value, target, StringComparison.OrdinalIgnoreCase))
                        .Select(x => x.Key)
                        .ToArray();
                    foreach (var key in remove)
                    {
                        allowedMojibakeIncidentKeys.Remove(key);
                        allowedMojibakeTargets.Remove(key);
                    }
                }
            }

            mojibakePluginLoadStates[target] = isLoaded;
        }
    }

    private void ProcessMojibakeSafetyIncidents()
    {
        RefreshMojibakeAllowancesForPluginRestarts();
        // v0.4.16: 起動時の全辞書事前走査は行わない。
        // 通常は実際の翻訳適用時に検知し、フックを通らない描画経路だけ
        // 「そのプラグインのウィンドウが実際に表示された時」の限定確認で補完する。
        if (activeMojibakeIncident != null) return;
        if (!pendingMojibakeIncidents.TryDequeue(out var incident)) return;

        activeMojibakeIncident = incident;
        mojibakeCorrectionBuffer = incident.TranslatedText;
        mojibakeAlertWindow.ResetForIncident();

        if (incident.TargetKind == MojibakeTargetKind.Plugin)
        {
            lock (pluginConfigSync)
            {
                if (config.Plugins.TryGetValue(incident.TargetName, out var state))
                    state.Enabled = false;
            }
            lastExplicitWindowOwner = string.Empty;
            lastExplicitWindowOwnerTick = 0;
            SaveConfig();
        }

        if (incident.TargetKind == MojibakeTargetKind.PluginInstaller)
            log.Warning("[PluginJPHelper/MojibakeSafety] Installer項目を自動で原文へ復元: {Target} / {Field} / {Reason}", incident.TargetName, incident.FieldName, incident.Reason);
        else
            log.Warning("[PluginJPHelper/MojibakeSafety] 翻訳を安全停止: {Target} / {Field} / {Reason}", incident.TargetName, incident.FieldName, incident.Reason);
        mojibakeAlertWindow.IsOpen = true;
    }

    private void TryTargetedMojibakeScanForVisiblePlugin(string pluginName)
    {
        if (string.IsNullOrWhiteSpace(pluginName) || activeMojibakeIncident != null) return;
        if (!config.Plugins.TryGetValue(pluginName, out var state) || !state.TranslationTarget || !state.Enabled) return;

        var now = Environment.TickCount64;
        if (mojibakeTargetedScanTicks.TryGetValue(pluginName, out var last)
            && now - last < MojibakeTargetedScanIntervalMs) return;
        mojibakeTargetedScanTicks[pluginName] = now;

        foreach (var row in GetDictionaryCatalog(pluginName))
        {
            var source = row.Key;
            if (!IsSafeSourceForTargetedMojibakeScan(source) || state.DeletedKeys.Contains(source)) continue;

            string candidate;
            string dictionarySource;
            if (state.UserOverrides.TryGetValue(source, out var userJa) && !string.IsNullOrWhiteSpace(userJa))
            {
                candidate = userJa;
                dictionarySource = string.IsNullOrWhiteSpace(state.LastCsvPath) ? "ユーザー訳" : state.LastCsvPath;
            }
            else if (state.OfficialOverrides.TryGetValue(source, out var officialJa) && !string.IsNullOrWhiteSpace(officialJa))
            {
                candidate = officialJa;
                dictionarySource = "公式上書き";
            }
            else
            {
                candidate = row.Value;
                dictionarySource = string.IsNullOrWhiteSpace(state.LastCsvPath) ? "標準辞書" : state.LastCsvPath;
            }

            if (!MojibakeSafety.TryDetectTranslatedText(candidate, out var reason)) continue;

            var incident = new MojibakeIncident(
                MojibakeTargetKind.Plugin, pluginName, "日本語訳", source, candidate, reason, dictionarySource);
            if (IsMojibakeAllowedForSession(incident)) continue;
            state.Enabled = false;
            QueueMojibakeIncident(incident);
            return;
        }
    }

    private static bool IsSafeSourceForTargetedMojibakeScan(string source)
    {
        if (string.IsNullOrWhiteSpace(source) || source.Length > 512) return false;
        // 限定再確認は「原文は正常・訳側だけ異常」のケースだけを補完する。
        // RuntimeLogs等の抽出素材に含まれる壊れた原文はPJH起因として扱わない。
        if (source.IndexOf('\0') >= 0 || source.IndexOf('\r') >= 0 || source.IndexOf('\n') >= 0) return false;
        if (source.Contains('�') || source.Contains("□□□", StringComparison.Ordinal)) return false;
        return true;
    }

    private static string InstallerRepairHistoryKey(InstallerMojibakeRepairRecord item)
        => $"{item.RepairedAt.ToUnixTimeMilliseconds()}|{item.PluginName}|{item.FieldName}";

    private void OpenInstallerRepairHistorySettings()
    {
        requestedMainTab = "文字化け設定";
        requestOpenInstallerRepairHistory = true;
        mainWindow.IsOpen = true;
        mainWindow.BringToFront();
    }

    private void OpenIncidentDictionary(MojibakeIncident incident)
    {
        mainWindow.IsOpen = true;
        if (incident.TargetKind == MojibakeTargetKind.Plugin)
        {
            selectedPlugin = incident.TargetName;
            filter = incident.SourceText;
            requestedMainTab = "翻訳辞書";
        }
        else
        {
            requestedMainTab = "Plugin Installer";
        }
    }

    private bool SaveIncidentCorrection(MojibakeIncident incident, string corrected, out string status)
    {
        status = string.Empty;
        if (incident.TargetKind == MojibakeTargetKind.Plugin)
        {
            lock (pluginConfigSync)
            {
                if (!config.Plugins.TryGetValue(incident.TargetName, out var state))
                {
                    status = "対象プラグインの辞書設定が見つかりません。";
                    return false;
                }

                state.DeletedKeys.Remove(incident.SourceText);
                if (string.IsNullOrWhiteSpace(corrected)) state.UserOverrides.Remove(incident.SourceText);
                else state.UserOverrides[incident.SourceText] = corrected;
            }
            SaveConfig();
            status = "ユーザー訳として保存しました。翻訳は停止したままです。確認後に対象プラグインの日本語化をONにしてください。";
            return true;
        }

        return pluginInstallerModule.TryUpdateMojibakeTranslation(incident, corrected, out status);
    }

    private bool SuppressIncidentTranslation(MojibakeIncident incident, out string status)
    {
        status = string.Empty;
        if (incident.TargetKind == MojibakeTargetKind.Plugin)
        {
            lock (pluginConfigSync)
            {
                if (!config.Plugins.TryGetValue(incident.TargetName, out var state))
                {
                    status = "対象プラグインの辞書設定が見つかりません。";
                    return false;
                }
                state.UserOverrides.Remove(incident.SourceText);
                state.DeletedKeys.Add(incident.SourceText);
            }
            SaveConfig();
            status = "この原文の翻訳を使用しない設定にしました。翻訳は停止したままです。";
            return true;
        }

        return pluginInstallerModule.TryUpdateMojibakeTranslation(incident, string.Empty, out status);
    }

    private void FinishMojibakeIncident(bool keepWindowOpen = false)
    {
        var incident = activeMojibakeIncident;
        if (incident != null)
        {
            lock (mojibakeSafetySync) queuedMojibakeIncidentKeys.Remove(incident.DedupeKey);
        }
        activeMojibakeIncident = null;
        mojibakeCorrectionBuffer = string.Empty;
        if (!keepWindowOpen) mojibakeAlertWindow.IsOpen = false;
    }

    private void DrawMainWindowContents()
    {
        EnsureOfficialNoticeRefresh();
        EnsureCommunityUpdateRefresh();

        if (activeMojibakeIncident != null)
        {
            var mojibakeStatusLabel = activeMojibakeIncident.TargetKind == MojibakeTargetKind.PluginInstaller
                ? $"文字化け自動保護: {activeMojibakeIncident.TargetName}"
                : $"文字化け安全停止中: {activeMojibakeIncident.TargetName}";
            ImGui.TextColored(new Vector4(1.00f, 0.62f, 0.20f, 1.00f), mojibakeStatusLabel);
            ImGui.SameLine();
            if (ImGui.Button("確認画面を開く##MojibakeSafetyReopen")) mojibakeAlertWindow.IsOpen = true;
            ImGui.Separator();
        }

        ImGui.PushStyleColor(ImGuiCol.Tab, new Vector4(0.16f, 0.20f, 0.26f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.TabHovered, new Vector4(0.24f, 0.46f, 0.70f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.TabActive, new Vector4(0.18f, 0.38f, 0.62f, 1.00f));
        if (ImGui.BeginTabBar("mainTabs"))
        {
            var dictFlags = requestedMainTab == "翻訳辞書" ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
            var installerFlags = requestedMainTab == "Plugin Installer" ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
            if (ImGui.BeginTabItem("翻訳辞書", dictFlags)) { DrawScrollableMainTab("dict", DrawDictionaryTab); if (requestedMainTab == "翻訳辞書") requestedMainTab = string.Empty; ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("未翻訳・取得")) { DrawScrollableMainTab("capture", DrawCaptureTab); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("公式辞書")) { DrawScrollableMainTab("official", DrawOfficialDictionaryTab); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("コミュニティ辞書")) { DrawScrollableMainTab("community", DrawCommunityDictionaryTab); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Plugin Installer", installerFlags)) { DrawScrollableMainTab("pluginInstaller", pluginInstallerModule.DrawUi); if (requestedMainTab == "Plugin Installer") requestedMainTab = string.Empty; ImGui.EndTabItem(); }
            var settingsFlags = requestedMainTab == "文字化け設定" ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
            if (ImGui.BeginTabItem("文字化け設定", settingsFlags)) { DrawScrollableMainTab("settings", DrawSettingsTab); if (requestedMainTab == "文字化け設定") requestedMainTab = string.Empty; ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("ヘルプ")) { DrawScrollableMainTab("help", DrawHelpTab); ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }
        ImGui.PopStyleColor(3);
    }

    private static bool TryNormalizeMojibakeInput(string input, out string normalized)
    {
        normalized = string.Empty;
        var value = (input ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value)) return false;

        // U+25A1 / U+00EF U+00BF U+00BD のようなコードポイント入力にも対応。
        if (value.Contains("U+", StringComparison.OrdinalIgnoreCase))
        {
            var tokens = value.Split(new[] { ' ', ',', ';', '/' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var builder = new StringBuilder();
            foreach (var token in tokens)
            {
                if (!token.StartsWith("U+", StringComparison.OrdinalIgnoreCase)) return false;
                var hex = token[2..];
                if (!int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var codepoint)) return false;
                if (codepoint < 0 || codepoint > 0x10FFFF || (codepoint >= 0xD800 && codepoint <= 0xDFFF)) return false;
                builder.Append(char.ConvertFromUtf32(codepoint));
            }
            normalized = builder.ToString();
            return normalized.Length > 0;
        }

        normalized = value;
        return true;
    }

    private static string DescribeMojibakeText(string text, string label = "")
    {
        if (string.IsNullOrEmpty(text)) return "未設定";
        var codes = text.EnumerateRunes().Select(r => $"U+{r.Value:X4}").ToArray();
        var name = label?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name) && codes.Length == 1)
        {
            name = codes[0] switch
            {
                "U+FFFD" => "置換文字",
                "U+25A1" => "白四角",
                _ => "登録文字",
            };
        }
        if (string.IsNullOrWhiteSpace(name)) name = "登録文字列";
        return $"{string.Join(" ", codes)}  {name}";
    }

    private void DrawSettingsTab()
    {
        ImGui.TextUnformatted("文字化け判定設定");
        ImGui.TextDisabled("翻訳結果だけを判定します。原文側の文字化けはこの設定では停止対象にしません。");
        ImGui.TextDisabled("ホワイトリストと判定条件が同時に一致した場合は、ホワイトリストを最優先して警告しません。");
        ImGui.Separator();

        var changed = false;
        var rules = config.MojibakeMarkerRules ??= new List<MojibakeMarkerRule>();

        if (ImGui.CollapsingHeader($"文字化け判定ルール（{rules.Count}件）##MojibakeRuleSection", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextDisabled("登録文字列は実際の文字を表示します。文字列・Unicode欄はドラッグ選択して Ctrl+C でコピーできます。");
            ImGui.Spacing();

            var removeIndex = -1;
            if (ImGui.BeginTable("mojibake_rule_table", 7,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("有効", ImGuiTableColumnFlags.WidthFixed, 48);
                ImGui.TableSetupColumn("名前", ImGuiTableColumnFlags.WidthFixed, 140);
                ImGui.TableSetupColumn("登録文字列", ImGuiTableColumnFlags.WidthStretch, 1.0f);
                ImGui.TableSetupColumn("Unicode", ImGuiTableColumnFlags.WidthFixed, 220);
                ImGui.TableSetupColumn("回数", ImGuiTableColumnFlags.WidthFixed, 72);
                ImGui.TableSetupColumn("条件", ImGuiTableColumnFlags.WidthFixed, 235);
                ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 60);
                ImGui.TableHeadersRow();

                for (var i = 0; i < rules.Count; i++)
                {
                    var rule = rules[i];
                    if (rule == null) continue;
                    ImGui.PushID($"moji_rule_{i}");
                    ImGui.TableNextRow();

                    ImGui.TableSetColumnIndex(0);
                    var enabled = rule.Enabled;
                    if (ImGui.Checkbox("##enabled", ref enabled)) { rule.Enabled = enabled; changed = true; }

                    ImGui.TableSetColumnIndex(1);
                    var label = rule.Label ?? string.Empty;
                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.InputText("##label", ref label, 96)) { rule.Label = label; changed = true; }

                    ImGui.TableSetColumnIndex(2);
                    var displayText = rule.Text ?? string.Empty;
                    ImGui.SetNextItemWidth(-1);
                    ImGui.InputText("##registered_text", ref displayText, 256, ImGuiInputTextFlags.ReadOnly);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("実際に登録されている文字列です。ドラッグ選択してコピーできます。");

                    ImGui.TableSetColumnIndex(3);
                    var unicodeText = string.IsNullOrEmpty(rule.Text)
                        ? "(未設定)"
                        : string.Join(" ", rule.Text.EnumerateRunes().Select(r => $"U+{r.Value:X4}"));
                    var unicodeCopy = unicodeText;
                    ImGui.SetNextItemWidth(-1);
                    ImGui.InputText("##unicode_text", ref unicodeCopy, 512, ImGuiInputTextFlags.ReadOnly);

                    ImGui.TableSetColumnIndex(4);
                    var count = Math.Clamp(rule.MinimumConsecutive, 1, 64);
                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.InputInt("##count", ref count, 1, 1))
                    {
                        rule.MinimumConsecutive = Math.Clamp(count, 1, 64);
                        changed = true;
                    }

                    ImGui.TableSetColumnIndex(5);
                    ImGui.TextUnformatted($"左記文字列が {count} 回繰り返したら検知");

                    ImGui.TableSetColumnIndex(6);
                    if (ImGui.SmallButton("削除")) removeIndex = i;
                    ImGui.PopID();
                }
                ImGui.EndTable();
            }

            if (removeIndex >= 0)
            {
                rules.RemoveAt(removeIndex);
                changed = true;
            }

            ImGui.Spacing();
            ImGui.TextUnformatted("判定ルールを追加");
            ImGui.SetNextItemWidth(260);
            ImGui.InputTextWithHint("##moji_rule_label", "名前（例：白四角）", ref mojibakeRuleAddLabel, 64);
            ImGui.SetNextItemWidth(320);
            ImGui.InputTextWithHint("##moji_rule_add", "文字列 または U+25A1", ref mojibakeRuleAddInput, 128);
            if (TryNormalizeMojibakeInput(mojibakeRuleAddInput, out var addRuleText))
                ImGui.TextDisabled($"認識結果: {DescribeMojibakeText(addRuleText, mojibakeRuleAddLabel)}");
            else if (!string.IsNullOrWhiteSpace(mojibakeRuleAddInput))
                ImGui.TextColored(new Vector4(1f, 0.55f, 0.35f, 1f), "入力を認識できません。実文字または U+XXXX 形式で入力してください。");

            ImGui.SetNextItemWidth(95);
            ImGui.InputInt("繰り返し回数##moji_rule_add_count", ref mojibakeRuleAddMinimum, 1, 1);
            mojibakeRuleAddMinimum = Math.Clamp(mojibakeRuleAddMinimum, 1, 64);
            ImGui.SameLine();
            var canAddRule = TryNormalizeMojibakeInput(mojibakeRuleAddInput, out addRuleText)
                             && !rules.Any(x => x != null && string.Equals(x.Text, addRuleText, StringComparison.Ordinal));
            if (!canAddRule) ImGui.BeginDisabled();
            if (ImGui.Button("判定ルールを追加"))
            {
                rules.Add(new MojibakeMarkerRule
                {
                    Text = addRuleText,
                    Label = mojibakeRuleAddLabel.Trim(),
                    MinimumConsecutive = mojibakeRuleAddMinimum,
                    Enabled = true,
                });
                mojibakeRuleAddInput = string.Empty;
                mojibakeRuleAddLabel = string.Empty;
                mojibakeRuleAddMinimum = 1;
                changed = true;
            }
            if (!canAddRule) ImGui.EndDisabled();

            ImGui.Spacing();
            ImGui.TextUnformatted("特殊判定");
            var control = config.MojibakeDetectControlCharacters;
            if (ImGui.Checkbox("制御文字を検知", ref control)) { config.MojibakeDetectControlCharacters = control; changed = true; }
            ImGui.TextDisabled("改行・タブ以外の制御文字を検知します。");
        }

        ImGui.Spacing();
        ImGui.Separator();

        var white = config.MojibakeWhitelistRules ??= new List<MojibakeWhitelistRule>();
        if (ImGui.CollapsingHeader($"ホワイトリスト（{white.Count}件）##MojibakeWhitelistSection", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextDisabled("ここに一致した翻訳文字列は、他の文字化け判定ルールに一致しても正常扱いにします。");
            ImGui.TextDisabled("ホワイトリストは文字化け判定ルールより優先されます。");
            ImGui.Spacing();

            var removeWhiteIndex = -1;
            if (ImGui.BeginTable("mojibake_white_table", 6,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("有効", ImGuiTableColumnFlags.WidthFixed, 48);
                ImGui.TableSetupColumn("名前", ImGuiTableColumnFlags.WidthFixed, 140);
                ImGui.TableSetupColumn("登録文字列", ImGuiTableColumnFlags.WidthStretch, 1.0f);
                ImGui.TableSetupColumn("Unicode", ImGuiTableColumnFlags.WidthFixed, 220);
                ImGui.TableSetupColumn("判定方式", ImGuiTableColumnFlags.WidthFixed, 110);
                ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 60);
                ImGui.TableHeadersRow();

                for (var i = 0; i < white.Count; i++)
                {
                    var entry = white[i];
                    if (entry == null) continue;
                    ImGui.PushID($"moji_white_{i}");
                    ImGui.TableNextRow();

                    ImGui.TableSetColumnIndex(0);
                    var enabled = entry.Enabled;
                    if (ImGui.Checkbox("##enabled", ref enabled)) { entry.Enabled = enabled; changed = true; }

                    ImGui.TableSetColumnIndex(1);
                    var label = entry.Label ?? string.Empty;
                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.InputText("##label", ref label, 96)) { entry.Label = label; changed = true; }

                    ImGui.TableSetColumnIndex(2);
                    var whiteText = entry.Text ?? string.Empty;
                    ImGui.SetNextItemWidth(-1);
                    ImGui.InputText("##registered_text", ref whiteText, 256, ImGuiInputTextFlags.ReadOnly);

                    ImGui.TableSetColumnIndex(3);
                    var unicodeText = string.IsNullOrEmpty(entry.Text)
                        ? "(未設定)"
                        : string.Join(" ", entry.Text.EnumerateRunes().Select(r => $"U+{r.Value:X4}"));
                    var unicodeCopy = unicodeText;
                    ImGui.SetNextItemWidth(-1);
                    ImGui.InputText("##unicode_text", ref unicodeCopy, 512, ImGuiInputTextFlags.ReadOnly);

                    ImGui.TableSetColumnIndex(4);
                    var modeLabel = entry.PartialMatch ? "部分一致" : "完全一致";
                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.BeginCombo("##white_mode", modeLabel))
                    {
                        if (ImGui.Selectable("完全一致", !entry.PartialMatch)) { entry.PartialMatch = false; changed = true; }
                        if (ImGui.Selectable("部分一致", entry.PartialMatch)) { entry.PartialMatch = true; changed = true; }
                        ImGui.EndCombo();
                    }

                    ImGui.TableSetColumnIndex(5);
                    if (ImGui.SmallButton("削除")) removeWhiteIndex = i;
                    ImGui.PopID();
                }
                ImGui.EndTable();
            }

            if (removeWhiteIndex >= 0)
            {
                white.RemoveAt(removeWhiteIndex);
                changed = true;
            }

            ImGui.Spacing();
            ImGui.TextUnformatted("ホワイトリストを追加");
            ImGui.SetNextItemWidth(260);
            ImGui.InputTextWithHint("##moji_white_label", "名前（例：フランス語 Âmes）", ref mojibakeWhitelistAddLabel, 64);
            ImGui.SetNextItemWidth(320);
            ImGui.InputTextWithHint("##moji_white_add", "文字列 または U+XXXX", ref mojibakeWhitelistAddInput, 256);
            if (TryNormalizeMojibakeInput(mojibakeWhitelistAddInput, out var addWhiteText))
                ImGui.TextDisabled($"認識結果: {DescribeMojibakeText(addWhiteText, mojibakeWhitelistAddLabel)}");
            ImGui.Checkbox("部分一致でホワイトリスト登録", ref mojibakeWhitelistAddPartial);
            ImGui.SameLine();
            var canAddWhite = TryNormalizeMojibakeInput(mojibakeWhitelistAddInput, out addWhiteText)
                              && !white.Any(x => x != null && string.Equals(x.Text, addWhiteText, StringComparison.Ordinal) && x.PartialMatch == mojibakeWhitelistAddPartial);
            if (!canAddWhite) ImGui.BeginDisabled();
            if (ImGui.Button("ホワイトリストに追加"))
            {
                white.Add(new MojibakeWhitelistRule
                {
                    Text = addWhiteText,
                    Label = mojibakeWhitelistAddLabel.Trim(),
                    Enabled = true,
                    PartialMatch = mojibakeWhitelistAddPartial,
                });
                mojibakeWhitelistAddInput = string.Empty;
                mojibakeWhitelistAddLabel = string.Empty;
                mojibakeWhitelistAddPartial = false;
                changed = true;
            }
            if (!canAddWhite) ImGui.EndDisabled();
        }

        ImGui.Spacing();
        ImGui.Separator();

        var repairHistory = config.InstallerMojibakeRepairHistory ??= new List<InstallerMojibakeRepairRecord>();
        if (requestOpenInstallerRepairHistory)
        {
            ImGui.SetNextItemOpen(true, ImGuiCond.Always);
            requestOpenInstallerRepairHistory = false;
        }
        if (ImGui.CollapsingHeader($"Plugin Installer 文字化け修復履歴（{repairHistory.Count}件）##InstallerMojibakeRepairHistory"))
        {
            ImGui.TextDisabled("文字化けを検知した項目は、その場では元Manifestの原文へ戻しています。");
            ImGui.TextColored(new Vector4(1f, 0.78f, 0.28f, 1f), "ここで保存するまでは、再翻訳・翻訳除外の指定は今回のPJH起動中だけの仮処理です。");
            ImGui.TextDisabled("最終的に『現在の翻訳ファイルへ上書き保存』を押すと、現在使用中のPlugin Installer翻訳ファイルへ確定保存します。");
            ImGui.Spacing();

            if (repairHistory.Count == 0)
            {
                ImGui.TextDisabled("修復履歴はありません。");
            }
            else
            {
                if (ImGui.Button("全選択##SelectAllInstallerMojibakeRepair"))
                    foreach (var item in repairHistory.Where(x => x != null)) selectedInstallerRepairHistory.Add(InstallerRepairHistoryKey(item));
                ImGui.SameLine();
                if (ImGui.Button("選択解除##ClearInstallerMojibakeRepairSelection")) selectedInstallerRepairHistory.Clear();

                var selectedRecords = repairHistory.Where(x => x != null && selectedInstallerRepairHistory.Contains(InstallerRepairHistoryKey(x))).ToArray();
                var hasSelection = selectedRecords.Length > 0;
                if (!hasSelection) ImGui.BeginDisabled();
                if (ImGui.Button("選択項目を再翻訳##RetranslateInstallerMojibake"))
                    _ = pluginInstallerModule.StageMojibakeRetranslateAsync(selectedRecords);
                ImGui.SameLine();
                if (ImGui.Button("選択項目を今後は翻訳しない##NoTranslateInstallerMojibake"))
                    pluginInstallerModule.StageMojibakeDoNotTranslate(selectedRecords);
                if (!hasSelection) ImGui.EndDisabled();

                ImGui.SameLine();
                var hasStaged = pluginInstallerModule.HasStagedMojibakeRepairs;
                if (!hasStaged) ImGui.BeginDisabled();
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.18f, 0.48f, 0.26f, 1f));
                var commit = ImGui.Button("現在の翻訳ファイルへ上書き保存##CommitInstallerMojibakeRepairs", new Vector2(280, 0));
                ImGui.PopStyleColor();
                if (!hasStaged) ImGui.EndDisabled();
                if (commit)
                {
                    if (pluginInstallerModule.CommitStagedMojibakeRepairs())
                        selectedInstallerRepairHistory.Clear();
                }

                ImGui.SameLine();
                if (ImGui.Button("修復履歴を消去##ClearInstallerMojibakeRepairHistory"))
                {
                    repairHistory.Clear();
                    selectedInstallerRepairHistory.Clear();
                    changed = true;
                }

                if (!string.IsNullOrWhiteSpace(pluginInstallerModule.MojibakeRepairStageStatus))
                    ImGui.TextWrapped(pluginInstallerModule.MojibakeRepairStageStatus);

                ImGui.Spacing();

                if (ImGui.BeginTable("installer_mojibake_repair_history", 7,
                    ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY,
                    new Vector2(0, 260)))
                {
                    ImGui.TableSetupColumn("選択", ImGuiTableColumnFlags.WidthFixed, 55);
                    ImGui.TableSetupColumn("日時", ImGuiTableColumnFlags.WidthFixed, 150);
                    ImGui.TableSetupColumn("プラグイン", ImGuiTableColumnFlags.WidthFixed, 150);
                    ImGui.TableSetupColumn("項目", ImGuiTableColumnFlags.WidthFixed, 95);
                    ImGui.TableSetupColumn("検知理由", ImGuiTableColumnFlags.WidthFixed, 180);
                    ImGui.TableSetupColumn("現在の翻訳", ImGuiTableColumnFlags.WidthStretch, 1.0f);
                    ImGui.TableSetupColumn("翻訳前の原文", ImGuiTableColumnFlags.WidthStretch, 1.0f);
                    ImGui.TableHeadersRow();

                    for (var i = repairHistory.Count - 1; i >= 0; i--)
                    {
                        var item = repairHistory[i];
                        if (item == null) continue;
                        var selectionKey = InstallerRepairHistoryKey(item);
                        ImGui.PushID($"installer_repair_{i}");
                        ImGui.TableNextRow();

                        ImGui.TableSetColumnIndex(0);
                        var selected = selectedInstallerRepairHistory.Contains(selectionKey);
                        if (ImGui.Checkbox("##selected", ref selected))
                        {
                            if (selected) selectedInstallerRepairHistory.Add(selectionKey);
                            else selectedInstallerRepairHistory.Remove(selectionKey);
                        }

                        ImGui.TableSetColumnIndex(1);
                        ImGui.TextUnformatted(item.RepairedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss"));
                        ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(item.PluginName ?? string.Empty);
                        ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(item.FieldName ?? string.Empty);
                        ImGui.TableSetColumnIndex(4); ImGui.TextWrapped(item.Reason ?? string.Empty);
                        ImGui.TableSetColumnIndex(5);
                        var detected = item.DetectedText ?? string.Empty;
                        ImGui.SetNextItemWidth(-1); ImGui.InputText("##detected", ref detected, 4096, ImGuiInputTextFlags.ReadOnly);
                        ImGui.TableSetColumnIndex(6);
                        var restored = item.RestoredSourceText ?? string.Empty;
                        ImGui.SetNextItemWidth(-1); ImGui.InputText("##restored", ref restored, 4096, ImGuiInputTextFlags.ReadOnly);
                        ImGui.PopID();
                    }
                    ImGui.EndTable();
                }

            }
        }

        if (changed)
        {
            MojibakeSafety.Configure(config);
            SaveConfig();
        }
    }

    private sealed class MojibakeAlertWindow : Window
    {
        private readonly Plugin owner;
        private int selectedAction;

        internal MojibakeAlertWindow(Plugin owner)
            : base("文字化けを検知 v0.5.0###PluginJPHelperMojibakeAlert")
        {
            this.owner = owner;
            Size = new Vector2(760, 560);
            SizeCondition = ImGuiCond.FirstUseEver;
        }

        internal void ResetForIncident()
        {
            selectedAction = 0;
        }

        public override void PreDraw() => drawingOwnUi = true;

        public override void Draw()
        {
            var incident = owner.activeMojibakeIncident;
            if (incident == null)
            {
                ImGui.TextDisabled("現在、確認が必要な文字化けはありません。");
                return;
            }

            if (incident.TargetKind == MojibakeTargetKind.PluginInstaller)
                ImGui.TextWrapped("Plugin Installerの翻訳で文字化けを検知しました。この項目だけ原文に戻しました。ほかのInstaller翻訳は継続します。");
            else
                ImGui.TextWrapped("以下の翻訳の文字化けを検知、対象のプラグインの翻訳をOFFにします。");
            ImGui.Separator();
            ImGui.TextColored(new Vector4(1.00f, 0.82f, 0.28f, 1.00f), $"【対象プラグイン】 {incident.TargetName}");
            ImGui.TextUnformatted($"種類: {(incident.TargetKind == MojibakeTargetKind.PluginInstaller ? "Plugin Installer" : "プラグイン翻訳")}");
            ImGui.TextUnformatted($"辞書: {incident.DictionarySource}");
            ImGui.TextWrapped($"検知理由: {incident.Reason}");
            ImGui.Separator();
            ImGui.TextUnformatted("原文");
            DrawAutoHeightTextBox("##MojibakeSource", incident.SourceText);
            ImGui.TextUnformatted("検知した翻訳");
            DrawAutoHeightTextBox("##MojibakeBadTranslation", incident.TranslatedText);

            if (incident.TargetKind == MojibakeTargetKind.PluginInstaller)
            {
                ImGui.Separator();
                ImGui.TextWrapped("この項目はPJHが自動で元Manifestの原文表示へ戻しました。正常なほかの翻訳はそのまま使用されます。");
                ImGui.TextWrapped("恒久的に直す場合は、修復履歴画面で『再翻訳』または『今後は翻訳しない』を選び、最後に現在の翻訳ファイルへ上書き保存してください。保存しない場合は今回の起動中だけの処理です。");
                ImGui.Spacing();
                if (ImGui.Button("修復履歴・再処理画面を開く", new Vector2(240, 0)))
                {
                    owner.OpenInstallerRepairHistorySettings();
                    owner.FinishMojibakeIncident();
                    ResetForIncident();
                }
                ImGui.SameLine();
                if (ImGui.Button("閉じる", new Vector2(120, 0)))
                {
                    owner.FinishMojibakeIncident();
                    ResetForIncident();
                }
                return;
            }

            ImGui.Separator();
            ImGui.TextUnformatted("処理を選択してください。");

            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.48f, 0.92f, 1.00f, 1.00f));
            ImGui.RadioButton("辞書を表示して修正", ref selectedAction, 1);
            ImGui.PopStyleColor();

            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.00f, 0.82f, 0.28f, 1.00f));
            ImGui.RadioButton("翻訳を無効にしプラグインを起動", ref selectedAction, 2);
            ImGui.PopStyleColor();

            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.00f, 0.52f, 0.52f, 1.00f));
            ImGui.RadioButton("文字化けのままプラグインを起動", ref selectedAction, 3);
            ImGui.PopStyleColor();

            ImGui.Spacing();
            if (selectedAction == 1)
            {
                ImGui.TextWrapped("文字化けを検知した辞書を開きます。内容を修正し、「保存」「削除」「訳を消す」のいずれかを行った後、名前を付けて書き出してください。");
            }
            else if (selectedAction == 2)
            {
                ImGui.TextWrapped("翻訳を無効にし、未翻訳のまま対象プラグインを起動します。\n翻訳を有効にした時、文字化けの修正が行われていなければ再度表示します。");
            }
            else if (selectedAction == 3)
            {
                ImGui.TextWrapped("文字化けした翻訳をそのまま使用して、対象プラグインを起動します。\n他へ影響が波及する可能性があるため、おすすめはできません。\n文字化けの理由が分かっており、修正できない、または修正する必要がない場合のみ選択してください。\nこの警告はPJHを再起動した時に再表示します。");
            }

            ImGui.Spacing();
            if (selectedAction == 0) ImGui.BeginDisabled();
            var buttonColor = selectedAction switch
            {
                1 => new Vector4(0.18f, 0.46f, 0.62f, 1.00f),
                2 => new Vector4(0.62f, 0.48f, 0.12f, 1.00f),
                3 => new Vector4(0.62f, 0.20f, 0.20f, 1.00f),
                _ => new Vector4(0.30f, 0.30f, 0.30f, 1.00f),
            };
            ImGui.PushStyleColor(ImGuiCol.Button, buttonColor);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, buttonColor + new Vector4(0.08f, 0.08f, 0.08f, 0.00f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, buttonColor - new Vector4(0.06f, 0.06f, 0.06f, 0.00f));
            var execute = ImGui.Button("選択した処理を実行", new Vector2(220, 0));
            ImGui.PopStyleColor(3);
            if (selectedAction == 0) ImGui.EndDisabled();

            if (execute)
            {
                switch (selectedAction)
                {
                    case 1:
                        owner.OpenIncidentDictionary(incident);
                        break;
                    case 2:
                        // 検知時点で対象翻訳は既にOFF。辞書内容は変更しない。
                        break;
                    case 3:
                        owner.AllowMojibakeAndResume(incident);
                        break;
                }

                owner.FinishMojibakeIncident();
                ResetForIncident();
                return;
            }
        }

        private static void DrawAutoHeightTextBox(string id, string text)
        {
            var available = Math.Max(ImGui.GetContentRegionAvail().X - 20.0f, 120.0f);
            var measured = ImGui.CalcTextSize(string.IsNullOrEmpty(text) ? " " : text, false, available);
            var lineHeight = ImGui.GetTextLineHeightWithSpacing();
            var desired = Math.Clamp(measured.Y + lineHeight * 1.4f, lineHeight * 2.2f, lineHeight * 7.0f);
            ImGui.BeginChild(id, new Vector2(0, desired), true);
            ImGui.TextWrapped(text);
            ImGui.EndChild();
        }

        public override void PostDraw() => drawingOwnUi = false;
    }

    private sealed class MainWindow : Window
    {
        private readonly Plugin owner;

        public MainWindow(Plugin owner)
            : base("Plugin JP Helper###PluginJPHelperMain")
        {
            this.owner = owner;
            Size = new Vector2(920, 650);
            SizeCondition = ImGuiCond.FirstUseEver;

            // Dalamud標準WindowSystemのウィンドウ設定を利用する。
            // 透明度・背景ブラー・ピン留め・クリック透過はタイトルバーの標準設定メニューから変更できる。
            BgAlpha = null;
            AllowBackgroundBlur = true;
            AllowPinning = true;
            AllowClickthrough = true;

        }

        public override void PreDraw()
        {
            // WindowSystem が ImGui.Begin() を呼ぶ前から、自分自身を翻訳・取得対象外にする。
            drawingOwnUi = true;
            WindowName = owner.BuildMainWindowTitle();
        }

        public override void Draw() => owner.DrawMainWindowContents();

        public override void PostDraw()
        {
            drawingOwnUi = false;
        }
    }

    private void DrawScrollableMainTab(string id, Action drawContent)
    {
        if (ImGui.BeginChild($"##mainTabContent_{id}", new Vector2(0, 0), false, ImGuiWindowFlags.None))
            drawContent();
        ImGui.EndChild();
    }

    private sealed class CommunityDictionaryEntry
    {
        public string FileName { get; set; } = string.Empty;
        public string PluginName { get; set; } = string.Empty;
        public string Owner { get; set; } = string.Empty;
        public string Updated { get; set; } = string.Empty;
        public string Sha { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
    }

    private sealed class CommunityDictionaryIndex
    {
        public int Version { get; set; }
        public List<CommunityDictionaryEntry> Entries { get; set; } = new();
    }

    private string CommunityDictionaryDirectory()
    {
        var dir = Path.Combine(DictionaryDirectory(), "Community");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string CommunityCsvPath(string fileName) => Path.Combine(CommunityDictionaryDirectory(), fileName);
    private string CommunityShaPath(string fileName) => CommunityCsvPath(fileName) + ".sha";

    private string GetCommunityDictionaryState(CommunityDictionaryEntry item)
    {
        var path = CommunityCsvPath(item.FileName);
        if (!File.Exists(path)) return "未取得";
        var shaPath = CommunityShaPath(item.FileName);
        if (!File.Exists(shaPath)) return "導入済み";
        try
        {
            var localSha = File.ReadAllText(shaPath, Encoding.UTF8).Trim();
            return string.Equals(localSha, item.Sha, StringComparison.Ordinal) ? "最新版" : "更新あり";
        }
        catch { return "導入済み"; }
    }

    private void EnsureCommunityUpdateRefresh()
    {
        // 自動確認はDalamudのrepo読込と競合させない。
        if (!pluginInstallerModule.IsSafeForBackgroundNetworkWork()) return;
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref communityLastAttemptTick);
        if (communityDictionaryBusy || (last != 0 && now - last < UpdateCheckIntervalMs)) return;
        _ = RefreshCommunityDictionariesAsync(false);
    }

    private Task RefreshCommunityDictionariesAsync(bool acknowledge)
    {
        if (communityDictionaryBusy) return Task.CompletedTask;
        communityDictionaryBusy = true;
        Interlocked.Exchange(ref communityLastAttemptTick, Environment.TickCount64);
        if (acknowledge) communityStatus = "GitHubのコミュニティ辞書一覧を確認しています...";

        return Task.Run(() =>
        {
            try
            {
                using var apiReq = new HttpRequestMessage(HttpMethod.Get, CommunityIndexApiUrl);
                apiReq.Headers.UserAgent.ParseAdd("PluginJPHelper/0.4.4");
                using var apiRes = officialDictionaryHttp.SendAsync(apiReq).GetAwaiter().GetResult();
                apiRes.EnsureSuccessStatusCode();
                var apiJson = apiRes.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                using var apiDoc = JsonDocument.Parse(apiJson);
                var root = apiDoc.RootElement;
                var indexSha = root.TryGetProperty("sha", out var shaProp) ? shaProp.GetString() ?? string.Empty : string.Empty;
                var encodedContent = root.TryGetProperty("content", out var contentProp) ? contentProp.GetString() ?? string.Empty : string.Empty;
                var encoding = root.TryGetProperty("encoding", out var encodingProp) ? encodingProp.GetString() ?? string.Empty : string.Empty;
                if (string.IsNullOrWhiteSpace(encodedContent) || !string.Equals(encoding, "base64", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("community-index.json の本文を取得できませんでした。");

                var compactBase64 = encodedContent.Replace("\n", string.Empty).Replace("\r", string.Empty);
                var indexJson = Encoding.UTF8.GetString(Convert.FromBase64String(compactBase64));
                var index = JsonSerializer.Deserialize<CommunityDictionaryIndex>(indexJson, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? new CommunityDictionaryIndex();

                var entries = (index.Entries ?? new List<CommunityDictionaryEntry>())
                    .Where(x => !string.IsNullOrWhiteSpace(x.FileName)
                        && x.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(x.DownloadUrl))
                    .OrderBy(x => x.PluginName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                lock (communityDictionaryLock)
                    communityDictionaryEntries = entries;

                var previousSha = config.LastAcknowledgedCommunityIndexSha ?? string.Empty;
                communityNoticePending = !string.IsNullOrWhiteSpace(indexSha)
                    && !string.Equals(indexSha, previousSha, StringComparison.Ordinal);

                if (acknowledge)
                {
                    communityListLoaded = true;
                    communityStatus = $"コミュニティ辞書 {entries.Count}件を確認しました。";
                    communityNoticePending = false;
                    // SaveConfig() は UI キャッシュを Clear() するため描画スレッドで行う。
                    RunOnMainThread(() =>
                    {
                        config.LastAcknowledgedCommunityIndexSha = indexSha;
                        SaveConfig();
                    });
                }
            }
            catch (Exception ex)
            {
                if (acknowledge)
                    communityStatus = $"コミュニティ辞書一覧の取得に失敗しました: {ex.Message}";
                log.Warning(ex, "[PluginJPHelper] community dictionary list failed");
            }
            finally
            {
                communityDictionaryBusy = false;
            }
        });
    }

    private Task DownloadSelectedCommunityDictionariesAsync()
    {
        if (communityDictionaryBusy) return Task.CompletedTask;
        List<CommunityDictionaryEntry> targets;
        lock (communityDictionaryLock)
            targets = communityDictionaryEntries.Where(x => selectedCommunityDictionaries.Contains(x.FileName)).ToList();
        if (targets.Count == 0)
        {
            communityStatus = "ダウンロード／更新する辞書を選択してください。";
            return Task.CompletedTask;
        }

        communityDictionaryBusy = true;
        return Task.Run(() =>
        {
            try
            {
                var completed = 0;
                foreach (var item in targets)
                {
                    communityStatus = $"{item.FileName} をダウンロードしています...";
                    using var req = new HttpRequestMessage(HttpMethod.Get, item.DownloadUrl);
                    req.Headers.UserAgent.ParseAdd("PluginJPHelper/0.4.4");
                    using var res = officialDictionaryHttp.SendAsync(req).GetAwaiter().GetResult();
                    res.EnsureSuccessStatusCode();
                    var text = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    File.WriteAllText(CommunityCsvPath(item.FileName), text, new UTF8Encoding(true));
                    File.WriteAllText(CommunityShaPath(item.FileName), item.Sha, Encoding.UTF8);

                    // パースまでは背景スレッドで行う（共有状態に触れないため）。
                    var rows = ParseCsvRecords(text);
                    var metadataPluginName = rows.Skip(1)
                        .FirstOrDefault(r => r.Count >= 6 && !string.IsNullOrWhiteSpace(r[0]))?[0]?.Trim();
                    if (string.IsNullOrWhiteSpace(metadataPluginName))
                        metadataPluginName = item.PluginName;

                    // 設定への反映は描画スレッドで行う。
                    if (!string.IsNullOrWhiteSpace(metadataPluginName))
                    {
                        var applyPluginName = metadataPluginName;
                        var communityWindows = ExtractDictionaryWindowKeywords(rows);
                        RunOnMainThread(() =>
                        {
                            if (!config.Plugins.TryGetValue(applyPluginName, out var metadataState) || metadataState == null)
                            {
                                metadataState = new PluginDictionaryState
                                {
                                    Enabled = true,
                                    TranslationTarget = true,
                                    WindowKeyword = applyPluginName
                                };
                                lock (pluginConfigSync)
                                    config.Plugins[applyPluginName] = metadataState;
                                EnsureCaptureDictionary(applyPluginName);
                            }

                            // 明示的なダウンロード/更新操作なので、辞書側に記載された @Window は
                            // 過去の抑止より優先して再適用する。
                            metadataState.SuppressedDictionaryWindowKeywords ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var keyword in communityWindows)
                                metadataState.SuppressedDictionaryWindowKeywords.Remove(keyword);

                            ApplyDictionaryWindowMetadata(applyPluginName, metadataState, communityWindows, "コミュニティ辞書");
                        });
                    }

                    completed++;
                }
                RunOnMainThread(SaveConfig);
                communityStatus = $"選択したコミュニティ辞書 {completed}件をダウンロード／更新しました。別ウィンドウ設定が含まれる辞書は自動適用しました。";
            }
            catch (Exception ex)
            {
                communityStatus = $"コミュニティ辞書のダウンロードに失敗しました: {ex.Message}";
                log.Warning(ex, "[PluginJPHelper] community dictionary download failed");
            }
            finally
            {
                communityDictionaryBusy = false;
            }
        });
    }

    private void DrawCommunityDictionaryTab()
    {
        ImGui.TextWrapped("ユーザーが作成・共有したコミュニティ辞書を確認し、必要なものだけダウンロード／更新できます。");
        ImGui.Separator();

        ImGui.TextUnformatted("自分の投稿名");
        ImGui.SetNextItemWidth(260f);
        ImGui.InputTextWithHint("##communityPosterName", "例: エルパ", ref communityPosterNameBuffer, 128);
        ImGui.SameLine();
        if (ActionButton("保存", ButtonRole.Primary))
        {
            config.CommunityPosterName = communityPosterNameBuffer.Trim();
            communityPosterNameBuffer = config.CommunityPosterName;
            SaveConfig();
        }
        ImGui.TextDisabled("Web投稿時に入力した投稿者名と一致する辞書を [自分] として強調表示します。削除権限の判定には使用しません。");
        ImGui.Separator();

        if (ActionButton("コミュニティ辞書一覧を取得", ButtonRole.Primary) && !communityDictionaryBusy)
            _ = RefreshCommunityDictionariesAsync(true);
        ImGui.SameLine();
        if (ActionButton("コミュニティ辞書フォルダーを開く", ButtonRole.Success))
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = CommunityDictionaryDirectory(), UseShellExecute = true });
                communityStatus = "コミュニティ辞書フォルダーを開きました。";
            }
            catch (Exception ex) { communityStatus = $"フォルダーを開けませんでした: {ex.Message}"; }
        }
        ImGui.SameLine();
        if (ActionButton("コミュニティ辞書をアップロード", ButtonRole.Warning))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://pjh-community-upload.akumanomaria.workers.dev/",
                    UseShellExecute = true,
                });
                communityStatus = "コミュニティ辞書の投稿ページを開きました。";
            }
            catch (Exception ex) { communityStatus = $"アップロードページを開けませんでした: {ex.Message}"; }
        }
        if (communityDictionaryBusy) { ImGui.SameLine(); ImGui.TextDisabled("通信中..."); }

        if (!string.IsNullOrWhiteSpace(communityStatus)) ImGui.TextWrapped(communityStatus);
        if (!communityListLoaded)
        {
            ImGui.TextDisabled("「コミュニティ辞書一覧を取得」を押すとGitHub上の一覧を表示します。");
            return;
        }

        ImGui.SetNextItemWidth(300f);
        ImGui.InputTextWithHint("##communityFilter", "辞書名・プラグイン名・投稿者で検索", ref communityFilter, 160);
        ImGui.SameLine();
        if (ActionButton("検索をクリア", ButtonRole.Neutral)) communityFilter = string.Empty;

        List<CommunityDictionaryEntry> snapshot;
        lock (communityDictionaryLock) snapshot = communityDictionaryEntries.ToList();
        var filterText = communityFilter.Trim();
        var visibleEntries = snapshot.Where(x =>
            filterText.Length == 0
            || x.FileName.Contains(filterText, StringComparison.OrdinalIgnoreCase)
            || x.PluginName.Contains(filterText, StringComparison.OrdinalIgnoreCase)
            || x.Owner.Contains(filterText, StringComparison.OrdinalIgnoreCase)).ToArray();

        if (visibleEntries.Length == 0)
        {
            ImGui.TextDisabled("表示できるコミュニティ辞書がありません。");
        }
        else
        {
            DrawDictionaryStatusColumnControls("community");
            DrawListSortControls("community", new[] { "辞書", "対応プラグイン", "翻訳辞書登録", "未翻訳登録", "日本語化", "導入", "プラグインスイッチ", "投稿者", "更新日", "状態" }, ref communityDictionarySortMode, ref communityDictionarySortAscending);
            visibleEntries = communityDictionarySortMode switch
            {
                1 => SortArray(visibleEntries, x => x.PluginName, communityDictionarySortAscending),
                2 => SortArray(visibleEntries, x => GetPluginStatusColumns(x.PluginName).TranslationRegistered == "有り", communityDictionarySortAscending),
                3 => SortArray(visibleEntries, x => GetPluginStatusColumns(x.PluginName).CaptureRegistered == "有り", communityDictionarySortAscending),
                4 => SortArray(visibleEntries, x => GetPluginStatusColumns(x.PluginName).TranslationEnabled == "ON", communityDictionarySortAscending),
                5 => SortArray(visibleEntries, x => GetPluginStatusColumns(x.PluginName).Installed == "導入済み", communityDictionarySortAscending),
                6 => SortArray(visibleEntries, x => GetPluginStatusColumns(x.PluginName).PluginSwitch == "ON", communityDictionarySortAscending),
                7 => SortArray(visibleEntries, x => x.Owner, communityDictionarySortAscending),
                8 => SortArray(visibleEntries, x => x.Updated, communityDictionarySortAscending),
                9 => SortArray(visibleEntries, x => GetCommunityDictionaryState(x), communityDictionarySortAscending),
                _ => SortArray(visibleEntries, x => x.FileName, communityDictionarySortAscending),
            };
            var statusColumnCount = VisibleDictionaryStatusColumnCount();
            if (ImGui.BeginTable("communityDictionaryTable", 6 + statusColumnCount, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollX | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoKeepColumnsVisible, new Vector2(0, 0)))
            {
                ImGui.TableSetupColumn("選択", ImGuiTableColumnFlags.WidthFixed, 55);
                ImGui.TableSetupColumn("辞書", ImGuiTableColumnFlags.WidthFixed, 190);
                ImGui.TableSetupColumn("対応プラグイン", ImGuiTableColumnFlags.WidthFixed, 170);
                if (showStatusTranslationRegistered) ImGui.TableSetupColumn("翻訳辞書登録", ImGuiTableColumnFlags.WidthFixed, 95);
                if (showStatusCaptureRegistered) ImGui.TableSetupColumn("未翻訳登録", ImGuiTableColumnFlags.WidthFixed, 90);
                if (showStatusTranslationEnabled) ImGui.TableSetupColumn("日本語化", ImGuiTableColumnFlags.WidthFixed, 80);
                if (showStatusInstalled) ImGui.TableSetupColumn("導入", ImGuiTableColumnFlags.WidthFixed, 85);
                if (showStatusPluginSwitch) ImGui.TableSetupColumn("プラグインスイッチ", ImGuiTableColumnFlags.WidthFixed, 115);
                ImGui.TableSetupColumn("投稿者", ImGuiTableColumnFlags.WidthFixed, 170);
                ImGui.TableSetupColumn("更新日", ImGuiTableColumnFlags.WidthFixed, 105);
                ImGui.TableSetupColumn("状態", ImGuiTableColumnFlags.WidthFixed, 90);
                var communityHeaders = new List<string> { "選択", "辞書", "対応プラグイン" };
                if (showStatusTranslationRegistered) communityHeaders.Add("翻訳辞書登録");
                if (showStatusCaptureRegistered) communityHeaders.Add("未翻訳登録");
                if (showStatusTranslationEnabled) communityHeaders.Add("日本語化");
                if (showStatusInstalled) communityHeaders.Add("導入");
                if (showStatusPluginSwitch) communityHeaders.Add("プラグインスイッチ");
                communityHeaders.Add("投稿者");
                communityHeaders.Add("更新日");
                communityHeaders.Add("状態");
                DrawJapaneseTableHeaderRow(communityHeaders);

            foreach (var item in visibleEntries)
            {
                var isMine = !string.IsNullOrWhiteSpace(config.CommunityPosterName)
                    && string.Equals(config.CommunityPosterName, item.Owner, StringComparison.OrdinalIgnoreCase);
                var mineColor = new Vector4(0.35f, 0.95f, 0.55f, 1.00f);
                var state = GetCommunityDictionaryState(item);

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                var selected = selectedCommunityDictionaries.Contains(item.FileName);
                if (ImGui.Checkbox($"##community_{item.FileName}", ref selected))
                {
                    if (selected) selectedCommunityDictionaries.Add(item.FileName);
                    else selectedCommunityDictionaries.Remove(item.FileName);
                }

                ImGui.TableSetColumnIndex(1);
                if (isMine) ImGui.TextColored(mineColor, item.FileName); else ImGui.TextUnformatted(item.FileName);
                ImGui.TableSetColumnIndex(2);
                if (isMine) ImGui.TextColored(mineColor, item.PluginName); else ImGui.TextUnformatted(item.PluginName);
                var pluginStatus = GetPluginStatusColumns(item.PluginName);
                var column = 3;
                if (showStatusTranslationRegistered) { ImGui.TableSetColumnIndex(column++); DrawYesNoStatus(pluginStatus.TranslationRegistered); }
                if (showStatusCaptureRegistered) { ImGui.TableSetColumnIndex(column++); DrawYesNoStatus(pluginStatus.CaptureRegistered); }
                if (showStatusTranslationEnabled) { ImGui.TableSetColumnIndex(column++); DrawOnOffStatus(pluginStatus.TranslationEnabled); }
                if (showStatusInstalled) { ImGui.TableSetColumnIndex(column++); DrawInstalledStatus(pluginStatus.Installed); }
                if (showStatusPluginSwitch) { ImGui.TableSetColumnIndex(column++); DrawOnOffStatus(pluginStatus.PluginSwitch); }
                ImGui.TableSetColumnIndex(column++);
                if (isMine) ImGui.TextColored(mineColor, $"{item.Owner} [自分]"); else ImGui.TextUnformatted(item.Owner);
                ImGui.TableSetColumnIndex(column++);
                if (isMine) ImGui.TextColored(mineColor, item.Updated); else ImGui.TextUnformatted(item.Updated);
                ImGui.TableSetColumnIndex(column);
                if (state == "更新あり") ImGui.TextColored(new Vector4(1f, 0.75f, 0.20f, 1f), state);
                else if (state == "最新版") ImGui.TextColored(new Vector4(0.35f, 0.85f, 0.50f, 1f), state);
                else ImGui.TextUnformatted(state);
            }
                ImGui.EndTable();
            }
        }

        if (ActionButton("選択した辞書をダウンロード／更新", ButtonRole.Success) && !communityDictionaryBusy)
            _ = DownloadSelectedCommunityDictionariesAsync();
    }

    private static readonly (string Key, string Label)[] RsrBaseDictionaryTabs =
    {
        ("Main", "メイン"),
        ("Actions", "アクション"),
        ("List", "リスト"),
        ("Basic", "基本"),
        ("UI", "UI"),
        ("Auto", "自動"),
        ("Target", "ターゲット"),
        ("Duty", "コンテンツ"),
        ("Extra", "その他"),
        ("Debug", "デバッグ"),
        ("AutoDuty", "AutoDuty"),
    };

    private IEnumerable<(string Key, string Label)> GetRsrDictionaryTabs()
    {
        yield return ("Main", "メイン");
        var known = new HashSet<string>(RsrBaseDictionaryTabs.Select(x => x.Key), StringComparer.Ordinal) { "Uncategorized" };
        var dynamicMenus = config.Plugins["RSR"].Locations.Values
            .Select(x => x.Menu)
            .Where(x => !string.IsNullOrWhiteSpace(x) && !known.Contains(x))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
        foreach (var menu in dynamicMenus) yield return (menu, menu);
        foreach (var tab in RsrBaseDictionaryTabs.Skip(1)) yield return tab;
        yield return ("Uncategorized", "未分類");
    }

    private void RemoveTranslationTarget(string pluginName)
    {
        if (!config.Plugins.TryGetValue(pluginName, out var state)) return;
        state.TranslationTarget = false;
        state.Enabled = false;
        SaveConfig();

        var next = GetPluginConfigSnapshot().Where(x => x.Value.TranslationTarget).Select(x => x.Key)
            .OrderBy(x => PluginSortKey(x)).ThenBy(x => x, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(next)) selectedPlugin = capturePlugin = next;
        csvStatus = $"{pluginName} を翻訳対象から外しました。辞書データ自体は削除していません。";
    }

    private void DrawPluginDropdown(string id, string[] pluginNames, ref string selected, bool disabled)
    {
        ImGui.SetNextItemWidth(240f);
        if (disabled) ImGui.BeginDisabled();

        var previewEnabled = !string.IsNullOrWhiteSpace(selected)
            && config.Plugins.TryGetValue(selected, out var previewState)
            && previewState.Enabled;
        if (previewEnabled) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.35f, 0.95f, 0.55f, 1.00f));

        var opened = ImGui.BeginCombo(id, string.IsNullOrWhiteSpace(selected) ? "選択してください" : selected);

        if (previewEnabled) ImGui.PopStyleColor();

        if (opened)
        {
            foreach (var name in pluginNames)
            {
                var isSelected = string.Equals(selected, name, StringComparison.Ordinal);
                var isEnabled = config.Plugins.TryGetValue(name, out var state) && state.Enabled;
                if (isEnabled) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.35f, 0.95f, 0.55f, 1.00f));
                if (ImGui.Selectable($"{name}##{id}_{name}", isSelected)) selected = name;
                if (isEnabled) ImGui.PopStyleColor();
                if (isSelected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        if (disabled) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("日本語化が有効なプラグインは緑色で表示します。");
    }

    // v0.4.9:
    // 別 Begin() された独立ウィンドウを任意の翻訳対象へ関連付ける。
    // 「検出テストモード」は開始時点のウィンドウ一覧を記録し、その後に新しく現れた
    // ウィンドウだけを候補として表示する。Pawprint / Omni 等の固有名は決め打ちしない。
    private static string NormalizeDetectedWindowKeyword(string value)
    {
        value = (value ?? string.Empty).Trim();
        var marker = value.IndexOf("###", StringComparison.Ordinal);
        if (marker >= 0 && marker + 3 < value.Length)
            value = value[(marker + 3)..].Trim();
        return value;
    }

    private static string DescribeDetectedWindow(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var marker = value.IndexOf("###", StringComparison.Ordinal);
        if (marker >= 0 && marker + 3 < value.Length)
        {
            var visible = value[..marker].Trim();
            var internalId = value[(marker + 3)..].Trim();
            if (!string.IsNullOrWhiteSpace(visible) && !string.IsNullOrWhiteSpace(internalId))
                return $"{visible}  [内部ID: {internalId}]";
        }
        return value;
    }

    private void DrawAdditionalWindowControls(string pluginName, PluginDictionaryState state)
    {
        if (!ImGui.CollapsingHeader("別ウィンドウの関連付け")) return;

        ImGui.TextWrapped("メイン画面とは別の独立ウィンドウを使うプラグイン向けです。関連付けたウィンドウでも、このプラグインの翻訳辞書を使用します。");
        ImGui.TextDisabled("Popup / Combo / Tooltip は通常、自動で親ウィンドウを引き継ぎます。ここでは完全に別 Begin() されたウィンドウだけを追加してください。");

        var keywords = (state.WindowKeyword ?? string.Empty)
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (keywords.Count == 0)
        {
            ImGui.TextDisabled("関連付け済みウィンドウ名：なし");
        }
        else
        {
            ImGui.TextUnformatted("関連付け済み：");
            for (var i = 0; i < keywords.Count; i++)
            {
                ImGui.PushID($"windowKeyword_{pluginName}_{i}");
                var keyword = keywords[i];
                ImGui.BulletText(keyword);

                var sourceLabel = GetDictionaryWindowSourceLabel(state, keyword);
                if (!string.IsNullOrWhiteSpace(sourceLabel))
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled($"[{sourceLabel}から自動適用]");
                }

                ImGui.SameLine();
                if (ActionButton("削除", ButtonRole.Danger))
                {
                    // 辞書由来の場合は抑止リストへ記録し、
                    // 辞書更新・再読込で勝手に復活しないようにする。
                    if (!string.IsNullOrWhiteSpace(sourceLabel))
                        state.SuppressedDictionaryWindowKeywords.Add(keyword);

                    keywords.RemoveAt(i);
                    state.WindowKeyword = string.Join("|", keywords);
                    SaveConfig();
                    ImGui.PopID();
                    break;
                }
                ImGui.PopID();
            }
        }

        ImGui.Separator();
        ImGui.TextUnformatted("検出テストモード");

        var detectingThisPlugin = additionalWindowDetectMode &&
                                  string.Equals(additionalWindowDetectPlugin, pluginName, StringComparison.OrdinalIgnoreCase);

        if (!detectingThisPlugin)
        {
            if (additionalWindowDetectMode) ImGui.BeginDisabled();
            if (ActionButton("検出開始", ButtonRole.Primary))
            {
                additionalWindowDetectMode = true;
                additionalWindowDetectPlugin = pluginName;
                additionalWindowDetectStartedAt = Environment.TickCount64;
                additionalWindowDetectBaseline = GetLiveDetectedWindows().ToHashSet(StringComparer.Ordinal);
                additionalWindowNewSelection = string.Empty;
            }
            if (additionalWindowDetectMode) ImGui.EndDisabled();

            ImGui.SameLine();
            ImGui.TextDisabled("押したあと、翻訳したい別ウィンドウをゲーム内で開いてください。");
        }
        else
        {
            var elapsed = Math.Max(0, (Environment.TickCount64 - additionalWindowDetectStartedAt) / 1000);
            ImGui.TextWrapped($"検出中… {elapsed}秒　対象の別ウィンドウを開いてください。開始時から開いていた場合は、一度閉じて約0.5秒待ってから開き直してください。");
            ImGui.SameLine();
            if (ActionButton("検出終了", ButtonRole.Danger))
            {
                additionalWindowDetectMode = false;
                additionalWindowDetectPlugin = string.Empty;
                additionalWindowDetectBaseline.Clear();
                additionalWindowNewSelection = string.Empty;
            }

            // v0.4.9:
            // 検出開始時に開いていたウィンドウも、一度閉じたら基準リストから外す。
            // これにより「検出開始時に開いていた → 閉じる → 再度開く」でも
            // 再オープン時に新規ウィンドウとして検出できる。
            var liveNow = GetLiveDetectedWindows()
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.Ordinal);

            additionalWindowDetectBaseline.RemoveWhere(x => !liveNow.Contains(x));

            var newlyDetected = liveNow
                .Where(x => !additionalWindowDetectBaseline.Contains(x))
                .Where(x => !IsTransientImGuiWindow(x))
                .Where(x => !x.Contains("Plugin JP Helper", StringComparison.OrdinalIgnoreCase))
                .Where(x => showInternalDetectedWindows || !IsNoisyInternalDetectedWindow(x))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (newlyDetected.Length == 0)
            {
                ImGui.TextDisabled("新しい独立ウィンドウはまだ検出されていません。");
            }
            else
            {
                if (!newlyDetected.Contains(additionalWindowNewSelection, StringComparer.Ordinal))
                    additionalWindowNewSelection = newlyDetected[0];

                ImGui.TextUnformatted($"新しく検出：{newlyDetected.Length}件");
                ImGui.SetNextItemWidth(520);
                var preview = DescribeDetectedWindow(additionalWindowNewSelection);
                if (ImGui.BeginCombo($"##newDetectedWindow_{pluginName}", preview))
                {
                    foreach (var item in newlyDetected)
                    {
                        var selected = string.Equals(item, additionalWindowNewSelection, StringComparison.Ordinal);
                        if (ImGui.Selectable($"{DescribeDetectedWindow(item)}##newDetected_{item}", selected))
                            additionalWindowNewSelection = item;
                        if (selected) ImGui.SetItemDefaultFocus();
                    }
                    ImGui.EndCombo();
                }

                var normalized = NormalizeDetectedWindowKeyword(additionalWindowNewSelection);
                ImGui.TextDisabled($"追加時登録名：{normalized}");

                var alreadyAddedNew = keywords.Contains(normalized, StringComparer.OrdinalIgnoreCase);
                if (alreadyAddedNew || string.IsNullOrWhiteSpace(normalized)) ImGui.BeginDisabled();
                if (ActionButton("この新規ウィンドウを関連付け", ButtonRole.Success))
                {
                    state.SuppressedDictionaryWindowKeywords.Remove(normalized);
                    state.SuppressedDictionaryWindowKeywords.Remove(normalized);
                    keywords.Add(normalized);
                    state.WindowKeyword = string.Join("|", keywords.Distinct(StringComparer.OrdinalIgnoreCase));
                    SaveConfig();

                    // 追加後も検出は続けられる。現在までに見えたウィンドウを基準へ吸収し、
                    // 次に開く別ウィンドウだけを探せるようにする。
                    additionalWindowDetectBaseline = GetLiveDetectedWindows().ToHashSet(StringComparer.Ordinal);
                    additionalWindowNewSelection = string.Empty;
                }
                if (alreadyAddedNew || string.IsNullOrWhiteSpace(normalized)) ImGui.EndDisabled();
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("手動追加");
        ImGui.SetNextItemWidth(300);
        ImGui.InputTextWithHint($"##additionalWindowKeyword_{pluginName}", "ウィンドウ名 / 内部ID", ref additionalWindowKeywordInput, 128);
        ImGui.SameLine();
        var manual = additionalWindowKeywordInput.Trim();
        if (string.IsNullOrWhiteSpace(manual)) ImGui.BeginDisabled();
        if (ActionButton("追加##additionalWindowKeywordAdd", ButtonRole.Primary))
        {
            var normalized = NormalizeDetectedWindowKeyword(manual);
            if (!string.IsNullOrWhiteSpace(normalized) && !keywords.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                state.SuppressedDictionaryWindowKeywords.Remove(normalized);
                keywords.Add(normalized);
            state.WindowKeyword = string.Join("|", keywords);
            additionalWindowKeywordInput = string.Empty;
            SaveConfig();
        }
        if (string.IsNullOrWhiteSpace(manual)) ImGui.EndDisabled();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("現在表示中のウィンドウ");
        ImGui.TextDisabled("この一覧はリアルタイムです。対象ウィンドウを閉じると自動で消え、再度開くと戻ります。");

        ImGui.SetNextItemWidth(360);
        ImGui.InputTextWithHint($"##windowSearch_{pluginName}", "検索（表示名 / 内部ID）", ref additionalWindowSearch, 128);
        ImGui.SameLine();
        if (ActionButton("クリア##windowSearchClear", ButtonRole.Neutral))
            additionalWindowSearch = string.Empty;

        ImGui.SameLine();
        ImGui.Checkbox("内部UIも表示", ref showInternalDetectedWindows);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("UmbraのToolbar/AuxBarなど、画面部品として大量生成される内部ImGui Windowも表示します。");

        var search = additionalWindowSearch.Trim();
        var liveDetected = GetLiveDetectedWindows()
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Where(x => !IsTransientImGuiWindow(x))
            .Where(x => !x.Contains("Plugin JP Helper", StringComparison.OrdinalIgnoreCase))
            .Where(x => showInternalDetectedWindows || !IsNoisyInternalDetectedWindow(x))
            .Where(x => string.IsNullOrWhiteSpace(search)
                     || x.Contains(search, StringComparison.OrdinalIgnoreCase)
                     || DescribeDetectedWindow(x).Contains(search, StringComparison.OrdinalIgnoreCase)
                     || NormalizeDetectedWindowKeyword(x).Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToArray();

        if (liveDetected.Length == 0)
        {
            ImGui.TextDisabled(string.IsNullOrWhiteSpace(search)
                ? "現在表示中の対象ウィンドウはありません。"
                : "現在表示中で検索条件に一致するウィンドウはありません。");
            return;
        }

        ImGui.TextDisabled($"現在表示中：{liveDetected.Length}件（最大100件表示）");

        var childHeight = Math.Min(320f, 30f + liveDetected.Length * 27f);
        if (ImGui.BeginChild($"##windowSearchResults_{pluginName}", new Vector2(0, childHeight), true))
        {
            foreach (var raw in liveDetected)
            {
                var normalized = NormalizeDetectedWindowKeyword(raw);
                var alreadyAdded = keywords.Contains(normalized, StringComparer.OrdinalIgnoreCase);

                ImGui.PushID($"windowResult_{pluginName}_{raw}");

                if (alreadyAdded) ImGui.BeginDisabled();
                if (ActionButton(alreadyAdded ? "追加済み" : "追加", alreadyAdded ? ButtonRole.Neutral : ButtonRole.Success))
                {
                    state.SuppressedDictionaryWindowKeywords.Remove(normalized);
                    state.SuppressedDictionaryWindowKeywords.Remove(normalized);
                    keywords.Add(normalized);
                    state.WindowKeyword = string.Join("|", keywords.Distinct(StringComparer.OrdinalIgnoreCase));
                    SaveConfig();
                }
                if (alreadyAdded) ImGui.EndDisabled();

                ImGui.SameLine();
                ImGui.TextUnformatted(DescribeDetectedWindow(raw));

                if (!string.Equals(raw, normalized, StringComparison.Ordinal))
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled($"→ 追加時登録名: {normalized}");
                }

                ImGui.PopID();
            }
        }
        ImGui.EndChild();
    }

    private void DrawOpenCommandControls(string pluginName, PluginDictionaryState state)
    {
        if (!openCommandBuffers.TryGetValue(pluginName, out var commandBuffer))
        {
            commandBuffer = state.OpenCommand ?? string.Empty;
            openCommandBuffers[pluginName] = commandBuffer;
        }

        ImGui.TextUnformatted("開くコマンド：");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(240f);
        if (ImGui.InputTextWithHint($"##openCommand_{pluginName}", "/command", ref commandBuffer, 256))
            openCommandBuffers[pluginName] = commandBuffer;

        ImGui.SameLine();
        if (ActionButton("コマンドを保存", ButtonRole.Primary))
        {
            var trimmed = commandBuffer.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                state.OpenCommand = string.Empty;
                openCommandBuffers[pluginName] = string.Empty;
                SaveConfig();
                csvStatus = $"{pluginName} の開くコマンドを削除しました。";
            }
            else if (!trimmed.StartsWith("/", StringComparison.Ordinal))
            {
                csvStatus = "開くコマンドは / から始まるコマンドを入力してください。";
            }
            else
            {
                state.OpenCommand = trimmed;
                openCommandBuffers[pluginName] = trimmed;
                SaveConfig();
                csvStatus = $"{pluginName} の開くコマンドを保存しました: {trimmed}";
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("このプラグインを開くためのコマンドを保存します。プラグインごとに個別保存されます。");

        ImGui.SameLine();
        var canOpen = !string.IsNullOrWhiteSpace(state.OpenCommand);
        if (!canOpen) ImGui.BeginDisabled();
        if (ActionButton("開く", ButtonRole.Success) && canOpen)
        {
            try
            {
                var savedCommand = state.OpenCommand ?? string.Empty;
                commandManager.ProcessCommand(savedCommand);
                csvStatus = $"{pluginName} を開くコマンドを実行しました: {savedCommand}";
            }
            catch (Exception ex)
            {
                log.Warning(ex, "[PluginJPHelper] Failed to execute open command for {Plugin}", pluginName);
                csvStatus = $"コマンドの実行に失敗しました: {state.OpenCommand}";
            }
        }
        if (!canOpen) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(canOpen
                ? $"保存済みコマンドを実行します: {state.OpenCommand}"
                : "先に開くコマンドを入力して「コマンドを保存」を押してください。");
    }

    private void EnsureOfficialNoticeRefresh()
    {
        // 自動確認はDalamudのrepo読込と競合させない。
        if (!pluginInstallerModule.IsSafeForBackgroundNetworkWork()) return;
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref officialNoticeLastAttemptTick);
        if (officialNoticeBusy || (last != 0 && now - last < UpdateCheckIntervalMs)) return;
        _ = RefreshOfficialNoticeAsync();
    }

    private Task RefreshOfficialNoticeAsync()
    {
        if (officialNoticeBusy) return Task.CompletedTask;
        officialNoticeBusy = true;
        Interlocked.Exchange(ref officialNoticeLastAttemptTick, Environment.TickCount64);

        return Task.Run(() =>
        {
            try
            {
                // 公式辞書の更新判定は notice.txt ではなく Dictionaries/Official 配下のCSV構成で行う。
                using var dirReq = new HttpRequestMessage(HttpMethod.Get, OfficialDictionaryApiUrl);
                dirReq.Headers.UserAgent.ParseAdd("PluginJPHelper/0.4.4");
                dirReq.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var dirRes = officialDictionaryHttp.SendAsync(dirReq).GetAwaiter().GetResult();
                dirRes.EnsureSuccessStatusCode();
                var dirJson = dirRes.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                using var dirDoc = JsonDocument.Parse(dirJson);

                var parts = new List<string>();
                foreach (var item in dirDoc.RootElement.EnumerateArray())
                {
                    if (!item.TryGetProperty("type", out var type) || type.GetString() != "file") continue;
                    var name = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;
                    if (!name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) continue;
                    var sha = item.TryGetProperty("sha", out var shaEl) ? shaEl.GetString() ?? string.Empty : string.Empty;
                    parts.Add($"{name}:{sha}");
                }
                parts.Sort(StringComparer.OrdinalIgnoreCase);
                var signatureSource = string.Join("|", parts);
                var latestSignature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signatureSource)));

                // 表示文は notice.txt の1行目を利用する。フォルダー更新が無い限り通知は出さない。
                using var req = new HttpRequestMessage(HttpMethod.Get, OfficialNoticeUrl);
                req.Headers.UserAgent.ParseAdd("PluginJPHelper/0.4.4");
                using var res = officialDictionaryHttp.SendAsync(req).GetAwaiter().GetResult();
                res.EnsureSuccessStatusCode();
                var text = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                var firstLine = text.Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Replace('\r', '\n')
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault()?.Trim() ?? "公式辞書に更新があります。";

                officialNoticeSha = latestSignature;
                officialNoticeText = !string.IsNullOrWhiteSpace(latestSignature)
                    && !string.Equals(latestSignature, config.LastAcknowledgedOfficialNoticeSha ?? string.Empty, StringComparison.Ordinal)
                        ? firstLine
                        : string.Empty;
            }
            catch (Exception ex)
            {
                // 通知取得失敗は翻訳機能を止めない。タイトルには何も追加しない。
                log.Debug(ex, "[PluginJPHelper] official dictionary update check failed");
            }
            finally
            {
                officialNoticeBusy = false;
            }
        });
    }

    private string BuildMainWindowTitle()
    {
        const string baseTitle = "Plugin JP Helper v0.5.0";
        var officialNotice = officialNoticeText?.Trim() ?? string.Empty;
        var communityNotice = communityNoticePending ? "コミュニティ辞書に更新があります。" : string.Empty;
        var notice = string.Empty;
        if (officialNotice.Length > 0)
            notice += $"【{officialNotice}】";
        if (communityNotice.Length > 0)
            notice += $"【{communityNotice}】";
        if (notice.Length == 0) return baseTitle + "###PluginJPHelperMain";

        // ImGuiの標準タイトルバー内に収まるよう、通知を一定幅で横スクロールさせる。
        // ###以降を固定IDにすることで、表示文字が動いてもウィンドウ状態は維持される。
        const int visibleChars = 34;
        const string gap = "　　　　";
        var loop = notice + gap;
        if (loop.Length <= visibleChars)
            return $"{baseTitle}     {notice}###PluginJPHelperMain";

        var start = (int)((Environment.TickCount64 / 220) % loop.Length);
        var doubled = loop + loop;
        var segment = doubled.Substring(start, Math.Min(visibleChars, doubled.Length - start));
        if (segment.Length < visibleChars)
            segment += doubled.Substring(0, visibleChars - segment.Length);
        return $"{baseTitle}     {segment}###PluginJPHelperMain";
    }

    private sealed class OfficialDictionaryEntry
    {
        public string Name { get; set; } = string.Empty;
        public string Sha { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
        public string PluginName { get; set; } = string.Empty;
    }

    private static string GetOfficialDictionaryPluginName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var versionMarker = stem.LastIndexOf("_v", StringComparison.OrdinalIgnoreCase);
        if (versionMarker > 0)
        {
            var version = stem[(versionMarker + 2)..];
            if (version.Length > 0 && version.All(c => char.IsDigit(c) || c == '.'))
                return stem[..versionMarker];
        }
        return stem;
    }

    private bool TryGetInstalledPluginState(string pluginName, out bool isLoaded)
    {
        static string Normalize(string value)
            => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        var aliases = pluginName.ToUpperInvariant() switch
        {
            "BMR" => new[] { "BMR", "BossModReborn" },
            "BM" => new[] { "BM", "BossMod" },
            "RSR" => new[] { "RSR", "RotationSolverReborn", "Rotation Solver Reborn" },
            _ => new[] { pluginName },
        };

        var installed = pluginInterface.InstalledPlugins.FirstOrDefault(p =>
            aliases.Any(alias =>
                string.Equals(alias, p.InternalName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(alias, p.Name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Normalize(alias), Normalize(p.InternalName ?? string.Empty), StringComparison.Ordinal)
                || string.Equals(Normalize(alias), Normalize(p.Name ?? string.Empty), StringComparison.Ordinal)));

        if (installed == null)
        {
            isLoaded = false;
            return false;
        }

        isLoaded = installed.IsLoaded;
        return true;
    }

    private IReadOnlyList<PluginDictionaryState> FindRegisteredPluginStates(string pluginName)
    {
        var matches = new List<PluginDictionaryState>();
        foreach (var (registeredName, state) in GetPluginConfigSnapshot())
        {
            if (state == null) continue;

            var matched = PluginIdentityMatches(registeredName, pluginName);
            if (!matched)
            {
                var keywords = (state.WindowKeyword ?? string.Empty)
                    .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                matched = keywords.Any(keyword => PluginIdentityMatches(keyword, pluginName));
            }

            if (matched && !matches.Contains(state)) matches.Add(state);
        }
        return matches;
    }

    private (string TranslationRegistered, string CaptureRegistered, string TranslationEnabled, string Installed, string PluginSwitch) GetPluginStatusColumns(string pluginName)
    {
        // 同一プラグインが略称・InternalName・旧登録名など複数のキーで残っていても、
        // 最初の1件だけで判定せず、一致した全設定を集約する。
        var states = FindRegisteredPluginStates(pluginName);
        var translationRegistered = states.Any(state => state.TranslationTarget) ? "有り" : "無し";

        // 現行v0.3.4では未翻訳取得の対象登録も TranslationTarget を共用している。
        // そのため未翻訳登録も一致した全設定の TranslationTarget を集約して表示する。
        var captureRegistered = states.Any(state => state.TranslationTarget) ? "有り" : "無し";
        var translationEnabled = states.Any(state => state.TranslationTarget && state.Enabled) ? "ON" : "OFF";

        if (!TryGetInstalledPluginState(pluginName, out var isLoaded))
            return (translationRegistered, captureRegistered, translationEnabled, "未導入", "OFF");

        return (translationRegistered, captureRegistered, translationEnabled, "導入済み", isLoaded ? "ON" : "OFF");
    }

    private static void DrawYesNoStatus(string value)
    {
        if (value == "有り") ImGui.TextColored(new Vector4(0.35f, 0.85f, 0.50f, 1f), value);
        else ImGui.TextDisabled(value);
    }

    private static void DrawOnOffStatus(string value)
    {
        if (value == "ON") ImGui.TextColored(new Vector4(0.35f, 0.85f, 0.50f, 1f), value);
        else ImGui.TextColored(new Vector4(1f, 0.75f, 0.20f, 1f), value);
    }

    private static void DrawInstalledStatus(string value)
    {
        if (value == "導入済み") ImGui.TextColored(new Vector4(0.35f, 0.85f, 0.50f, 1f), value);
        else ImGui.TextDisabled(value);
    }

    private string OfficialDictionaryDirectory()
    {
        var dir = Path.Combine(DictionaryDirectory(), "Official");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string OfficialCsvPath(string fileName) => Path.Combine(OfficialDictionaryDirectory(), fileName);
    private string OfficialShaPath(string fileName) => OfficialCsvPath(fileName) + ".sha";

    private static bool IsDictionaryWindowMetaRow(IReadOnlyList<string> cols)
        => cols.Count >= 6
           && string.Equals(cols[3]?.Trim(), DictionaryWindowMetaType, StringComparison.OrdinalIgnoreCase)
           && !string.IsNullOrWhiteSpace(cols[4]);

    private static List<string> ExtractDictionaryWindowKeywords(List<List<string>> rows)
        => rows.Skip(1)
            .Where(IsDictionaryWindowMetaRow)
            .Select(cols => NormalizeDetectedWindowKeyword(cols[4]))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void ApplyDictionaryWindowMetadata(
        string pluginName,
        PluginDictionaryState state,
        IEnumerable<string> keywords,
        string sourceLabel)
    {
        state.DictionaryWindowKeywordSources ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        state.SuppressedDictionaryWindowKeywords ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var active = (state.WindowKeyword ?? string.Empty)
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var changed = false;
        foreach (var raw in keywords)
        {
            var keyword = NormalizeDetectedWindowKeyword(raw);
            if (string.IsNullOrWhiteSpace(keyword)) continue;

            state.DictionaryWindowKeywordSources[keyword] = sourceLabel;

            // ユーザーが明示的に解除した辞書由来関連付けは、自動復活させない。
            if (state.SuppressedDictionaryWindowKeywords.Contains(keyword))
                continue;

            if (!active.Contains(keyword, StringComparer.OrdinalIgnoreCase))
            {
                active.Add(keyword);
                changed = true;
            }
        }

        if (changed)
            state.WindowKeyword = string.Join("|", active);
    }

    private string GetDictionaryWindowSourceLabel(PluginDictionaryState state, string keyword)
    {
        if (state.DictionaryWindowKeywordSources != null
            && state.DictionaryWindowKeywordSources.TryGetValue(keyword, out var source)
            && !string.IsNullOrWhiteSpace(source))
            return source;
        return string.Empty;
    }

    private List<string> GetDictionaryWindowMetadataFromCsvText(string csvText)
    {
        try
        {
            var rows = ParseCsvRecords(csvText);
            return ExtractDictionaryWindowKeywords(rows);
        }
        catch
        {
            return new List<string>();
        }
    }

    private void LoadInstalledOfficialDictionaries()
    {
        try
        {
            foreach (var path in Directory.GetFiles(OfficialDictionaryDirectory(), "*.csv", SearchOption.TopDirectoryOnly))
                LoadOfficialDictionaryFile(path);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[PluginJPHelper] failed to load installed official dictionaries");
        }
    }

    private string LoadOfficialDictionaryFile(string path)
    {
        var csvText = File.ReadAllText(path, Encoding.UTF8);
        var rows = ParseCsvRecords(csvText);
        if (rows.Count < 2) return $"公式辞書にデータがありません: {Path.GetFileName(path)}";

        var pluginName = rows.Skip(1).FirstOrDefault(r => r.Count >= 6 && !string.IsNullOrWhiteSpace(r[0]))?[0]?.Trim();
        if (string.IsNullOrWhiteSpace(pluginName)) pluginName = Path.GetFileNameWithoutExtension(path);

        if (!config.Plugins.TryGetValue(pluginName, out var state) || state == null)
        {
            state = new PluginDictionaryState { Enabled = true, TranslationTarget = true, WindowKeyword = pluginName };
            lock (pluginConfigSync)
            config.Plugins[pluginName] = state;
            EnsureCaptureDictionary(pluginName);
        }

        var officialWindows = ExtractDictionaryWindowKeywords(rows);
        ApplyDictionaryWindowMetadata(pluginName, state, officialWindows, "公式辞書");

        state.OfficialOverrides.Clear();
        var imported = 0;
        foreach (var cols in rows.Skip(1))
        {
            if (cols.Count < 6) continue;
            if (IsDictionaryWindowMetaRow(cols)) continue;
            var english = cols[4];
            var japanese = cols[5];
            if (string.IsNullOrWhiteSpace(english) || string.IsNullOrWhiteSpace(japanese)) continue;
            state.OfficialOverrides[english] = japanese;
            if (!string.IsNullOrWhiteSpace(cols[1]) || !string.IsNullOrWhiteSpace(cols[2]))
                state.Locations[english] = new DictionaryLocation { Menu = cols[1], Section = cols[2] };
            imported++;
        }
        state.TranslationTarget = true;
        InvalidateDictionaryUiCache();
        SaveConfig();
        return $"{pluginName}: 公式辞書 {imported}件を読み込みました。";
    }

    private Task RefreshOfficialDictionariesAsync()
    {
        if (officialDictionaryBusy) return Task.CompletedTask;
        officialDictionaryBusy = true;
        officialDictionaryStatus = "GitHubの公式辞書一覧を確認しています...";

        return Task.Run(() =>
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, OfficialDictionaryApiUrl);
                req.Headers.UserAgent.ParseAdd("PluginJPHelper/0.4.4");
                req.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var res = officialDictionaryHttp.SendAsync(req).GetAwaiter().GetResult();
                res.EnsureSuccessStatusCode();
                var json = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(json);
                var list = new List<OfficialDictionaryEntry>();
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (!item.TryGetProperty("type", out var type) || type.GetString() != "file") continue;
                    var name = item.GetProperty("name").GetString() ?? string.Empty;
                    if (!name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) continue;
                    var sha = item.GetProperty("sha").GetString() ?? string.Empty;
                    var url = item.TryGetProperty("download_url", out var du) ? du.GetString() ?? string.Empty : string.Empty;
                    list.Add(new OfficialDictionaryEntry
                    {
                        Name = name,
                        Sha = sha,
                        DownloadUrl = url,
                        PluginName = GetOfficialDictionaryPluginName(name),
                    });
                }
                lock (officialDictionaryLock)
                    officialDictionaryEntries = list.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
                officialDictionaryStatus = $"公式辞書 {list.Count}件を確認しました。";
                var noticeToAcknowledge = officialNoticeText?.Trim() ?? string.Empty;
                var folderSignatureToAcknowledge = officialNoticeSha?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(folderSignatureToAcknowledge))
                {
                    officialNoticeText = string.Empty;
                    // SaveConfig() は UI キャッシュを Clear() するため描画スレッドで行う。
                    RunOnMainThread(() =>
                    {
                        // Officialフォルダー内CSVの構成署名を確認済みとして保存する。
                        config.LastAcknowledgedOfficialNotice = noticeToAcknowledge;
                        config.LastAcknowledgedOfficialNoticeSha = folderSignatureToAcknowledge;
                        SaveConfig();
                    });
                }
            }
            catch (Exception ex)
            {
                officialDictionaryStatus = $"公式辞書一覧の取得に失敗しました: {ex.Message}";
                log.Warning(ex, "[PluginJPHelper] official dictionary list failed");
            }
            finally
            {
                officialDictionaryBusy = false;
            }
        });
    }

    private Task DownloadSelectedOfficialDictionariesAsync()
    {
        if (officialDictionaryBusy) return Task.CompletedTask;
        List<OfficialDictionaryEntry> targets;
        lock (officialDictionaryLock)
            targets = officialDictionaryEntries.Where(x => selectedOfficialDictionaries.Contains(x.Name)).ToList();
        if (targets.Count == 0)
        {
            officialDictionaryStatus = "ダウンロードする公式辞書を選択してください。";
            return Task.CompletedTask;
        }

        officialDictionaryBusy = true;
        return Task.Run(() =>
        {
            try
            {
                var completed = 0;
                foreach (var item in targets)
                {
                    officialDictionaryStatus = $"{item.Name} をダウンロードしています...";
                    if (string.IsNullOrWhiteSpace(item.DownloadUrl)) continue;
                    using var req = new HttpRequestMessage(HttpMethod.Get, item.DownloadUrl);
                    req.Headers.UserAgent.ParseAdd("PluginJPHelper/0.4.4");
                    using var res = officialDictionaryHttp.SendAsync(req).GetAwaiter().GetResult();
                    res.EnsureSuccessStatusCode();
                    var text = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    var path = OfficialCsvPath(item.Name);
                    File.WriteAllText(path, text, new UTF8Encoding(true));
                    File.WriteAllText(OfficialShaPath(item.Name), item.Sha, Encoding.UTF8);

                    // CSV のパースまでは背景スレッドで行ってよい（共有状態に触れないため）。
                    List<List<string>>? downloadedRows = null;
                    string? downloadedPluginName = null;
                    try
                    {
                        downloadedRows = ParseCsvRecords(text);
                        downloadedPluginName = downloadedRows.Skip(1)
                            .FirstOrDefault(r => r.Count >= 6 && !string.IsNullOrWhiteSpace(r[0]))?[0]?.Trim();
                    }
                    catch { }

                    // 設定と辞書への適用は描画スレッドで行う。
                    // ここで直接 OfficialOverrides を Clear() すると、翻訳フックが
                    // 同じ辞書を読んでいる最中に作り替えることになる。
                    var applyRows = downloadedRows;
                    var applyPluginName = downloadedPluginName;
                    RunOnMainThread(() =>
                    {
                        // 公式辞書の明示ダウンロード/更新時は、その辞書に含まれる @Window の
                        // 抑止を解除してから読み込む。起動時の自動読込では抑止を維持する。
                        if (applyRows != null
                            && !string.IsNullOrWhiteSpace(applyPluginName)
                            && config.Plugins.TryGetValue(applyPluginName, out var downloadedState)
                            && downloadedState != null)
                        {
                            downloadedState.SuppressedDictionaryWindowKeywords ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var keyword in ExtractDictionaryWindowKeywords(applyRows))
                                downloadedState.SuppressedDictionaryWindowKeywords.Remove(keyword);
                        }

                        // 公式辞書は保存直後に読み込み、@Windowメタ情報も自動適用。
                        LoadOfficialDictionaryFile(path);
                    });
                    completed++;
                }
                officialDictionaryStatus = $"選択した公式辞書 {completed}件をダウンロード／更新し、辞書と別ウィンドウ設定を自動適用しました。";
            }
            catch (Exception ex)
            {
                officialDictionaryStatus = $"公式辞書のダウンロードに失敗しました: {ex.Message}";
                log.Warning(ex, "[PluginJPHelper] official dictionary download failed");
            }
            finally
            {
                officialDictionaryBusy = false;
            }
        });
    }

    private string GetOfficialDictionaryState(OfficialDictionaryEntry item)
    {
        var path = OfficialCsvPath(item.Name);
        if (!File.Exists(path)) return "未導入";
        var shaPath = OfficialShaPath(item.Name);
        if (!File.Exists(shaPath)) return "導入済み";
        try
        {
            var localSha = File.ReadAllText(shaPath, Encoding.UTF8).Trim();
            return string.Equals(localSha, item.Sha, StringComparison.Ordinal) ? "最新版" : "更新あり";
        }
        catch { return "導入済み"; }
    }

    private void DrawHelpTab()
    {
        ImGui.TextWrapped("Plugin JP Helper は、他のDalamudプラグインの表示文字列をCSV辞書で日本語化する補助プラグインです。");
        ImGui.Spacing();

        if (ImGui.CollapsingHeader("Plugin Installerの日本語化：使い方・仕様"))
        {
            ImGui.TextWrapped("DalamudのPlugin Installerに表示される公開プラグインの説明文を、専用JSON辞書を使って日本語化します。Plugin本体を書き換える機能ではなく、表示だけを日本語へ差し替えます。");
            ImGui.Spacing();

            ImGui.TextUnformatted("【基本的な使い方】");
            ImGui.BulletText("「Plugin Installer」タブを開きます。");
            ImGui.BulletText("「Plugin Installerの説明を辞書で日本語化」をONにします。");
            ImGui.BulletText("翻訳辞書に登録済みのプラグインは、Plugin Installerの説明文が日本語で表示されます。");
            ImGui.BulletText("「翻訳の下に原文も表示」をONにすると、日本語の下に元の英文も表示できます。");
            ImGui.BulletText("日本語化のチェックをOFFにすると、その場で元の英文表示へ戻ります。保存済みの翻訳辞書は削除されません。");
            ImGui.TextWrapped("※ 再度ONにすると、保存済みの翻訳をそのまま使用します。");
            ImGui.Spacing();

            ImGui.TextUnformatted("【日本語検索】");
            ImGui.BulletText("Plugin Installerの検索は本来英語が中心ですが、PJHの検索欄へ日本語を入力すると、対応する用語を英語へ置き換えてDalamudへ渡します。");
            ImGui.BulletText("例：手配書、討伐手帳、製作、リテイナーなど、登録済みの検索用語に対応します。");
            ImGui.TextWrapped("※ 日本語検索は辞書に登録された用語を使う補助機能です。すべての日本語を自由文として理解して検索する機能ではありません。");
            ImGui.Spacing();

            ImGui.TextUnformatted("【翻訳の保存と差分更新】");
            ImGui.BulletText("一度翻訳したPlugin Installerの説明は plugin-installer-translations.json に保存します。");
            ImGui.BulletText("次回以降は毎回すべてを翻訳せず、新しく追加されたプラグイン、または原文が変更された部分だけを差分として確認します。");
            ImGui.BulletText("既に手動で確認・編集した辞書項目は優先して保護し、Google翻訳で自動上書きしません。");
            ImGui.TextWrapped("「今すぐ差分チェック」を押すと、設定した待ち時間を待たずに差分確認を行います。");
            ImGui.Spacing();

            ImGui.TextUnformatted("【Google翻訳とPJH中継サーバー】");
            ImGui.BulletText("翻訳方式で『PJH中継サーバー』を選んでいても、Google翻訳が利用可能な場合はGoogle翻訳を優先します。");
            ImGui.BulletText("Google翻訳を利用できない場合はPJH中継サーバーへ切り替えます。画面には実際に使用している翻訳先を表示します。");
            ImGui.BulletText("PJH中継サーバーは利用者側のAPIキー設定は不要です。中継サーバー側には月間45万文字の安全上限があります。");
            ImGui.BulletText("『利用可能チェック』は手動確認用です。Googleは確認用通信を1回行い、中継サーバーは状態確認のみで翻訳文字数を消費しません。");
            ImGui.BulletText("翻訳サービスへの自動再試行は行いません。差分チェックだけでは外部翻訳へ送信しません。");
            ImGui.Spacing();

            ImGui.TextUnformatted("【Google翻訳とFFXIV用語】");
            ImGui.BulletText("未翻訳・変更分の説明文は、設定した翻訳方式（Google翻訳 / PJH中継サーバー）を利用して日本語化します。");
            ImGui.BulletText("FFXIV/Dalamud固有用語や保護対象の文字列は、Googleへ送る前に一時的な目印へ置き換えます。");
            ImGui.BulletText("翻訳後、その目印の位置へPJHの用語辞書に登録したFFXIV表記を戻します。");
            ImGui.TextWrapped("この方式により、Google翻訳後の文章から『どの日本語が元のFFXIV用語だったか』を推測して置換するのではなく、翻訳前から場所を確保して用語を戻します。");
            ImGui.Spacing();

            ImGui.TextUnformatted("【Google側の制限について】");
            ImGui.BulletText("短時間に多数の翻訳が必要になった場合、Google側の制限で一時的に翻訳できないことがあります。");
            ImGui.BulletText("制限中は状態欄に表示されます。すぐに何度も再実行せず、少し時間を空けてから差分チェックしてください。");
            ImGui.BulletText("翻訳済みの内容は保存されているため、再確認時も未翻訳・変更分だけが対象になります。");
            ImGui.Spacing();

            ImGui.TextUnformatted("【プライバシー・対象範囲】");
            ImGui.BulletText("差分確認とGoogle翻訳の対象は、Dalamudで公開されているRemote Manifestのプラグインだけです。");
            ImGui.BulletText("Local-only / private Pluginは、差分確認・Google送信・辞書自動更新の対象外です。");
            ImGui.BulletText("導入済み公開PluginのLocal Manifestは、表示へ翻訳を適用するためだけに使用します。");
            ImGui.TextWrapped("※ Local-only / private Pluginの名前や説明文をGoogle翻訳へ送信しないよう分離しています。");
            ImGui.Spacing();

            ImGui.TextUnformatted("【Plugin Installer用の辞書ファイル】");
            ImGui.BulletText("plugin-installer-translations.json：Pluginごとの説明文・Punchlineの翻訳を保存する辞書です。");
            ImGui.BulletText("plugin-installer-terminology.json：FFXIV/Dalamud用語など、翻訳時に保護・補正するための用語辞書です。");
            ImGui.TextWrapped("通常はPlugin JP Helperの配布ZIPに含まれているため、個別にダウンロードして配置する必要はありません。");
            ImGui.TextWrapped("保存先：XIVLauncher\\pluginConfigs\\PluginJPHelper\\Dictionaries\\PluginInstaller\\");
            ImGui.Spacing();

            ImGui.TextUnformatted("【翻訳辞書が0件・日本語化されない場合】");
            ImGui.BulletText("「辞書ファイル設定」の「使用中」に plugin-installer-translations.json が表示されているか確認します。");
            ImGui.BulletText("「読込件数」が0の場合は「再読み込み」を試してください。");
            ImGui.BulletText("別のJSONを指定している場合は「翻訳ファイルを選択」またはパス欄から正しい辞書を指定し、「この辞書を使用」を押します。");
            ImGui.BulletText("辞書ファイル自体がおかしい、または再読み込みしても0件のままの場合は、Plugin JP Helperの再インストールをお試しください。");
            ImGui.TextWrapped("※ 再インストール時にPlugin Installer用の初期辞書ファイルも一緒に配置されます。");
            ImGui.Spacing();

            ImGui.TextUnformatted("【負荷について】");
            ImGui.TextWrapped("PJHは作り始めの頃、広い範囲へ翻訳処理を適用するとフレームレートへ影響することがあったため、現在は対象を限定し、保存済み辞書と差分処理を優先して重い処理を増やさないようにしています。必要のないプラグインは日本語化をOFFにして使用できます。");
        }

        if (ImGui.CollapsingHeader("かんたん：公式CSVを使う方法"))
        {
            ImGui.BulletText("「公式辞書」タブを開き、「公式辞書一覧を取得」を押します。");
            ImGui.BulletText("使いたいCSVにチェックを入れ、「選択した辞書をダウンロード／更新」を押します。");
            ImGui.BulletText("「公式辞書フォルダーを開く」でダウンロード先を開きます。");
            ImGui.BulletText("使いたいCSVを「ローカル辞書フォルダー」へコピーします。");
            ImGui.BulletText("「翻訳辞書」タブでCSVを読み込み、日本語化を有効にします。");
            ImGui.TextWrapped("※ 公式辞書フォルダーは配布・更新確認用です。そこへ置いただけでは翻訳に使用されません。ローカル辞書フォルダーへコピーしてから使用してください。");
            ImGui.TextWrapped("※ 公式辞書を更新しても、ローカル側でユーザーが作成・編集したCSVは自動で上書きしません。");
        }

        if (ImGui.CollapsingHeader("自分で辞書を作る方法（未翻訳取得・自動翻訳）"))
        {
            ImGui.TextWrapped("「未翻訳・取得」タブは、取得作業と自動翻訳作業を分けています。上から順番に進めると安全に辞書を作れます。");
            ImGui.Spacing();

            ImGui.TextUnformatted("【1. 翻訳対象プラグインを選ぶ】");
            ImGui.BulletText("「インストール済みプラグインから追加」で、日本語化したいプラグインを選んで「翻訳対象に追加」を押します。");
            ImGui.BulletText("対象プラグインを切り替える場合は、画面上部の翻訳対象プラグイン選択から変更します。");
            ImGui.TextWrapped("※ 取得中・自動翻訳中・翻訳結果の確認待ち中は、誤操作を防ぐため対象変更や関係するボタンが一時的に無効になります。");
            ImGui.Spacing();

            ImGui.TextUnformatted("【2. 「未翻訳取得」サブタブで文字列を集める】");
            ImGui.BulletText("「取得開始」を押します。");
            ImGui.BulletText("対象プラグインの設定画面・タブ・ボタン・ツールチップ・プルダウンなど、翻訳したい場所を一通り表示・操作します。");
            ImGui.BulletText("確認が終わったら「取得停止」を押します。取得中は、一覧編集・対象変更・自動翻訳開始などはできません。");
            ImGui.BulletText("取り込み方式が「自動」の場合は、取得停止時に対象分を翻訳辞書へ取り込みます。「手動」の場合は一覧確認後に「翻訳辞書へ取り込む」を押します。");
            ImGui.Spacing();

            ImGui.TextUnformatted("【3. 取得結果を確認する】");
            ImGui.BulletText("「対象プラグイン」には対象として判定できた未翻訳文字列を表示します。");
            ImGui.BulletText("「対象候補」には対象プラグイン由来の可能性が高い文字列を表示します。必要なものだけ確認して対象側へ移します。");
            ImGui.BulletText("「他プラグイン」は別プラグイン由来と判定した文字列です。対象辞書へは自動で混ぜません。");
            ImGui.BulletText("「判定不能」は所有元を安全に判断できなかった文字列です。必要な場合だけ確認してください。");
            ImGui.BulletText("キャラクター名・サーバー名・FC名・時刻・座標・ログ・ローカルパスなど、公開辞書に不要な動的／個人的文字列は削除してください。");
            ImGui.Spacing();

            ImGui.TextUnformatted("【4. 「自動翻訳」サブタブで下書きを作る】");
            ImGui.BulletText("未翻訳取得を停止してから「未翻訳を自動翻訳」を押します。");
            ImGui.BulletText("選択した翻訳方式を利用し、翻訳可能な固定UI文字列だけを順番に翻訳します。翻訳中は対象変更・取得開始・取り込みなどの操作を無効化します。");
            ImGui.BulletText("URL・コマンド・ローカルパス・明らかな動的数値・技術ID・作者表記などは可能な範囲で翻訳対象外にします。");
            ImGui.BulletText("プラグイン名などの固有名詞やFFXIV用語は、可能な範囲で保護してから翻訳します。");
            ImGui.TextWrapped("※ 自動翻訳は完成した公式訳を作る機能ではなく、ユーザーが辞書を作るための下書き補助です。自然さや意味が正しいか必ず確認してください。");
            ImGui.Spacing();

            ImGui.TextUnformatted("【5. 翻訳ログを確認する】");
            ImGui.BulletText("自動翻訳ログには、翻訳開始・成功・スキップ理由・失敗・Google側エラーなどを表示します。");
            ImGui.BulletText("「利用可能チェック」で、選択中の翻訳方式へ1回だけ通信し、利用可能か、HTTP 429か、通信エラーかを確認できます。429時は自動再試行せず停止します。Plugin Installer側にも同じ設定があります。");
            ImGui.BulletText("自動翻訳中は「停止」ボタンで途中停止できます。429待機中でも停止でき、途中までの翻訳結果は保持します。");
            ImGui.BulletText("問題がある場合は「ログをコピー」で内容を共有できます。");
            ImGui.TextWrapped("※ Google側の制限が出た場合は、短時間に何度も再実行せず、少し時間を空けてから再度お試しください。");
            ImGui.Spacing();

            ImGui.TextUnformatted("【6. 翻訳結果を辞書へ反映する】");
            ImGui.BulletText("翻訳結果を確認し、問題なければ「翻訳結果を辞書へ反映」を押します。不要なら「自動翻訳結果を破棄」を押します。");
            ImGui.BulletText("翻訳結果の確認待ち中は、別の取得や対象変更を行えないようにしています。反映または破棄を完了してから次の作業へ進んでください。");
            ImGui.BulletText("既にユーザーが作成・修正した翻訳や既存の翻訳がある項目は、自動翻訳で勝手に上書きしません。");
            ImGui.Spacing();

            ImGui.TextUnformatted("【7. 翻訳辞書タブで仕上げる】");
            ImGui.BulletText("「翻訳辞書」タブへ移動し、日本語訳を確認・修正します。");
            ImGui.BulletText("内容が整ったらCSVへ保存します。Google自動翻訳の作業用CSVは再生成できますが、手修正済みの既存辞書は自動翻訳で勝手に置き換えない方針です。");
            ImGui.TextWrapped("※ コミュニティ辞書や公式辞書として公開する場合は、個人情報・動的値・別プラグインの文字列が混ざっていないか、必ず最後に確認してください。");
        }

        if (ImGui.CollapsingHeader("コミュニティ辞書を投稿・削除する方法"))
        {
            ImGui.BulletText("「コミュニティ辞書」タブの「コミュニティ辞書をアップロード」を押すと投稿ページが開きます。");
            ImGui.BulletText("投稿者名、削除用パスワード、投稿するCSVを指定して投稿します。GitHubアカウントは必要ありません。");
            ImGui.BulletText("アップロードしたCSVのファイル名は変更されず、そのままコミュニティ辞書名として登録されます。");
            ImGui.BulletText("削除する場合は投稿ページ下部の削除欄から自分の辞書を選び、投稿時に決めた削除用パスワードを入力します。");
            ImGui.TextWrapped("※ 削除用パスワードは必ず自分で覚えておいてください。旧方式で登録された辞書など、削除用パスワードを持たない辞書はWeb画面から削除できません。");
            ImGui.Spacing();
            ImGui.TextWrapped("【アップロード前に必ず確認してください】");
            ImGui.TextWrapped("プラグインによっては、キャラクター名・サーバー名・FC名など、個人を特定される可能性がある表示文字列まで翻訳・取得されてCSVに含まれる場合があります。コミュニティ辞書として公開すると、その内容も第三者から見える状態になります。");
            ImGui.TextWrapped("投稿前にCSVを開き、自分のキャラクター名やサーバー名などで検索してください。該当する行が見つかった場合は、その行を削除してからアップロードしてください。必要に応じて他のキャラクター名、ログ、座標、時刻などの個人的・動的な情報も確認してください。");
            ImGui.TextWrapped("※ Plugin JP Helper側ですべての個人情報を自動判定することはできません。公開前の確認は投稿者自身で行ってください。");
        }

        if (ImGui.CollapsingHeader("翻訳辞書と対象プラグインについて"))
        {
            ImGui.TextWrapped("翻訳辞書タブでは、対象プラグインごとのCSVを読み込み、表示文字列の日本語化を有効／無効にできます。");
            ImGui.TextWrapped("初めて使うプラグインは「未翻訳・取得」から対象プラグインへ追加してください。");
            ImGui.TextWrapped("Plugin JP Helper をOFFにすると、対象プラグインの表示は元の文字列へ戻ります。");
        }

        if (ImGui.CollapsingHeader("辞書の更新通知について"))
        {
            ImGui.TextWrapped("Plugin JP Helper起動時と、その後1時間ごとに公式辞書・コミュニティ辞書の更新を確認します。");
            ImGui.TextWrapped("公式辞書はGitHubの Dictionaries/Official フォルダー内のCSVに追加・更新・削除があった場合に通知します。コミュニティ辞書は community-index.json の更新を確認します。");
            ImGui.TextWrapped("更新がある場合はタイトルバーに【公式辞書に更新があります。】【コミュニティ辞書に更新があります。】のように表示されます。");
            ImGui.TextWrapped("各辞書タブで「一覧を取得」が成功すると、その時点を確認済みとして通知が消えます。");
        }

        DrawDiagnosticsSection();
    }

    // 日本語化されないときに最初に見る場所。フックが張れているか、翻訳が走っているかを
    // Dalamudログを開かずに確認できるようにする。
    private void DrawDiagnosticsSection()
    {
        if (!ImGui.CollapsingHeader("動作診断（日本語化されないとき・不具合報告用）")) return;

        ImGui.TextWrapped("Plugin JP HelperはImGuiの描画関数をフックして文字列を差し替えます。日本語化されない場合は、まずフックが有効かどうかを確認してください。");
        ImGui.Spacing();

        var installedCount = hookInstalledNames.Length;
        var failedCount = hookFailedNames.Length;
        if (installedCount == 0)
            TextColoredUnformatted(DiagnosticErrorColor, "フック: すべて失敗しています。日本語化は動作しません。");
        else if (failedCount == 0)
            TextColoredUnformatted(DiagnosticOkColor, $"フック: {installedCount}件すべて有効");
        else
            TextColoredUnformatted(DiagnosticWarnColor, $"フック: 有効 {installedCount}件 / 失敗 {failedCount}件");

        if (failedCount > 0)
        {
            TextColoredUnformatted(DiagnosticErrorColor, $"失敗: {string.Join(", ", hookFailedNames)}");
            ImGui.TextWrapped("ImGuiのバージョンによっては存在しない関数があり、一部の失敗は正常です（例: SeparatorText）。多数が失敗している場合はDalamudのバージョンを確認してください。");
        }

        if (installedCount > 0 && ImGui.TreeNode("有効なフック一覧"))
        {
            // フック名は固定の識別子だが、書式指定APIの TextWrapped ではなく
            // 素通しの TextUnformatted で出す。
            ImGui.TextUnformatted(string.Join(", ", hookInstalledNames));
            ImGui.TreePop();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("【カウンター】自分のウィンドウぶんは数えません。");
        if (ImGui.BeginTable("pjhDiagnosticCounters", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("項目");
            ImGui.TableSetupColumn("回数");
            ImGui.TableHeadersRow();
            DrawDiagnosticCounterRow("翻訳を差し替えた回数", Interlocked.Read(ref translatedCount));
            DrawDiagnosticCounterRow("Combo(配列) 呼び出し", Interlocked.Read(ref comboHookCalls));
            DrawDiagnosticCounterRow("Combo(NUL区切り) 呼び出し", Interlocked.Read(ref comboStrCalls));
            DrawDiagnosticCounterRow("Combo(コールバック) 呼び出し", Interlocked.Read(ref comboFnCalls));
            DrawDiagnosticCounterRow("BeginCombo 呼び出し", Interlocked.Read(ref beginComboCalls));
            DrawDiagnosticCounterRow("Combo内Selectable 呼び出し", Interlocked.Read(ref comboSelectableCalls));
            DrawDiagnosticCounterRow("Combo内で差し替えた項目", Interlocked.Read(ref comboTranslatedItems));
            DrawDiagnosticCounterRow("SliderInt 呼び出し", Interlocked.Read(ref sliderIntCalls));
            DrawDiagnosticCounterRow("TableSetupColumn 呼び出し", Interlocked.Read(ref tableSetupColumnCalls));
            ImGui.EndTable();
        }

        ImGui.Spacing();
        if (ActionButton("診断情報をコピー", ButtonRole.Primary))
            ImGui.SetClipboardText(BuildDiagnosticsReport());
        ImGui.SameLine();
        ImGui.TextDisabled("不具合報告に貼り付けてください。");

        ImGui.Spacing();
        ImGui.Separator();
        var recording = hookDiagnosticsRecording;
        if (ImGui.Checkbox("フック通過ログを記録する", ref recording))
        {
            hookDiagnosticsRecording = recording;
            // OFFにしたら溜まったぶんも捨てる。次にONにしたとき古い記録と混ざらないようにする。
            if (!recording) while (hookDiagnosticEntries.TryDequeue(out _)) { }
        }
        ImGui.TextDisabled($"ONの間だけ、直近{MaxHookDiagnosticEntries}件の通過内容を記録します。常時ONにする必要はありません。");

        var entries = hookDiagnosticEntries.ToArray();
        if (entries.Length == 0)
        {
            ImGui.TextDisabled(hookDiagnosticsRecording ? "まだ記録がありません。対象プラグインの画面を開いてください。" : "記録は停止中です。");
            return;
        }

        if (ImGui.BeginTable("pjhDiagnosticEntries", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.ScrollX | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoKeepColumnsVisible, new Vector2(0, 200)))
        {
            ImGui.TableSetupColumn("時刻");
            ImGui.TableSetupColumn("種別");
            ImGui.TableSetupColumn("文字列");
            ImGui.TableSetupColumn("ウィンドウ");
            ImGui.TableSetupColumn("判定プラグイン");
            ImGui.TableHeadersRow();
            for (var i = entries.Length - 1; i >= 0; i--)
            {
                var entry = entries[i];
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted(entry.Time);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(entry.Kind);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(entry.Text);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(entry.Window);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(entry.Owner);
            }
            ImGui.EndTable();
        }
    }

    private static readonly Vector4 DiagnosticOkColor = new(0.40f, 0.82f, 0.48f, 1.00f);
    private static readonly Vector4 DiagnosticWarnColor = new(0.95f, 0.66f, 0.25f, 1.00f);
    private static readonly Vector4 DiagnosticErrorColor = new(0.92f, 0.42f, 0.42f, 1.00f);

    // ImGui.TextColored は内部で書式指定を解釈するため、変数を含む文字列には使わない。
    private static void TextColoredUnformatted(Vector4 color, string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    private static void DrawDiagnosticCounterRow(string label, long value)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn(); ImGui.TextUnformatted(label);
        ImGui.TableNextColumn(); ImGui.TextUnformatted(value.ToString("N0"));
    }

    private string BuildDiagnosticsReport()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"PluginJPHelper 診断情報 ({DateTime.Now:yyyy-MM-dd HH:mm:ss})");
        builder.AppendLine($"有効フック({hookInstalledNames.Length}): {string.Join(", ", hookInstalledNames)}");
        builder.AppendLine($"失敗フック({hookFailedNames.Length}): {(hookFailedNames.Length == 0 ? "なし" : string.Join(", ", hookFailedNames))}");
        builder.AppendLine($"翻訳差し替え: {Interlocked.Read(ref translatedCount)}");
        builder.AppendLine($"Combo配列={Interlocked.Read(ref comboHookCalls)} ComboNUL={Interlocked.Read(ref comboStrCalls)} Comboコールバック={Interlocked.Read(ref comboFnCalls)}");
        builder.AppendLine($"BeginCombo={Interlocked.Read(ref beginComboCalls)} Combo内Selectable={Interlocked.Read(ref comboSelectableCalls)} Combo内差し替え={Interlocked.Read(ref comboTranslatedItems)}");
        builder.AppendLine($"SliderInt={Interlocked.Read(ref sliderIntCalls)} TableSetupColumn={Interlocked.Read(ref tableSetupColumnCalls)}");
        builder.AppendLine($"選択中プラグイン: {selectedPlugin} / 取得対象: {capturePlugin} / 取得中: {captureEnabled}");
        return builder.ToString();
    }

    private void DrawDictionaryStatusColumnControls(string idSuffix)
    {
        ImGui.TextUnformatted("表示項目:");
        ImGui.SameLine();
        ImGui.Checkbox($"翻訳辞書登録##showTranslationRegistered_{idSuffix}", ref showStatusTranslationRegistered);
        ImGui.SameLine();
        ImGui.Checkbox($"未翻訳登録##showCaptureRegistered_{idSuffix}", ref showStatusCaptureRegistered);
        ImGui.SameLine();
        ImGui.Checkbox($"日本語化##showTranslationEnabled_{idSuffix}", ref showStatusTranslationEnabled);
        ImGui.SameLine();
        ImGui.Checkbox($"導入##showInstalled_{idSuffix}", ref showStatusInstalled);
        ImGui.SameLine();
        ImGui.Checkbox($"プラグインスイッチ##showPluginSwitch_{idSuffix}", ref showStatusPluginSwitch);
        ImGui.SameLine();
        if (ActionButton($"最小表示##compact_{idSuffix}", ButtonRole.Neutral))
        {
            showStatusTranslationRegistered = false;
            showStatusCaptureRegistered = false;
            showStatusTranslationEnabled = false;
            showStatusInstalled = false;
            showStatusPluginSwitch = false;
        }
        ImGui.SameLine();
        if (ActionButton($"全部表示##all_{idSuffix}", ButtonRole.Neutral))
        {
            showStatusTranslationRegistered = true;
            showStatusCaptureRegistered = true;
            showStatusTranslationEnabled = true;
            showStatusInstalled = true;
            showStatusPluginSwitch = true;
        }
    }

    private int VisibleDictionaryStatusColumnCount()
        => (showStatusTranslationRegistered ? 1 : 0)
         + (showStatusCaptureRegistered ? 1 : 0)
         + (showStatusTranslationEnabled ? 1 : 0)
         + (showStatusInstalled ? 1 : 0)
         + (showStatusPluginSwitch ? 1 : 0);

    private static T[] SortArray<T, TKey>(IEnumerable<T> source, Func<T, TKey> keySelector, bool ascending)
        => ascending
            ? source.OrderBy(keySelector).ToArray()
            : source.OrderByDescending(keySelector).ToArray();

    private static List<T> SortList<T, TKey>(IEnumerable<T> source, Func<T, TKey> keySelector, bool ascending)
        => ascending
            ? source.OrderBy(keySelector).ToList()
            : source.OrderByDescending(keySelector).ToList();

    private static void DrawListSortControls(string id, string[] labels, ref int mode, ref bool ascending)
    {
        if (labels.Length == 0) return;
        mode = Math.Clamp(mode, 0, labels.Length - 1);
        ImGui.TextUnformatted("並び順：");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(170f);
        if (ImGui.BeginCombo($"##sort_{id}", labels[mode]))
        {
            for (var i = 0; i < labels.Length; i++)
            {
                var selected = mode == i;
                if (ImGui.Selectable($"{labels[i]}##sort_{id}_{i}", selected)) mode = i;
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (ImGui.Button($"{(ascending ? "昇順" : "降順")}##sortDir_{id}")) ascending = !ascending;
    }

    private void DrawOfficialDictionaryTab()
    {
        ImGui.TextWrapped("GitHubの公式CSV辞書を確認し、必要なものだけダウンロード／更新できます。使用方法は「ヘルプ」タブを確認してください。");
        ImGui.Separator();
        if (ActionButton("公式辞書一覧を取得", ButtonRole.Primary) && !officialDictionaryBusy)
        {
            _ = RefreshOfficialDictionariesAsync();
            _ = RefreshOfficialNoticeAsync();
        }
        ImGui.SameLine();
        if (ActionButton("公式辞書フォルダーを開く", ButtonRole.Success))
        {
            try { Process.Start(new ProcessStartInfo { FileName = OfficialDictionaryDirectory(), UseShellExecute = true }); officialDictionaryStatus = "公式辞書フォルダーを開きました。"; }
            catch (Exception ex) { officialDictionaryStatus = $"フォルダーを開けませんでした: {ex.Message}"; }
        }
        ImGui.SameLine();
        if (ActionButton("ローカル辞書フォルダーを開く", ButtonRole.Warning))
        {
            try { Process.Start(new ProcessStartInfo { FileName = DictionaryDirectory(), UseShellExecute = true }); officialDictionaryStatus = "ローカル辞書フォルダーを開きました。"; }
            catch (Exception ex) { officialDictionaryStatus = $"ローカル辞書フォルダーを開けませんでした: {ex.Message}"; }
        }
        if (!string.IsNullOrWhiteSpace(officialDictionaryStatus)) ImGui.TextWrapped(officialDictionaryStatus);

        List<OfficialDictionaryEntry> snapshot;
        lock (officialDictionaryLock) snapshot = officialDictionaryEntries.ToList();
        if (snapshot.Count == 0)
        {
            ImGui.TextDisabled("「公式辞書一覧を取得」を押すとGitHub上のCSV一覧を表示します。");
            return;
        }

        DrawDictionaryStatusColumnControls("official");
        DrawListSortControls("official", new[] { "公式辞書", "対応プラグイン", "翻訳辞書登録", "未翻訳登録", "日本語化", "導入", "プラグインスイッチ", "状態" }, ref officialDictionarySortMode, ref officialDictionarySortAscending);
        snapshot = officialDictionarySortMode switch
        {
            1 => SortList(snapshot, x => x.PluginName, officialDictionarySortAscending),
            2 => SortList(snapshot, x => GetPluginStatusColumns(x.PluginName).TranslationRegistered == "有り", officialDictionarySortAscending),
            3 => SortList(snapshot, x => GetPluginStatusColumns(x.PluginName).CaptureRegistered == "有り", officialDictionarySortAscending),
            4 => SortList(snapshot, x => GetPluginStatusColumns(x.PluginName).TranslationEnabled == "ON", officialDictionarySortAscending),
            5 => SortList(snapshot, x => GetPluginStatusColumns(x.PluginName).Installed == "導入済み", officialDictionarySortAscending),
            6 => SortList(snapshot, x => GetPluginStatusColumns(x.PluginName).PluginSwitch == "ON", officialDictionarySortAscending),
            7 => SortList(snapshot, x => GetOfficialDictionaryState(x), officialDictionarySortAscending),
            _ => SortList(snapshot, x => x.Name, officialDictionarySortAscending),
        };
        var statusColumnCount = VisibleDictionaryStatusColumnCount();
        if (ImGui.BeginTable("officialDictionaryTable", 4 + statusColumnCount, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollX | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoKeepColumnsVisible, new Vector2(0, -70)))
        {
            ImGui.TableSetupColumn("選択", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("公式辞書", ImGuiTableColumnFlags.WidthFixed, 190);
            ImGui.TableSetupColumn("対応プラグイン", ImGuiTableColumnFlags.WidthFixed, 170);
            if (showStatusTranslationRegistered) ImGui.TableSetupColumn("翻訳辞書登録", ImGuiTableColumnFlags.WidthFixed, 95);
            if (showStatusCaptureRegistered) ImGui.TableSetupColumn("未翻訳登録", ImGuiTableColumnFlags.WidthFixed, 90);
            if (showStatusTranslationEnabled) ImGui.TableSetupColumn("日本語化", ImGuiTableColumnFlags.WidthFixed, 80);
            if (showStatusInstalled) ImGui.TableSetupColumn("導入", ImGuiTableColumnFlags.WidthFixed, 85);
            if (showStatusPluginSwitch) ImGui.TableSetupColumn("プラグインスイッチ", ImGuiTableColumnFlags.WidthFixed, 115);
            ImGui.TableSetupColumn("状態", ImGuiTableColumnFlags.WidthFixed, 100);
            var officialHeaders = new List<string> { "選択", "公式辞書", "対応プラグイン" };
            if (showStatusTranslationRegistered) officialHeaders.Add("翻訳辞書登録");
            if (showStatusCaptureRegistered) officialHeaders.Add("未翻訳登録");
            if (showStatusTranslationEnabled) officialHeaders.Add("日本語化");
            if (showStatusInstalled) officialHeaders.Add("導入");
            if (showStatusPluginSwitch) officialHeaders.Add("プラグインスイッチ");
            officialHeaders.Add("状態");
            DrawJapaneseTableHeaderRow(officialHeaders);
            foreach (var item in snapshot)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                var selected = selectedOfficialDictionaries.Contains(item.Name);
                if (ImGui.Checkbox($"##official_{item.Name}", ref selected))
                {
                    if (selected) selectedOfficialDictionaries.Add(item.Name); else selectedOfficialDictionaries.Remove(item.Name);
                }
                ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted(item.Name);
                ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(item.PluginName);
                var pluginStatus = GetPluginStatusColumns(item.PluginName);
                var column = 3;
                if (showStatusTranslationRegistered) { ImGui.TableSetColumnIndex(column++); DrawYesNoStatus(pluginStatus.TranslationRegistered); }
                if (showStatusCaptureRegistered) { ImGui.TableSetColumnIndex(column++); DrawYesNoStatus(pluginStatus.CaptureRegistered); }
                if (showStatusTranslationEnabled) { ImGui.TableSetColumnIndex(column++); DrawOnOffStatus(pluginStatus.TranslationEnabled); }
                if (showStatusInstalled) { ImGui.TableSetColumnIndex(column++); DrawInstalledStatus(pluginStatus.Installed); }
                if (showStatusPluginSwitch) { ImGui.TableSetColumnIndex(column++); DrawOnOffStatus(pluginStatus.PluginSwitch); }
                ImGui.TableSetColumnIndex(column);
                var state = GetOfficialDictionaryState(item);
                if (state == "更新あり") ImGui.TextColored(new Vector4(1f, 0.75f, 0.20f, 1f), state);
                else if (state == "最新版") ImGui.TextColored(new Vector4(0.35f, 0.85f, 0.50f, 1f), state);
                else ImGui.TextUnformatted(state);
            }
            ImGui.EndTable();
        }
        if (ActionButton("選択した辞書をダウンロード／更新", ButtonRole.Success) && !officialDictionaryBusy)
            _ = DownloadSelectedOfficialDictionariesAsync();
        if (officialDictionaryBusy) { ImGui.SameLine(); ImGui.TextDisabled("通信中..."); }
    }

    private void DrawDictionaryTab()
    {
        var pluginNames = GetPluginConfigSnapshot()
            .Where(x => x.Value.TranslationTarget)
            .Select(x => x.Key)
            .OrderBy(x => PluginSortKey(x)).ThenBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        if (pluginNames.Length == 0)
        {
            ImGui.TextDisabled("翻訳対象がありません。「未翻訳・取得」タブからインストール済みプラグインを追加してください。");
            return;
        }
        if (!pluginNames.Contains(selectedPlugin, StringComparer.Ordinal)) selectedPlugin = pluginNames[0];
        ImGui.TextUnformatted("対象プラグイン：");
        ImGui.SameLine();
        DrawPluginDropdown("##dictPluginDropdown", pluginNames, ref selectedPlugin, false);
        ImGui.SameLine();
        var crossSelectionLocked = captureEnabled || baselineCaptureEnabled || dictionaryAutoTranslateTask != null || dictionaryAutoTranslateDrafts.Count > 0;
        if (crossSelectionLocked) ImGui.BeginDisabled();
        if (ActionButton("未翻訳・取得側にも適用", ButtonRole.Neutral))
            capturePlugin = selectedPlugin;
        if (crossSelectionLocked) ImGui.EndDisabled();
        var state = config.Plugins[selectedPlugin];
        var enabled = state.Enabled;
        ImGui.PushStyleColor(ImGuiCol.CheckMark, new Vector4(0.28f, 0.78f, 0.48f, 1.00f));
        if (ImGui.Checkbox($"{selectedPlugin} の日本語化を有効##enabled", ref enabled))
        {
            state.Enabled = enabled;

            // OFF時は現在の描画所有者スタックを次フレームで再判定させる。
            // 辞書やWindow関連付け自体は保持する。
            if (!enabled)
            {
                lastExplicitWindowOwner = string.Empty;
                lastExplicitWindowOwnerTick = 0;
            }

            SaveConfig();
        }
        ImGui.PopStyleColor();
        ImGui.SameLine();
        var catalogCount = GetDictionaryCatalog(selectedPlugin).Count();
        ImGui.TextUnformatted($"原文 {catalogCount} 件 / ユーザー訳 {state.UserOverrides.Count} 件");
        ImGui.SameLine();
        if (ActionButton("翻訳対象から外す", ButtonRole.Danger))
        {
            RemoveTranslationTarget(selectedPlugin);
            return;
        }
        if (config.CleanSlateMode) ImGui.TextWrapped("完全リセット後の取得済み原文を表示しています。旧標準訳は使いません。");

        ImGui.TextDisabled("配布されたCSVを使う場合は「CSVを選択して読み込む」だけで利用できます。");
        if (ActionButton("CSVを選択して読み込む", ButtonRole.Primary))
        {
            var chosen = ShowCsvOpenDialog();
            if (!string.IsNullOrWhiteSpace(chosen)) csvStatus = ImportDictionaryCsv(selectedPlugin, chosen);
        }
        ImGui.SameLine();
        if (ActionButton("再読み込み", ButtonRole.Primary))
        {
            csvStatus = ReloadDictionaryCsv(selectedPlugin);
        }
        ImGui.SameLine();
        if (ActionButton("名前を付けて書き出し", ButtonRole.Success))
        {
            var savePath = ShowCsvSaveDialog(selectedPlugin, state.LastCsvPath);
            if (!string.IsNullOrWhiteSpace(savePath)) csvStatus = ExportDictionaryCsv(selectedPlugin, savePath);
        }
        ImGui.SameLine();
        if (ActionButton("保存フォルダーを開く", ButtonRole.Neutral)) csvStatus = OpenDictionaryFolder();

        if (!string.IsNullOrWhiteSpace(state.LastCsvPath))
            ImGui.TextDisabled($"指定中CSV: {state.LastCsvPath}");
        else
            ImGui.TextDisabled("指定中CSV: なし");

        DrawOpenCommandControls(selectedPlugin, state);
        DrawAdditionalWindowControls(selectedPlugin, state);

        if (ImGui.Checkbox("未訳だけ表示", ref showUntranslatedOnly)) { }
        if (!string.IsNullOrWhiteSpace(csvStatus)) ImGui.TextWrapped(csvStatus);

        ImGui.SetNextItemWidth(320); ImGui.InputTextWithHint("##dictfilter", "原文・訳を絞り込み", ref filter, 256);
        ImGui.SameLine();
        var sortLabels = new[] { "原文", "画面内項目", "日本語訳", "未訳優先" };
        ImGui.SetNextItemWidth(140);
        if (ImGui.BeginCombo("並び順##dictSort", sortLabels[Math.Clamp(dictionarySortMode, 0, sortLabels.Length - 1)]))
        {
            for (var i = 0; i < sortLabels.Length; i++)
            {
                var selected = dictionarySortMode == i;
                if (ImGui.Selectable($"{sortLabels[i]}##dictSort{i}", selected)) dictionarySortMode = i;
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (ImGui.Button(dictionarySortAscending ? "昇順" : "降順")) dictionarySortAscending = !dictionarySortAscending;

        if (!string.IsNullOrWhiteSpace(filter))
        {
            ImGui.SameLine();
            if (ActionButton("検索結果を一括削除", ButtonRole.Danger))
                ImGui.OpenPopup("検索結果を一括削除しますか？");

            if (ImGui.BeginPopupModal("検索結果を一括削除しますか？", ImGuiWindowFlags.AlwaysAutoResize))
            {
                ImGui.TextWrapped($"「{filter}」に一致する翻訳辞書の項目をすべて削除します。\nこの操作は指定中CSVにも反映されます。");
                if (ActionButton("削除する", ButtonRole.Danger))
                {
                    csvStatus = DeleteFilteredDictionaryEntries(selectedPlugin, filter);
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();
                if (ActionButton("キャンセル", ButtonRole.Neutral)) ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
            }
        }

        if (selectedPlugin == "RSR")
        {
            ImGui.TextDisabled("RSR本体の左メニューと同じ順番で表示しています。");
            if (ImGui.BeginTabBar("rsrDictionaryTabs", ImGuiTabBarFlags.FittingPolicyScroll))
            {
                var menuCounts = GetDictionaryMenuCounts("RSR");
                foreach (var (key, label) in GetRsrDictionaryTabs())
                {
                    var count = menuCounts.TryGetValue(key, out var tabCount) ? tabCount : 0;
                    if (ImGui.BeginTabItem($"{label} ({count})"))
                    {
                        DrawDictionaryTable(state, key);
                        ImGui.EndTabItem();
                    }
                }
                ImGui.EndTabBar();
            }
        }
        else
        {
            DrawDictionaryTable(state, null);
        }
    }

    // Dear ImGui標準の列メニューは英語固定のため、PJHの表では日本語メニューを表示する。
    // 列幅調整はDalamudのImGui内部APIを通して、Dear ImGui標準と同じ処理を呼ぶ。
    private void DrawJapaneseColumnSizingMenu(int column, string popupId)
    {
        // BeginPopupで描画先がポップアップへ切り替わる前に、元テーブルを保持する。
        var table = ImGuiP.GetCurrentTable();
        if (!ImGui.BeginPopupContextItem(popupId))
            return;

        try
        {
            if (table.Handle != null && ImGui.MenuItem("この列を内容に合わせる"))
                ImGuiP.TableSetColumnWidthAutoSingle(table, column);

            if (table.Handle != null && ImGui.MenuItem("すべての列を内容に合わせる"))
                ImGuiP.TableSetColumnWidthAutoAll(table);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "列幅の自動調整に失敗しました。");
        }
        finally
        {
            ImGui.EndPopup();
        }
    }

    private void DrawJapaneseTableHeaderRow(IEnumerable<string> labels)
    {
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        var column = 0;
        foreach (var label in labels)
        {
            var currentColumn = column++;
            ImGui.TableSetColumnIndex(currentColumn);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(label);
            DrawJapaneseColumnSizingMenu(currentColumn, $"##pjh_colmenu_{currentColumn}_{label}");
        }
    }

    private void DrawSortableJapaneseTableHeader(int column, string label, int sortMode)
    {
        ImGui.TableSetColumnIndex(column);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(GetSortableHeaderLabel(label, sortMode));
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            SetDictionarySortFromHeader(sortMode);
        DrawJapaneseColumnSizingMenu(column, $"##pjh_sortcolmenu_{column}_{label}");
    }

    private string GetSortableHeaderLabel(string label, int sortMode)
    {
        if (dictionarySortMode != sortMode) return label;
        return $"{label} {(dictionarySortAscending ? "▲" : "▼")}";
    }

    private void SetDictionarySortFromHeader(int sortMode)
    {
        if (dictionarySortMode == sortMode)
            dictionarySortAscending = !dictionarySortAscending;
        else
        {
            dictionarySortMode = sortMode;
            dictionarySortAscending = true;
        }
    }

    private void DrawDictionaryTable(PluginDictionaryState state, string? category)
    {
        if (ImGui.BeginTable($"dict_{selectedPlugin}_{category ?? "all"}", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.ScrollX | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoKeepColumnsVisible, new Vector2(0, -35)))
        {
            ImGui.TableSetupColumn("画面内項目", ImGuiTableColumnFlags.WidthFixed, 150);
            ImGui.TableSetupColumn("原文（英語/中国語）", ImGuiTableColumnFlags.WidthFixed, 360);
            ImGui.TableSetupColumn("日本語訳（編集可）", ImGuiTableColumnFlags.WidthFixed, 420);
            ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 90);

            // ヘッダー行を縦スクロール時も固定。
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableNextRow(ImGuiTableRowFlags.Headers);

            DrawSortableJapaneseTableHeader(0, "画面内項目", 1);
            DrawSortableJapaneseTableHeader(1, "原文（英語/中国語）", 0);
            DrawSortableJapaneseTableHeader(2, "日本語訳（編集可）", 2);

            ImGui.TableSetColumnIndex(3);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("操作");

            var rows = GetDictionaryCatalog(selectedPlugin)
                .Select(kv => new
                {
                    Key = kv.Key,
                    DefaultJa = kv.Value,
                    CurrentJa = state.UserOverrides.TryGetValue(kv.Key, out var ov) && !string.IsNullOrWhiteSpace(ov) ? ov : (state.OfficialOverrides.TryGetValue(kv.Key, out var officialJa) && !string.IsNullOrWhiteSpace(officialJa) ? officialJa : kv.Value),
                    Section = selectedPlugin == "RSR" ? GetRsrSection(kv.Key) : string.Empty,
                    Menu = selectedPlugin == "RSR" ? GetRsrMenuCategory(kv.Key) : string.Empty,
                })
                .Where(row => category == null || row.Menu == category)
                .Where(row => !showUntranslatedOnly || string.IsNullOrWhiteSpace(row.CurrentJa))
                .Where(row => string.IsNullOrWhiteSpace(filter)
                    || row.Key.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || row.CurrentJa.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || row.Section.Contains(filter, StringComparison.OrdinalIgnoreCase));

            rows = dictionarySortMode switch
            {
                1 => dictionarySortAscending
                    ? rows.OrderBy(x => x.Section, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    : rows.OrderByDescending(x => x.Section, StringComparer.OrdinalIgnoreCase).ThenByDescending(x => x.Key, StringComparer.OrdinalIgnoreCase),
                2 => dictionarySortAscending
                    ? rows.OrderBy(x => x.CurrentJa, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    : rows.OrderByDescending(x => x.CurrentJa, StringComparer.OrdinalIgnoreCase).ThenByDescending(x => x.Key, StringComparer.OrdinalIgnoreCase),
                3 => dictionarySortAscending
                    ? rows.OrderBy(x => string.IsNullOrWhiteSpace(x.CurrentJa) ? 0 : 1).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    : rows.OrderByDescending(x => string.IsNullOrWhiteSpace(x.CurrentJa) ? 0 : 1).ThenByDescending(x => x.Key, StringComparer.OrdinalIgnoreCase),
                _ => dictionarySortAscending
                    ? rows.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    : rows.OrderByDescending(x => x.Key, StringComparer.OrdinalIgnoreCase),
            };

            // v0.0.66: 大きい辞書（特にRSR）で全行のInputText/Buttonを毎フレーム生成すると重くなる。
            // 並び替え後の行を配列化し、ImGuiListClipperで画面に見えている行だけ描画する。
            var rowList = rows.ToArray();
            var clipper = ImGui.ImGuiListClipper();
            clipper.Begin(rowList.Length);
            while (clipper.Step())
            {
                for (var rowIndex = clipper.DisplayStart; rowIndex < clipper.DisplayEnd; rowIndex++)
                {
                    var row = rowList[rowIndex];
                    var key = $"{selectedPlugin}\u001f{row.Key}";
                    if (!editBuffers.TryGetValue(key, out var edit)) edit = row.CurrentJa;
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.TextUnformatted(row.Section);
                    ImGui.TableSetColumnIndex(1);

                    var sourceText = row.Key;
                    ImGui.SetNextItemWidth(-1);
                    ImGui.InputText($"##source{key.GetHashCode()}", ref sourceText, 4096, ImGuiInputTextFlags.ReadOnly);

                    ImGui.TableSetColumnIndex(2);
                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.InputText($"##edit{key.GetHashCode()}", ref edit, 1024))
                        editBuffers[key] = edit;

                    ImGui.TableSetColumnIndex(3);
                    if (ImGui.SmallButton($"保存##{key.GetHashCode()}"))
                    {
                        if (string.IsNullOrWhiteSpace(edit))
                        {
                            state.UserOverrides.Remove(row.Key);
                            editBuffers[key] = row.DefaultJa;
                        }
                        else
                        {
                            state.UserOverrides[row.Key] = edit;
                            editBuffers[key] = edit;
                        }
                        state.DeletedKeys.Remove(row.Key);
                        SaveConfig();
                    }
                    ImGui.SameLine();
                    if (ActionButton($"削除##d{key.GetHashCode()}", ButtonRole.Danger))
                    {
                        csvStatus = DeleteDictionaryEntry(selectedPlugin, row.Key);
                        break;
                    }

                    if (state.UserOverrides.ContainsKey(row.Key))
                    {
                        if (ImGui.SmallButton($"訳を消す##r{key.GetHashCode()}"))
                        {
                            state.UserOverrides.Remove(row.Key);
                            editBuffers[key] = row.DefaultJa;
                            SaveConfig();
                        }
                    }
                }
            }
            clipper.End();

            ImGui.EndTable();
        }
    }

    private string DeleteDictionaryEntry(string pluginName, string source)
    {
        if (!config.Plugins.TryGetValue(pluginName, out var state)) return "削除対象の辞書が見つかりませんでした。";

        state.UserOverrides.Remove(source);
        state.Locations.Remove(source);
        state.DeletedKeys.Add(source);
        editBuffers.Remove($"{pluginName}\u001f{source}");
        InvalidateDictionaryUiCache();
        SaveConfig();

        if (!string.IsNullOrWhiteSpace(state.LastCsvPath) && File.Exists(state.LastCsvPath))
            return ExportDictionaryCsv(pluginName, state.LastCsvPath).Replace("CSVを書き出しました:", "辞書から削除しCSVへ反映しました:", StringComparison.Ordinal);

        return "辞書から削除しました。";
    }

    private string DeleteFilteredDictionaryEntries(string pluginName, string searchText)
    {
        if (!config.Plugins.TryGetValue(pluginName, out var state)) return "削除対象の辞書が見つかりませんでした。";
        if (string.IsNullOrWhiteSpace(searchText)) return "検索文字を入力してください。";

        var targets = GetDictionaryCatalogSnapshot(pluginName)
            .Where(kv =>
            {
                state.Locations.TryGetValue(kv.Key, out var loc);
                var ja = state.UserOverrides.TryGetValue(kv.Key, out var ov) && !string.IsNullOrWhiteSpace(ov) ? ov : (state.OfficialOverrides.TryGetValue(kv.Key, out var officialJa) && !string.IsNullOrWhiteSpace(officialJa) ? officialJa : kv.Value);
                return kv.Key.Contains(searchText, StringComparison.OrdinalIgnoreCase)
                    || ja.Contains(searchText, StringComparison.OrdinalIgnoreCase)
                    || (loc?.Section?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (loc?.Menu?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false);
            })
            .Select(kv => kv.Key)
            .ToArray();

        if (targets.Length == 0) return "検索結果に削除対象はありませんでした。";

        foreach (var source in targets)
        {
            state.UserOverrides.Remove(source);
            state.Locations.Remove(source);
            state.DeletedKeys.Add(source);
            editBuffers.Remove($"{pluginName}\u001f{source}");
        }

        InvalidateDictionaryUiCache();
        SaveConfig();

        if (!string.IsNullOrWhiteSpace(state.LastCsvPath) && File.Exists(state.LastCsvPath))
        {
            var result = ExportDictionaryCsv(pluginName, state.LastCsvPath);
            if (result.StartsWith("CSV書き出し失敗", StringComparison.Ordinal)) return $"{targets.Length}件削除しましたが、CSVへの反映に失敗しました。 {result}";
        }

        return $"検索結果から {targets.Length} 件削除しました。";
    }

    private string GetRsrMenuCategory(string key)
    {
        if (config.Plugins.TryGetValue("RSR", out var state) && state.Locations.TryGetValue(key, out var location) && !string.IsNullOrWhiteSpace(location.Menu))
            return location.Menu;

        // v0.0.22: 意味からの推測分類は廃止。再取得できた所属だけを正とする。
        return "Uncategorized";
    }

    private string GetRsrSection(string key)
    {
        if (config.Plugins.TryGetValue("RSR", out var state) && state.Locations.TryGetValue(key, out var location))
            return location.Section ?? string.Empty;
        return string.Empty;
    }

    private void ApplyCapturedLocations(string pluginName)
    {
        if (!config.Plugins.TryGetValue(pluginName, out var state)) return;
        var items = pluginCaptured[pluginName].Values.ToArray();
        foreach (var group in items.GroupBy(x => x.Text, StringComparer.Ordinal))
        {
            var contexts = group
                .Where(x => !string.IsNullOrWhiteSpace(x.Menu))
                .Select(x => new DictionaryLocation { Menu = x.Menu, Section = x.Section ?? string.Empty })
                .Distinct()
                .ToArray();

            // 取得した原文は、所属が取れない場合も辞書カタログへ必ず登録する。
            // 1箇所だけ特定できた場合のみ所属を付け、0件/複数箇所は未分類として保持する。
            // これによりBMR/BMなど、メニュー判定を持たない対象でも取得停止後すぐ翻訳辞書で編集できる。
            if (contexts.Length == 1) state.Locations[group.Key] = contexts[0];
            else state.Locations[group.Key] = new DictionaryLocation { Menu = string.Empty, Section = string.Empty };
        }
        SaveConfig();
    }

    private void DrawCaptureTab()
    {
        // 非同期翻訳の完了をUI描画の先頭で確定し、以降の操作ロック判定を一貫させる。
        if (dictionaryAutoTranslateTask is { IsCompleted: true } completedTask)
        {
            try
            {
                var result = completedTask.GetAwaiter().GetResult();
                dictionaryAutoTranslateDrafts.Clear();
                foreach (var pair in result.Translations)
                    dictionaryAutoTranslateDrafts[pair.Key] = pair.Value;
                var canceled = dictionaryAutoTranslateCts?.IsCancellationRequested == true;
                dictionaryAutoTranslateStatus = canceled
                    ? $"自動翻訳を停止しました。途中までの翻訳 {result.Translations.Count}件を保持しています。内容を確認して辞書へ反映するか、破棄してください。"
                    : $"自動翻訳完了: {result.Translations.Count}件 / 対象外 {result.Skipped}件 / 失敗 {result.Failed}件。内容を確認してから「翻訳結果を辞書へ反映」を押してください。";
            }
            catch (Exception ex)
            {
                dictionaryAutoTranslateStatus = $"自動翻訳に失敗しました: {ex.Message}";
            }
            dictionaryAutoTranslateTask = null;
            dictionaryAutoTranslateCts?.Dispose();
            dictionaryAutoTranslateCts = null;
        }

        if (dictionaryGoogleAvailabilityTask is { IsCompleted: true } googleCheckTask)
        {
            try
            {
                dictionaryGoogleAvailabilityStatus = googleCheckTask.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                dictionaryGoogleAvailabilityStatus = "確認失敗: " + ex.Message;
            }
            dictionaryGoogleAvailabilityTask = null;
        }

        var workflowLocked = captureEnabled || baselineCaptureEnabled || dictionaryAutoTranslateTask != null || dictionaryAutoTranslateDrafts.Count > 0;
        var workflowLockReason = dictionaryAutoTranslateTask != null
            ? "自動翻訳が完了するまで他の操作はできません。"
            : dictionaryAutoTranslateDrafts.Count > 0
                ? "自動翻訳結果の「辞書へ反映」または「破棄」を完了してから次の操作をしてください。"
                : (captureEnabled || baselineCaptureEnabled)
                    ? "未翻訳取得中です。先に「取得停止」を押してください。"
                    : string.Empty;

        ImGui.TextWrapped("新しい翻訳辞書を作るときに使います。まず「インストール済みプラグインから追加」で日本語化したいプラグインを追加してください。");
        ImGui.TextDisabled("追加後は「取得開始」→対象プラグインの画面やツールチップを一通り表示→「取得停止」の順で操作します。ツールチップ等も可能な限り対象プラグインへ自動分類します。");

        ImGui.Separator();
        ImGui.TextUnformatted("インストール済みプラグインから追加");
        ImGui.TextDisabled("日本語化したいプラグインを選択して追加します。");
        var installed = pluginInterface.InstalledPlugins
            .Where(p => !string.Equals(p.InternalName, pluginInterface.InternalName, StringComparison.OrdinalIgnoreCase))
            .Where(p => !IsInstalledPluginAlreadyRegistered(p.InternalName, p.Name))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.InternalName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // 登録済みになった項目が選択状態に残っていた場合はクリアする。
        if (!string.IsNullOrWhiteSpace(installedPluginSelection)
            && !installed.Any(p => string.Equals(p.InternalName, installedPluginSelection, StringComparison.Ordinal)))
            installedPluginSelection = string.Empty;
        var selectedInstalled = installed.FirstOrDefault(p => string.Equals(p.InternalName, installedPluginSelection, StringComparison.Ordinal));
        var installedPreview = selectedInstalled == null ? "選択してください" : $"{selectedInstalled.Name} ({selectedInstalled.InternalName})";
        ImGui.SetNextItemWidth(360);
        if (ImGui.BeginCombo("##installedPluginSelect", installedPreview))
        {
            foreach (var p in installed)
            {
                var label = $"{p.Name} ({p.InternalName}){(p.IsLoaded ? string.Empty : "  [停止中]")}";
                var selected = string.Equals(installedPluginSelection, p.InternalName, StringComparison.Ordinal);
                if (ImGui.Selectable($"{label}##installed_{p.InternalName}", selected)) installedPluginSelection = p.InternalName;
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (workflowLocked) ImGui.BeginDisabled();
        if (ActionButton("翻訳対象に追加", ButtonRole.Primary)) AddInstalledPluginTarget();
        if (workflowLocked) ImGui.EndDisabled();

        if (ImGui.TreeNode("手入力で追加（自動判定できない場合のみ）"))
        {
            ImGui.TextDisabled("通常は使いません。自動判定できないプラグインだけ手入力します。");
            ImGui.SetNextItemWidth(180); ImGui.InputTextWithHint("##customPlugin", "プラグイン名", ref customPluginName, 64);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(260); ImGui.InputTextWithHint("##customWindow", "ウィンドウ名キーワード", ref customWindowKeyword, 128);
            ImGui.SameLine();
            if (workflowLocked) ImGui.BeginDisabled();
            if (ActionButton("手入力で追加", ButtonRole.Primary)) AddCustomPlugin();
            if (workflowLocked) ImGui.EndDisabled();
            ImGui.TreePop();
        }


        ImGui.Spacing();
        ImGui.TextUnformatted("翻訳対象プラグイン");
        var captureNames = GetPluginConfigSnapshot().Where(x => x.Value.TranslationTarget).Select(x => x.Key)
            .OrderBy(x => PluginSortKey(x)).ThenBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        if (captureNames.Length > 0 && !captureNames.Contains(capturePlugin, StringComparer.Ordinal)) capturePlugin = captureNames[0];
        if (captureNames.Length > 0)
        {
            DrawPluginDropdown("##capturePluginDropdown", captureNames, ref capturePlugin, workflowLocked);
            ImGui.SameLine();
            if (workflowLocked) ImGui.BeginDisabled();
            if (ActionButton("翻訳辞書側にも適用", ButtonRole.Neutral))
                selectedPlugin = capturePlugin;
            if (workflowLocked) ImGui.EndDisabled();
        }

        if (!string.IsNullOrWhiteSpace(capturePlugin))
        {
            if (workflowLocked) ImGui.BeginDisabled();
            if (ActionButton("選択中を翻訳対象から外す", ButtonRole.Danger))
                RemoveTranslationTarget(capturePlugin);
            if (workflowLocked) ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.TextDisabled("辞書データは削除せず、翻訳対象からだけ外します。");
        }

        if (string.IsNullOrWhiteSpace(capturePlugin))
        {
            ImGui.TextDisabled("先に翻訳対象プラグインを追加してください。");
            return;
        }

        EnsureCaptureDictionary(capturePlugin);
        var current = pluginCaptured[capturePlugin];

        var untranslated = current.Values.Where(x => !IsKnown(x.Text)).OrderBy(x => x.Text, StringComparer.OrdinalIgnoreCase).ToArray();
        var bgUntranslated = backgroundCaptured.Values.Where(x => !IsKnown(x.Text)).OrderBy(x => x.Text, StringComparer.OrdinalIgnoreCase).ToArray();
        var targetCandidates = bgUntranslated.Where(x => ClassifyBackgroundItem(x, capturePlugin) == BackgroundCaptureClass.TargetCandidate).ToArray();
        var otherPlugins = bgUntranslated.Where(x => ClassifyBackgroundItem(x, capturePlugin) == BackgroundCaptureClass.OtherPlugin).ToArray();
        var unknownItems = bgUntranslated.Where(x => ClassifyBackgroundItem(x, capturePlugin) == BackgroundCaptureClass.Unknown).ToArray();

        ImGui.Separator();
        if (!string.IsNullOrWhiteSpace(workflowLockReason))
        {
            DrawBlinkingWorkflowWarning(workflowLockReason);
            ImGui.TextDisabled("作業途中で対象プラグインや取得内容が変わらないよう、関係するボタンを一時的に無効化しています。");
        }
        else
        {
            ImGui.TextDisabled("取得 → 自動翻訳 → 内容確認 → 辞書へ反映、の順に進めてください。");
        }

        ImGui.PushStyleColor(ImGuiCol.Tab, new Vector4(0.16f, 0.20f, 0.26f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.TabHovered, new Vector4(0.24f, 0.46f, 0.70f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.TabActive, new Vector4(0.18f, 0.38f, 0.62f, 1.00f));
        if (ImGui.BeginTabBar("captureWorkTabs"))
        {
            if (ImGui.BeginTabItem("未翻訳取得"))
            {
                ImGui.TextUnformatted("翻訳辞書への取り込み：");
                ImGui.SameLine();
                var captureControlLocked = dictionaryAutoTranslateTask != null || dictionaryAutoTranslateDrafts.Count > 0;
                if (captureControlLocked) ImGui.BeginDisabled();
                if (ActionButton(config.CaptureAutoImport ? "● 自動" : "自動", config.CaptureAutoImport ? ButtonRole.Success : ButtonRole.Neutral))
                {
                    config.CaptureAutoImport = true;
                    SaveConfig();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("取得停止時に、取得した未翻訳項目を翻訳辞書へ自動で取り込みます。通常はこちらを使用します。");

                ImGui.SameLine();
                if (ActionButton(!config.CaptureAutoImport ? "● 手動" : "手動", !config.CaptureAutoImport ? ButtonRole.Primary : ButtonRole.Neutral))
                {
                    config.CaptureAutoImport = false;
                    SaveConfig();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("取得結果を一覧で確認し、不要な項目を削除してから手動で翻訳辞書へ取り込みます。");
                if (captureControlLocked) ImGui.EndDisabled();

                if (!config.CaptureAutoImport)
                {
                    ImGui.SameLine();
                    var manualImportDisabled = captureEnabled || baselineCaptureEnabled || dictionaryAutoTranslateTask != null || dictionaryAutoTranslateDrafts.Count > 0 || current.Count == 0;
                    if (manualImportDisabled) ImGui.BeginDisabled();
                    if (ActionButton("翻訳辞書へ取り込む", ButtonRole.Success))
                    {
                        ApplyCapturedLocations(capturePlugin);
                        csvStatus = $"{capturePlugin} の取得結果を翻訳辞書へ取り込みました。";
                    }
                    if (manualImportDisabled) ImGui.EndDisabled();
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        ImGui.SetTooltip(manualImportDisabled ? "取得停止・翻訳作業の完了後、取得結果がある場合に使用できます。" : "現在の対象プラグインの取得結果をまとめて翻訳辞書へ取り込みます。不要な項目は先に削除してください。");
                }

                if (captureEnabled || baselineCaptureEnabled)
                {
                    if (ActionButton("取得停止", ButtonRole.Warning))
                    {
                        captureEnabled = false;
                        baselineCaptureEnabled = false;
                        if (config.CaptureAutoImport)
                            ApplyCapturedLocations(capturePlugin);
                    }
                    ImGui.SameLine();
                    ImGui.TextUnformatted($"{capturePlugin} を取得中");
                }
                else
                {
                    var startDisabled = dictionaryAutoTranslateTask != null || dictionaryAutoTranslateDrafts.Count > 0;
                    if (startDisabled) ImGui.BeginDisabled();
                    if (ActionButton("取得開始", ButtonRole.Success))
                    {
                        current.Clear();
                        backgroundCaptured.Clear();
                        RsrNavigationTracker.Reset();
                        baselineCaptureEnabled = true;
                        captureEnabled = true;
                        SaveConfig();
                    }
                    if (startDisabled) ImGui.EndDisabled();
                    ImGui.SameLine();
                    ImGui.TextDisabled(startDisabled ? "自動翻訳の確認・反映を完了してから新しい取得を開始できます。" : "対象プラグインの画面を一通り開いて操作してください。");
                }

                var listEditDisabled = captureEnabled || baselineCaptureEnabled || dictionaryAutoTranslateTask != null || dictionaryAutoTranslateDrafts.Count > 0;
                if (listEditDisabled) ImGui.BeginDisabled();
                if (ActionButton("一覧クリア", ButtonRole.Danger))
                {
                    current.Clear();
                    backgroundCaptured.Clear();
                }
                ImGui.SameLine();
                if (ActionButton("対象分をコピー", ButtonRole.Primary))
                    ImGui.SetClipboardText(BuildUntranslatedExport(capturePlugin));
                ImGui.SameLine();
                if (ActionButton("その他取得分をコピー", ButtonRole.Neutral))
                    ImGui.SetClipboardText(BuildBackgroundExport());
                if (listEditDisabled) ImGui.EndDisabled();

                ImGui.TextUnformatted($"対象: 取得 {current.Count}件 / 未翻訳 {untranslated.Length}件    その他: 取得 {backgroundCaptured.Count}件 / 未翻訳 {bgUntranslated.Length}件");

                if (ImGui.BeginTabBar("captureResultTabs"))
                {
                    if (ImGui.BeginTabItem($"対象プラグイン ({untranslated.Length})"))
                    {
                        DrawCapturedTable($"plugin_{capturePlugin}", untranslated, current, false, ref captureTargetFilter, listEditDisabled);
                        ImGui.EndTabItem();
                    }
                    if (ImGui.BeginTabItem($"対象候補 ({targetCandidates.Length})"))
                    {
                        ImGui.TextDisabled("対象プラグイン由来の可能性が高い項目です。確認してから対象側へ移してください。自動では辞書へ混ぜません。");
                        var moveDisabled = listEditDisabled || targetCandidates.Length == 0;
                        if (moveDisabled) ImGui.BeginDisabled();
                        if (ActionButton("候補を対象プラグインへ移す", ButtonRole.Success))
                        {
                            foreach (var item in targetCandidates) MoveBackgroundItemToTarget(item, capturePlugin);
                            csvStatus = $"対象候補 {targetCandidates.Length}件を {capturePlugin} の取得結果へ移しました。";
                        }
                        if (moveDisabled) ImGui.EndDisabled();
                        DrawCapturedTable("targetCandidate", targetCandidates, backgroundCaptured, true, ref captureOtherFilter, listEditDisabled);
                        ImGui.EndTabItem();
                    }
                    if (ImGui.BeginTabItem($"他プラグイン ({otherPlugins.Length})"))
                    {
                        ImGui.TextDisabled("インストール済みの別プラグインのウィンドウ名と一致した項目です。対象辞書には取り込みません。");
                        DrawCapturedTable("otherPlugin", otherPlugins, backgroundCaptured, true, ref captureOtherFilter, listEditDisabled);
                        ImGui.EndTabItem();
                    }
                    if (ImGui.BeginTabItem($"判定不能 ({unknownItems.Length})"))
                    {
                        ImGui.TextDisabled("所有元を安全に判定できなかった項目です。必要な場合だけコピーして確認してください。");
                        DrawCapturedTable("unknown", unknownItems, backgroundCaptured, true, ref captureOtherFilter, listEditDisabled);
                        ImGui.EndTabItem();
                    }
                    ImGui.EndTabBar();
                }
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("自動翻訳"))
            {
                ImGui.TextWrapped("自動翻訳は辞書作成を補助するための下書き機能です。訳文は必ず確認し、必要に応じて修正してから辞書へ反映してください。");
                ImGui.TextDisabled("既に翻訳済みの項目は自動翻訳の対象にせず、ユーザーが作成・修正した訳を優先します。");
                ImGui.Spacing();
                ImGui.TextUnformatted($"対象プラグイン: {capturePlugin}    未翻訳: {untranslated.Length}件    翻訳下書き: {dictionaryAutoTranslateDrafts.Count}件");
                ImGui.TextUnformatted("翻訳方式");
                if (ActionButton("Google翻訳##dictionaryProviderGoogle", pluginInstallerModule.UsePrivateServer ? ButtonRole.Neutral : ButtonRole.Success))
                    pluginInstallerModule.SetTranslationProvider(false);
                ImGui.SameLine();
                if (ActionButton("PJH中継サーバー##dictionaryProviderPrivate", pluginInstallerModule.UsePrivateServer ? ButtonRole.Success : ButtonRole.Neutral))
                    pluginInstallerModule.SetTranslationProvider(true);
                if (pluginInstallerModule.UsePrivateServer)
                {
                    if (pluginInstallerModule.EffectiveTranslationProviderName == "Google翻訳")
                        ImGui.TextColored(new System.Numerics.Vector4(0.45f, 1f, 0.55f, 1f), "現在の翻訳先: Google翻訳（PJH中継サーバー選択中）");
                    else
                        ImGui.TextUnformatted("現在の翻訳先: PJH中継サーバー");
                }

                var googleCheckBusy = dictionaryGoogleAvailabilityTask != null;
                if (googleCheckBusy) ImGui.BeginDisabled();
                if (ActionButton("利用可能チェック##dictionaryAutoTranslate", ButtonRole.Primary))
                {
                    dictionaryGoogleAvailabilityStatus = "確認中…";
                    AddDictionaryAutoTranslateLog($"[{pluginInstallerModule.TranslationProviderName}確認] 利用可能か確認を開始しました。");
                    dictionaryGoogleAvailabilityTask = pluginInstallerModule.CheckSelectedTranslationAvailabilityAsync("未翻訳の自動翻訳", AddDictionaryAutoTranslateLog);
                }
                if (googleCheckBusy) ImGui.EndDisabled();
                ImGui.SameLine();
                ImGui.TextUnformatted("確認結果: " + dictionaryGoogleAvailabilityStatus);

                if (captureEnabled || baselineCaptureEnabled)
                {
                    DrawBlinkingWorkflowWarning("未翻訳取得中のため、自動翻訳は開始できません。先に「未翻訳取得」タブで取得を停止してください。");
                }
                else if (dictionaryAutoTranslateTask != null)
                {
                    ImGui.TextUnformatted($"自動翻訳中… {pluginInstallerModule.TranslationProviderName}で順番に処理しています。");
                    ImGui.TextUnformatted(pluginInstallerModule.TranslationProviderName + "状態: " + pluginInstallerModule.GoogleStatus);
                    if (!string.IsNullOrWhiteSpace(pluginInstallerModule.GoogleStatusDetail))
                        ImGui.TextWrapped(pluginInstallerModule.GoogleStatusDetail);
                    if (pluginInstallerModule.GoogleStatus.Contains("429", StringComparison.OrdinalIgnoreCase))
                        ImGui.TextWrapped($"[429] {pluginInstallerModule.TranslationProviderName}側がHTTP 429を返しました。自動再試行は行わず停止します。");
                    if (ActionButton("停止##dictionaryAutoTranslate", ButtonRole.Danger))
                    {
                        dictionaryAutoTranslateStatus = "自動翻訳を停止しています…";
                        AddDictionaryAutoTranslateLog("[停止要求] 自動翻訳の停止を要求しました。");
                        dictionaryAutoTranslateCts?.Cancel();
                    }
                }
                else if (dictionaryAutoTranslateDrafts.Count == 0)
                {
                    var translateDisabled = untranslated.Length == 0;
                    if (translateDisabled) ImGui.BeginDisabled();
                    if (ActionButton("未翻訳を自動翻訳", ButtonRole.Primary))
                    {
                        ApplyCapturedLocations(capturePlugin);
                        dictionaryAutoTranslateDrafts.Clear();
                        ClearDictionaryAutoTranslateLogs();
                        dictionaryAutoTranslateStatus = pluginInstallerModule.TranslationProviderName + "を開始しました。翻訳可能な固定UI文字列だけを処理します。";
                        AddDictionaryAutoTranslateLog($"[開始] {capturePlugin} / 未翻訳 {untranslated.Length}件");
                        var texts = untranslated.Select(x => x.Text).Distinct(StringComparer.Ordinal).ToArray();
                        var translatePlugin = capturePlugin;
                        var protectedNames = GetDictionaryProtectedNames(translatePlugin);
                        dictionaryAutoTranslateCts?.Dispose();
                        dictionaryAutoTranslateCts = new CancellationTokenSource();
                        var token = dictionaryAutoTranslateCts.Token;
                        // TranslateDictionaryBatchAsync は async だが、最初の await に到達するまでは
                        // 呼び出し元スレッド（＝描画スレッド）で同期実行される。
                        // 先頭から連続してスキップ判定される項目が続く間は await に到達しないため、
                        // そのまま呼ぶと未翻訳の件数ぶんだけゲームが固まる。
                        // スキップ判定は1件あたり正規表現を9本回すので、件数が多いと無視できない。
                        // Task.Run で最初からワーカースレッドへ逃がす。
                        dictionaryAutoTranslateTask = Task.Run(
                            () => pluginInstallerModule.TranslateDictionaryBatchAsync(
                                translatePlugin,
                                texts,
                                // Dalamud API (InstalledPlugins) をワーカースレッドから触らないよう、
                                // 固有名詞は描画スレッドで確定済みの protectedNames を渡す。
                                text => GetDictionaryAutoTranslateSkipReason(text, translatePlugin, protectedNames),
                                protectedNames,
                                AddDictionaryAutoTranslateLog,
                                token),
                            token);
                    }
                    if (translateDisabled) ImGui.EndDisabled();
                    ImGui.SameLine();
                    ImGui.TextDisabled(translateDisabled ? "自動翻訳できる未翻訳項目がありません。" : "URL・パス・コマンド・動的値・技術IDなどは除外します。");
                }

                if (dictionaryAutoTranslateDrafts.Count > 0 && dictionaryAutoTranslateTask == null)
                {
                    if (ActionButton("翻訳結果を辞書へ反映", ButtonRole.Success))
                    {
                        if (config.Plugins.TryGetValue(capturePlugin, out var draftState))
                        {
                            var applied = 0;
                            var protectedExisting = 0;
                            foreach (var pair in dictionaryAutoTranslateDrafts)
                            {
                                var alreadyTranslated = (draftState.UserOverrides.TryGetValue(pair.Key, out var existingUser) && !string.IsNullOrWhiteSpace(existingUser))
                                    || (draftState.OfficialOverrides.TryGetValue(pair.Key, out var existingOfficial) && !string.IsNullOrWhiteSpace(existingOfficial));
                                if (alreadyTranslated)
                                {
                                    protectedExisting++;
                                    continue;
                                }
                                draftState.UserOverrides[pair.Key] = pair.Value;
                                editBuffers[$"{capturePlugin}\u001f{pair.Key}"] = pair.Value;
                                applied++;
                            }
                            dictionaryAutoTranslateDrafts.Clear();
                            SaveConfig();
                            dictionaryAutoTranslateStatus = $"自動翻訳 {applied}件を辞書へ反映しました。既存訳 {protectedExisting}件は上書きせず保護しました。翻訳辞書タブで修正してからCSVへ保存できます。";
                        }
                    }
                    ImGui.SameLine();
                    if (ActionButton("自動翻訳結果を破棄", ButtonRole.Danger))
                    {
                        dictionaryAutoTranslateDrafts.Clear();
                        dictionaryAutoTranslateStatus = "自動翻訳結果を破棄しました。";
                    }
                }

                if (!string.IsNullOrWhiteSpace(dictionaryAutoTranslateStatus))
                    ImGui.TextWrapped(dictionaryAutoTranslateStatus);

                if (!dictionaryAutoTranslateLogs.IsEmpty && ImGui.CollapsingHeader("自動翻訳ログ", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    if (ActionButton("ログをコピー##dictionaryTranslateLog", ButtonRole.Primary))
                        ImGui.SetClipboardText(string.Join(Environment.NewLine, dictionaryAutoTranslateLogs.ToArray()));
                    ImGui.SameLine();
                    if (dictionaryAutoTranslateTask != null) ImGui.BeginDisabled();
                    if (ActionButton("ログをクリア##dictionaryTranslateLog", ButtonRole.Danger))
                        ClearDictionaryAutoTranslateLogs();
                    if (dictionaryAutoTranslateTask != null) ImGui.EndDisabled();
                    ImGui.BeginChild("dictionaryTranslateLogChild", new Vector2(0, 180), true);
                    foreach (var line in dictionaryAutoTranslateLogs.ToArray())
                        ImGui.TextWrapped(line);
                    ImGui.EndChild();
                }

                DrawCapturedTable($"translate_{capturePlugin}", untranslated, current, false, ref captureTargetFilter, true);
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
        ImGui.PopStyleColor(3);
    }

    private void DrawCapturedTable(string id, CapturedItem[] items, ConcurrentDictionary<string, CapturedItem> source, bool background, ref string captureFilter, bool editsDisabled = false)
    {
        ImGui.SetNextItemWidth(320);
        ImGui.InputTextWithHint($"##captureFilter_{id}", "未翻訳の英語・種類などを絞り込み", ref captureFilter, 256);

        var filterText = captureFilter;
        var visibleItems = string.IsNullOrWhiteSpace(filterText)
            ? items
            : items.Where(x => CaptureItemMatchesFilter(x, filterText)).ToArray();

        if (!string.IsNullOrWhiteSpace(captureFilter))
        {
            ImGui.SameLine();
            if (editsDisabled) ImGui.BeginDisabled();
            if (ActionButton($"検索結果を削除##captureDeleteFiltered_{id}", ButtonRole.Danger))
                ImGui.OpenPopup($"取得結果を一括削除##{id}");
            if (editsDisabled) ImGui.EndDisabled();

            if (ImGui.BeginPopupModal($"取得結果を一括削除##{id}", ImGuiWindowFlags.AlwaysAutoResize))
            {
                ImGui.TextWrapped($"検索「{captureFilter}」に一致する未翻訳取得 {visibleItems.Length} 件を一覧から削除します。\n削除した項目は翻訳辞書へ取り込まれません。");
                if (ActionButton($"削除する##captureDeleteConfirm_{id}", ButtonRole.Danger))
                {
                    foreach (var item in visibleItems)
                        RemoveCapturedItem(source, item, background);
                    csvStatus = $"未翻訳取得から {visibleItems.Length} 件削除しました。";
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();
                if (ActionButton($"キャンセル##captureDeleteCancel_{id}", ButtonRole.Neutral)) ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
            }
        }

        if (visibleItems.Length != items.Length)
            ImGui.TextDisabled($"表示 {visibleItems.Length} / {items.Length} 件");

        DrawListSortControls($"capture_{id}", new[] { "未翻訳の原文", "メニュー", "画面内項目", "種類", "回数" }, ref captureSortMode, ref captureSortAscending);
        visibleItems = captureSortMode switch
        {
            1 => SortArray(visibleItems, x => x.Menu, captureSortAscending),
            2 => SortArray(visibleItems, x => x.Section, captureSortAscending),
            3 => SortArray(visibleItems, x => x.Kind, captureSortAscending),
            4 => SortArray(visibleItems, x => x.Count, captureSortAscending),
            _ => SortArray(visibleItems, x => x.Text, captureSortAscending),
        };

        if (ImGui.BeginTable($"untranslated_{id}", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.ScrollX | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoKeepColumnsVisible, new Vector2(0, -25)))
        {
            ImGui.TableSetupColumn("メニュー", ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("画面内項目", ImGuiTableColumnFlags.WidthFixed, 150);
            ImGui.TableSetupColumn("種類", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("回数", ImGuiTableColumnFlags.WidthFixed, 55);
            ImGui.TableSetupColumn("未翻訳の原文", ImGuiTableColumnFlags.WidthFixed, 420);
            ImGui.TableSetupColumn("自動翻訳（確認用）", ImGuiTableColumnFlags.WidthFixed, 360);
            ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 62);
            DrawJapaneseTableHeaderRow(new[] { "メニュー", "画面内項目", "種類", "回数", "未翻訳の原文", "自動翻訳（確認用）", "操作" });
            foreach (var item in visibleItems)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(item.Menu);
                ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted(item.Section);
                ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(item.Kind);
                ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(item.Count.ToString());
                ImGui.TableSetColumnIndex(4); ImGui.TextWrapped(item.Text);
                ImGui.TableSetColumnIndex(5);
                if (dictionaryAutoTranslateDrafts.TryGetValue(item.Text, out var draft)) ImGui.TextWrapped(draft);
                else if (!IsDictionaryAutoTranslateCandidate(item.Text)) ImGui.TextDisabled("対象外");
                ImGui.TableSetColumnIndex(6);
                var rowId = HashCode.Combine(item.Text, item.Menu, item.Section, item.Window);
                if (editsDisabled) ImGui.BeginDisabled();
                if (ActionButton($"削除##capture_{id}_{rowId}", ButtonRole.Danger))
                {
                    RemoveCapturedItem(source, item, background);
                    csvStatus = "未翻訳取得から1件削除しました。";
                    if (editsDisabled) ImGui.EndDisabled();
                    break;
                }
                if (editsDisabled) ImGui.EndDisabled();
            }
            ImGui.EndTable();
        }
    }

    private static readonly HashSet<string> DictionaryTechnicalIdentifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Atch", "Atr", "Shp", "PBD", "Pbd", "Meta", "Tex", "Mdl", "Mtrl", "Avfx", "Tmb", "Pap", "Sklb", "Skp", "chinese", "cjk", "font"
    };

    private bool IsDictionaryAutoTranslateCandidate(string text)
        => GetDictionaryAutoTranslateSkipReason(text, capturePlugin) == null;

    // protectedNames を渡すと GetDictionaryProtectedNames() を呼ばない。
    // 自動翻訳はワーカースレッドで走るため、そこから Dalamud の InstalledPlugins を
    // 触らないよう、描画スレッドで確定させた値を渡せるようにしている。
    private string? GetDictionaryAutoTranslateSkipReason(string text, string pluginName, IReadOnlyCollection<string>? protectedNames = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return "空文字";
        var value = text.Trim();
        if (ContainsJapanese(value)) return "すでに日本語を含む";
        if (DictionaryTechnicalIdentifiers.Contains(value)) return "技術識別子";
        if (value.Length < 2) return "短すぎる文字列";
        if (value.Length > 1200) return "長すぎる文字列";
        if (Regex.IsMatch(value, @"^https?://\S+$", RegexOptions.IgnoreCase)) return "URL";
        // A pure slash command is excluded, but a command help line with ':' remains
        // translatable so only the command prefix is protected and the description is translated.
        if (!value.Contains(':') && Regex.IsMatch(value, @"^/[A-Za-z0-9_\-]+(?:\s+\S+)*$")) return "コマンド";
        if (Regex.IsMatch(value, @"^[A-Za-z]:[\\/]" ) || value.StartsWith("\\", StringComparison.Ordinal)) return "ローカルパス";
        if (Regex.IsMatch(value, @"^(?:\d+[\s:/.,%+\-]*)+$")) return "動的な数値";
        if (Regex.IsMatch(value, @"^[A-Za-z0-9_.:+#\-]{1,24}$") && !value.Contains(' ')) return "短い内部ID";
        if (Regex.IsMatch(value, @"(?:^|[\\/])(?:common|chara|ui|bgcommon|vfx|shader|sound)/", RegexOptions.IgnoreCase)) return "FFXIV内部ファイルパス";
        if (Regex.IsMatch(value, @"^(?:by|author|created by|developer|maintainer)\s*[:\-]?\s*.+$", RegexOptions.IgnoreCase)) return "作者・クレジット表記";

        foreach (var protectedName in protectedNames ?? GetDictionaryProtectedNames(pluginName))
            if (string.Equals(value, protectedName, StringComparison.OrdinalIgnoreCase)) return "プラグイン名・固有名詞";

        return value.Any(char.IsLetter) ? null : "翻訳対象となる文字がない";
    }

    private IReadOnlyCollection<string> GetDictionaryProtectedNames(string pluginName)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(pluginName)) names.Add(pluginName.Trim());
        var installed = pluginInterface.InstalledPlugins.FirstOrDefault(p => string.Equals(p.InternalName, pluginName, StringComparison.OrdinalIgnoreCase));
        if (installed != null && !string.IsNullOrWhiteSpace(installed.Name)) names.Add(installed.Name.Trim());
        return names.ToArray();
    }

    private void AddDictionaryAutoTranslateLog(string message)
    {
        dictionaryAutoTranslateLogs.Enqueue($"[{DateTime.Now:HH:mm:ss}] {message}");
        while (dictionaryAutoTranslateLogs.Count > MaxDictionaryAutoTranslateLogs && dictionaryAutoTranslateLogs.TryDequeue(out _)) { }
    }

    private void ClearDictionaryAutoTranslateLogs()
    {
        while (dictionaryAutoTranslateLogs.TryDequeue(out _)) { }
    }

    private enum BackgroundCaptureClass
    {
        TargetCandidate,
        OtherPlugin,
        Unknown,
    }

    private BackgroundCaptureClass ClassifyBackgroundItem(CapturedItem item, string targetPlugin)
    {
        var window = item.Window ?? string.Empty;
        if (string.IsNullOrWhiteSpace(window)) return BackgroundCaptureClass.Unknown;

        var targetNames = GetDictionaryProtectedNames(targetPlugin);
        foreach (var name in targetNames)
            if (name.Length >= 3 && window.Contains(name, StringComparison.OrdinalIgnoreCase))
                return BackgroundCaptureClass.TargetCandidate;

        if (config.Plugins.TryGetValue(targetPlugin, out var targetState) && !string.IsNullOrWhiteSpace(targetState.WindowKeyword))
        {
            foreach (var keyword in targetState.WindowKeyword.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (keyword.Length < 4 || keyword.Equals("Configuration", StringComparison.OrdinalIgnoreCase) || keyword.Equals("Settings", StringComparison.OrdinalIgnoreCase)) continue;
                if (window.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return BackgroundCaptureClass.TargetCandidate;
            }
        }

        foreach (var plugin in pluginInterface.InstalledPlugins)
        {
            if (string.Equals(plugin.InternalName, targetPlugin, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(plugin.Name) && plugin.Name.Length >= 3 && window.Contains(plugin.Name, StringComparison.OrdinalIgnoreCase))
                return BackgroundCaptureClass.OtherPlugin;
            if (!string.IsNullOrWhiteSpace(plugin.InternalName) && plugin.InternalName.Length >= 3 && window.Contains(plugin.InternalName, StringComparison.OrdinalIgnoreCase))
                return BackgroundCaptureClass.OtherPlugin;
        }

        return BackgroundCaptureClass.Unknown;
    }

    private void MoveBackgroundItemToTarget(CapturedItem item, string targetPlugin)
    {
        EnsureCaptureDictionary(targetPlugin);
        var target = pluginCaptured[targetPlugin];
        var targetKey = string.Concat("", "\u001f", "", "\u001f", item.Text);
        target.AddOrUpdate(targetKey,
            _ => item with { Menu = string.Empty, Section = string.Empty },
            (_, old) => old with { Count = old.Count + item.Count, Kind = MergeKind(old.Kind, item.Kind) });
        RemoveCapturedItem(backgroundCaptured, item, true);
    }

    private static bool CaptureItemMatchesFilter(CapturedItem item, string searchText)
        => item.Text.Contains(searchText, StringComparison.OrdinalIgnoreCase)
        || item.Kind.Contains(searchText, StringComparison.OrdinalIgnoreCase)
        || item.Menu.Contains(searchText, StringComparison.OrdinalIgnoreCase)
        || item.Section.Contains(searchText, StringComparison.OrdinalIgnoreCase)
        || item.Window.Contains(searchText, StringComparison.OrdinalIgnoreCase);

    private static void RemoveCapturedItem(ConcurrentDictionary<string, CapturedItem> source, CapturedItem item, bool background)
    {
        var key = background
            ? string.Concat(item.Window, "\u001f", item.Text)
            : string.Concat(item.Menu, "\u001f", item.Section, "\u001f", item.Text);
        source.TryRemove(key, out _);
    }

    private static bool ContainsJapanese(string text)
    {
        // v0.0.28: 漢字だけでは中国語と区別できないため、ひらがな/カタカナを含む場合だけ日本語扱いする。
        foreach (var c in text)
        {
            if (c >= '\u3040' && c <= '\u30ff') return true;
        }
        return false;
    }

    private bool IsKnown(string source)
    {
        var visible = source; var marker = source.IndexOf("##", StringComparison.Ordinal); if (marker > 0) visible = source[..marker];
        if (KeepAsIs.Labels.Contains(visible)) return true;
        if (ContainsJapanese(visible)) return true;
        if (source.Contains("##Up", StringComparison.Ordinal) || source.Contains("##Down", StringComparison.Ordinal) ||
            source.Contains("#####up", StringComparison.OrdinalIgnoreCase) || source.Contains("#####down", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("Rotation Solver Reborn Remove Territory", StringComparison.Ordinal)) return true;
        if (visible.StartsWith("RSR has helped you by clicking actions ", StringComparison.Ordinal) && visible.EndsWith(" times.", StringComparison.Ordinal)) return true;
        foreach (var (name, state) in GetPluginConfigSnapshot())
        {
            if (state.DeletedKeys.Contains(visible)) continue;
            if (GetActiveStandardDictionary(name).ContainsKey(visible) || state.OfficialOverrides.ContainsKey(visible) || state.UserOverrides.ContainsKey(visible)) return true;
        }
        return false;
    }

    private string BuildUntranslatedExport(string pluginName)
    {
        var sb = new StringBuilder(); sb.AppendLine($"# Plugin JP Helper {pluginName} untranslated v0.1.0");
        foreach (var item in pluginCaptured[pluginName].Values.Where(x => !IsKnown(x.Text)).OrderBy(x => x.Text, StringComparer.OrdinalIgnoreCase))
            sb.Append(item.Menu).Append('\t').Append(item.Section).Append('\t').Append(item.Kind).Append('\t').Append(item.Count).Append('\t').AppendLine(item.Text);
        return sb.ToString();
    }

    private string BuildBackgroundExport()
    {
        var sb = new StringBuilder(); sb.AppendLine("# Plugin JP Helper background v0.1.0");
        foreach (var item in backgroundCaptured.Values.Where(x => !IsKnown(x.Text)).OrderBy(x => x.Text, StringComparer.OrdinalIgnoreCase))
            sb.Append(item.Window).Append('\t').Append(item.Kind).Append('\t').Append(item.Count).Append('\t').AppendLine(item.Text);
        return sb.ToString();
    }

    private IReadOnlyDictionary<string, string> GetActiveStandardDictionary(string pluginName)
        => config.CleanSlateMode || !StandardDictionaries.ByPlugin.TryGetValue(pluginName, out var dict)
            ? EmptyDictionary
            : dict;

    private static readonly IReadOnlyDictionary<string, string> EmptyDictionary
        = new Dictionary<string, string>(StringComparer.Ordinal);

    private void FullResetAllData()
    {
        captureEnabled = false;
        baselineCaptureEnabled = false;
        backgroundCaptured.Clear();
        foreach (var dict in pluginCaptured.Values) dict.Clear();
        editBuffers.Clear();
        seenWindows.Clear();
        liveWindowLastSeen.Clear();
        foreach (var state in GetPluginConfigSnapshot().Select(x => x.Value))
        {
            state.UserOverrides.Clear();
            state.Locations.Clear();
            state.Enabled = false;
        }
        config.Plugins["RSR"].Enabled = true;
        config.CleanSlateMode = true;
        config.CaptureSchemaVersion = 3;
        config.DataResetVersion = 24;
        RsrNavigationTracker.Reset();
        comboOpenDepth = 0;
        SaveConfig();
    }


    private void EnsureCaptureDictionaries()
    {
        foreach (var name in GetPluginConfigSnapshot().Select(x => x.Key)) EnsureCaptureDictionary(name);
    }

    private void EnsureCaptureDictionary(string name)
    {
        if (!pluginCaptured.ContainsKey(name)) pluginCaptured[name] = new ConcurrentDictionary<string, CapturedItem>(StringComparer.Ordinal);
    }

    private static int PluginSortKey(string name) => PluginProfileRegistry.SortKey(name);

    private bool IsTranslatedShadowKey(string pluginName, string key)
    {
        // RenderText段階の翻訳後文字列が取得ログへ回り込み、
        // 「原文」候補としてLocationsへ残ることがある。RSRでは標準辞書の
        // 日本語訳と完全一致するだけのキーは原文ではないので一覧から除外する。
        // 標準辞書の正式なキーそのものは必ず残す。
        if (!string.Equals(pluginName, "RSR", StringComparison.Ordinal)) return false;
        var standard = GetActiveStandardDictionary(pluginName);
        if (standard.ContainsKey(key)) return false;

        var matchCount = 0;
        foreach (var kv in standard)
        {
            if (!string.Equals(kv.Value, key, StringComparison.Ordinal)) continue;
            if (++matchCount > 0) return true;
        }
        return false;
    }

    private IEnumerable<KeyValuePair<string, string>> GetDictionaryCatalog(string pluginName)
        => GetDictionaryCatalogSnapshot(pluginName);

    private KeyValuePair<string, string>[] GetDictionaryCatalogSnapshot(string pluginName)
    {
        if (dictionaryCatalogCache.TryGetValue(pluginName, out var cached))
            return cached;

        if (!config.Plugins.TryGetValue(pluginName, out var state))
            return Array.Empty<KeyValuePair<string, string>>();

        var standard = GetActiveStandardDictionary(pluginName);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in state.Locations.Keys) keys.Add(key);
        foreach (var key in state.UserOverrides.Keys) keys.Add(key);
        foreach (var key in state.OfficialOverrides.Keys) keys.Add(key);
        foreach (var key in standard.Keys) keys.Add(key);

        var rows = new List<KeyValuePair<string, string>>(keys.Count);
        foreach (var key in keys)
        {
            if (state.DeletedKeys.Contains(key)) continue;
            if (IsTranslatedShadowKey(pluginName, key)) continue;
            var fallback = state.OfficialOverrides.TryGetValue(key, out var officialJa) && !string.IsNullOrWhiteSpace(officialJa) ? officialJa : (standard.TryGetValue(key, out var ja) ? ja : string.Empty);
            rows.Add(new KeyValuePair<string, string>(key, fallback));
        }

        var snapshot = rows.ToArray();
        dictionaryCatalogCache[pluginName] = snapshot;
        return snapshot;
    }

    private Dictionary<string, int> GetDictionaryMenuCounts(string pluginName)
    {
        if (dictionaryMenuCountCache.TryGetValue(pluginName, out var cached))
            return cached;

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var kv in GetDictionaryCatalogSnapshot(pluginName))
        {
            var menu = pluginName == "RSR" ? GetRsrMenuCategory(kv.Key) : string.Empty;
            counts[menu] = counts.TryGetValue(menu, out var n) ? n + 1 : 1;
        }

        dictionaryMenuCountCache[pluginName] = counts;
        return counts;
    }

    private void InvalidateDictionaryUiCache()
    {
        dictionaryCatalogCache.Clear();
        dictionaryMenuCountCache.Clear();
    }

    private static string NormalizePluginIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    private static IReadOnlyList<string> GetPluginIdentityAliases(string? value)
    {
        var normalized = NormalizePluginIdentity(value);
        return normalized switch
        {
            "rsr" or "rotationsolverreborn" => new[] { "rsr", "rotationsolverreborn" },
            "bmr" or "bossmodreborn" => new[] { "bmr", "bossmodreborn" },
            "bm" or "bossmod" => new[] { "bm", "bossmod" },
            "marketboard" or "marketboardplugin" => new[] { "marketboard", "marketboardplugin" },
            _ => string.IsNullOrEmpty(normalized) ? Array.Empty<string>() : new[] { normalized },
        };
    }

    private static bool PluginIdentityMatches(string? left, string? right)
    {
        var leftAliases = GetPluginIdentityAliases(left);
        var rightAliases = GetPluginIdentityAliases(right);
        return leftAliases.Any(x => rightAliases.Contains(x, StringComparer.Ordinal));
    }

    private bool IsInstalledPluginAlreadyRegistered(string? internalName, string? displayName)
    {
        foreach (var (registeredName, state) in GetPluginConfigSnapshot())
        {
            if (state == null || !state.TranslationTarget) continue;

            if (PluginIdentityMatches(registeredName, internalName)
                || PluginIdentityMatches(registeredName, displayName))
                return true;

            // 手入力登録などでキー名が異なる場合は、保存済みのウィンドウキーワードも照合する。
            var keywords = (state.WindowKeyword ?? string.Empty)
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (keywords.Any(keyword => PluginIdentityMatches(keyword, internalName)
                || PluginIdentityMatches(keyword, displayName)))
                return true;
        }
        return false;
    }

    private void AddInstalledPluginTarget()
    {
        if (string.IsNullOrWhiteSpace(installedPluginSelection))
        {
            csvStatus = "追加するインストール済みプラグインを選択してください。";
            return;
        }

        var plugin = pluginInterface.InstalledPlugins.FirstOrDefault(p => string.Equals(p.InternalName, installedPluginSelection, StringComparison.Ordinal));
        if (plugin == null)
        {
            csvStatus = "選択したプラグインをインストール済み一覧から取得できませんでした。";
            return;
        }

        var key = plugin.InternalName;
        if (!config.Plugins.TryGetValue(key, out var state))
        {
            state = new PluginDictionaryState();
            lock (pluginConfigSync)
            config.Plugins[key] = state;
        }

        state.TranslationTarget = true;
        state.Enabled = true;
        var keywords = new[] { plugin.Name?.Trim(), plugin.InternalName?.Trim() }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ArtisanBehavior.MatchesPluginName(key) || ArtisanBehavior.MatchesPluginName(plugin.Name))
            ArtisanBehavior.EnsureWindowKeywords(keywords);
        state.WindowKeyword = string.Join("|", keywords);
        EnsureCaptureDictionary(key);
        selectedPlugin = capturePlugin = key;
        SaveConfig();
        csvStatus = $"{plugin.Name} ({plugin.InternalName}) を翻訳対象に追加しました。";
    }

    private void AddCustomPlugin()
    {
        var name = customPluginName.Trim();
        var keyword = customWindowKeyword.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(keyword))
        {
            csvStatus = "追加にはプラグイン名とウィンドウ名キーワードの両方が必要です。";
            return;
        }
        if (!config.Plugins.TryGetValue(name, out var state))
        {
            state = new PluginDictionaryState();
            lock (pluginConfigSync)
            config.Plugins[name] = state;
        }
        state.WindowKeyword = keyword;
        state.Enabled = true;
        state.TranslationTarget = true;
        EnsureCaptureDictionary(name);
        selectedPlugin = capturePlugin = name;
        customPluginName = customWindowKeyword = string.Empty;
        SaveConfig();
        csvStatus = $"対象 {name} を追加しました。";
    }

    private string CsvPath(string pluginName)
    {
        var safe = string.Concat(pluginName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var dir = Path.Combine(pluginInterface.ConfigDirectory.FullName, "Dictionaries");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, safe + ".csv");
    }

    private string DictionaryDirectory()
    {
        var dir = Path.Combine(pluginInterface.ConfigDirectory.FullName, "Dictionaries");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string OpenDictionaryFolder()
    {
        try
        {
            var dir = DictionaryDirectory();
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            return $"保存フォルダーを開きました: {dir}";
        }
        catch (Exception ex) { return $"保存フォルダーを開けませんでした: {ex.Message}"; }
    }

    private static int GetInventoryToolsPatchVersion(string path)
    {
        const string prefix = "InventoryTools_JP_patch_v";
        var name = Path.GetFileNameWithoutExtension(path);
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return -1;
        return int.TryParse(name[prefix.Length..], out var version) ? version : -1;
    }

    private bool EnsureLatestBundledInventoryToolsCsv()
    {
        try
        {
            const string pluginName = InventoryToolsBehavior.PluginName;

            if (!config.Plugins.TryGetValue(pluginName, out var state) || state == null)
            {
                state = new PluginDictionaryState
                {
                    Enabled = true,
                    TranslationTarget = true,
                    WindowKeyword = "Configuration",
                };
                lock (pluginConfigSync)
            config.Plugins[pluginName] = state;
                EnsureCaptureDictionary(pluginName);
            }
            else
            {
                state.Enabled = true;
                state.TranslationTarget = true;
            }

            // typeof(Plugin).Assembly.Location は使わない。
            // Dalamud は Dev Plugin をメモリ上へ読み込むため、その場合 Location は空文字になる。
            // 空文字のまま Directory.GetFiles へ渡すと
            // ArgumentException: The path is empty. で落ちる。
            // Dalamud が保持しているロード元パスを使う。
            var assemblyDir = pluginInterface.AssemblyLocation.DirectoryName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(assemblyDir) || !Directory.Exists(assemblyDir))
            {
                log.Warning($"[PluginJPHelper] InventoryTools bundled CSV import skipped: assembly directory is unavailable: {assemblyDir}");
                return false;
            }

            var latest = Directory
                .GetFiles(assemblyDir, "InventoryTools_JP_patch_v*.csv", SearchOption.TopDirectoryOnly)
                .Select(path => new { Path = path, Version = GetInventoryToolsPatchVersion(path) })
                .Where(x => x.Version >= 0)
                .OrderByDescending(x => x.Version)
                .FirstOrDefault();

            if (latest == null)
            {
                log.Warning($"[PluginJPHelper] InventoryTools bundled patch CSV not found in: {assemblyDir}");
                return false;
            }

            var fileName = Path.GetFileName(latest.Path);
            var configCsvPath = Path.Combine(DictionaryDirectory(), fileName);
            var alreadyImported = File.Exists(configCsvPath)
                && string.Equals(
                    Path.GetFileName(state.LastCsvPath ?? string.Empty),
                    fileName,
                    StringComparison.OrdinalIgnoreCase);

            if (alreadyImported)
            {
                log.Information($"[PluginJPHelper] InventoryTools bundled CSV already imported: {fileName}");
                return true;
            }

            File.Copy(latest.Path, configCsvPath, true);
            var result = ImportDictionaryCsv(pluginName, configCsvPath);
            if (result.StartsWith("CSV読込失敗", StringComparison.Ordinal)
                || result.StartsWith("CSVがありません", StringComparison.Ordinal))
            {
                log.Warning($"[PluginJPHelper] InventoryTools bundled CSV import failed: {result}");
                return false;
            }

            log.Information($"[PluginJPHelper] InventoryTools latest bundled CSV imported: v{latest.Version} / {result}");
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[PluginJPHelper] InventoryTools bundled CSV import failed");
            return false;
        }
    }

    private void EnsureInitialRsrCsv()
    {
        try
        {
            var path = CsvPath("RSR");
            if (!File.Exists(path)) _ = ExportDictionaryCsv("RSR");
        }
        catch (Exception ex) { log.Warning(ex, "RSR初期CSVの作成に失敗しました"); }
    }

    private string ExportDictionaryCsv(string pluginName) => ExportDictionaryCsv(pluginName, CsvPath(pluginName));

    private string ExportDictionaryCsv(string pluginName, string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            var state = config.Plugins[pluginName];
            using var sw = new StreamWriter(fullPath, false, new UTF8Encoding(true));
            sw.WriteLine("Plugin,Menu,Section,Type,English,Japanese");

            // 別ウィンドウ関連付けも辞書に含める。
            // このCSVを別PC/別ユーザーで読み込んだ時も自動適用できる。
            foreach (var keyword in (state.WindowKeyword ?? string.Empty)
                         .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                sw.WriteLine(string.Join(',', new[]
                {
                    pluginName, string.Empty, string.Empty, DictionaryWindowMetaType, keyword, string.Empty
                }.Select(CsvEscape)));
            }

            foreach (var kv in GetDictionaryCatalog(pluginName).OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                state.Locations.TryGetValue(kv.Key, out var loc);
                var ja = state.UserOverrides.TryGetValue(kv.Key, out var ov) ? ov : (state.OfficialOverrides.TryGetValue(kv.Key, out var officialJa) ? officialJa : kv.Value);
                sw.WriteLine(string.Join(',', new[] { pluginName, loc?.Menu ?? string.Empty, loc?.Section ?? string.Empty, string.Empty, kv.Key, ja }.Select(CsvEscape)));
            }
            state.LastCsvPath = fullPath;
            SaveConfig();
            return $"CSVを書き出しました: {fullPath}";
        }
        catch (Exception ex) { return $"CSV書き出し失敗: {ex.Message}"; }
    }

    private string ReloadDictionaryCsv(string pluginName)
    {
        var state = config.Plugins[pluginName];
        if (string.IsNullOrWhiteSpace(state.LastCsvPath)) return "先にCSVを選択してください。";

        var path = state.LastCsvPath;
        if (File.Exists(path)) return ImportDictionaryCsv(pluginName, path);

        // 「再読み込み」対象のCSVが外部で削除されている場合は、
        // そのプラグインの読み込み済みユーザー辞書を空にして、削除済みCSVを指定し続けない。
        // 標準辞書・公式辞書・未翻訳取得結果・他プラグインの状態には触れない。
        state.UserOverrides.Clear();
        state.Locations.Clear();
        state.LastCsvPath = string.Empty;
        editBuffers.Clear();
        SaveConfig();
        return $"CSVがありません: {path} / 読み込み済みのユーザー辞書をクリアしました。";
    }

    private string ImportDictionaryCsv(string pluginName) => ImportDictionaryCsv(pluginName, CsvPath(pluginName));

    private string ImportDictionaryCsv(string pluginName, string path)
    {
        try
        {
            if (!File.Exists(path)) return $"CSVがありません: {path}";
            var csvText = File.ReadAllText(path, Encoding.UTF8);
            var rows = ParseCsvRecords(csvText);
            var state = config.Plugins[pluginName];

            var csvWindows = ExtractDictionaryWindowKeywords(rows);

            // v0.4.9:
            // ユーザーが「CSVを選択して読み込む」を実行した場合は明示操作とみなし、
            // 過去に手動削除して抑止されていた @Window でも、このCSVに含まれていれば
            // 抑止を解除して改めて自動適用する。
            state.SuppressedDictionaryWindowKeywords ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var keyword in csvWindows)
                state.SuppressedDictionaryWindowKeywords.Remove(keyword);

            ApplyDictionaryWindowMetadata(pluginName, state, csvWindows, "CSV辞書");

            var imported = 0;
            foreach (var cols in rows.Skip(1))
            {
                if (cols.Count < 6) continue;
                if (IsDictionaryWindowMetaRow(cols)) continue;
                var english = cols[4];
                var japanese = cols[5];
                if (string.IsNullOrWhiteSpace(english)) continue;
                state.DeletedKeys.Remove(english);
                if (string.IsNullOrWhiteSpace(japanese)) state.UserOverrides.Remove(english);
                else state.UserOverrides[english] = japanese;
                if (!string.IsNullOrWhiteSpace(cols[1]) || !string.IsNullOrWhiteSpace(cols[2]))
                    state.Locations[english] = new DictionaryLocation { Menu = cols[1], Section = cols[2] };
                imported++;
            }
            state.LastCsvPath = Path.GetFullPath(path);
            editBuffers.Clear();
            SaveConfig();
            return $"CSVを読み込みました: {imported}件 / {state.LastCsvPath}";
        }
        catch (Exception ex) { return $"CSV読込失敗: {ex.Message}"; }
    }

    private sealed record HookDiagnosticEntry(
        string Time,
        string Kind,
        string Text,
        string Window,
        string Owner,
        string CapturePlugin,
        bool IsCaptureTarget);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int StructSize;
        public IntPtr Owner;
        public IntPtr Instance;
        public string? Filter;
        public string? CustomFilter;
        public int MaxCustomFilter;
        public int FilterIndex;
        public IntPtr File;
        public int MaxFile;
        public IntPtr FileTitle;
        public int MaxFileTitle;
        public string? InitialDir;
        public string? Title;
        public int Flags;
        public short FileOffset;
        public short FileExtension;
        public string? DefaultExt;
        public IntPtr CustomData;
        public IntPtr Hook;
        public string? TemplateName;
        public IntPtr ReservedPtr;
        public int ReservedInt;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileName(ref OpenFileName ofn);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSaveFileName(ref OpenFileName ofn);

    private string? ShowCsvOpenDialog()
    {
        const int maxChars = 32768;
        var buffer = Marshal.AllocHGlobal(maxChars * sizeof(char));
        try
        {
            // Unicode NUL で初期化。
            for (var i = 0; i < maxChars; i++) Marshal.WriteInt16(buffer, i * sizeof(char), 0);
            var ofn = new OpenFileName
            {
                StructSize = Marshal.SizeOf<OpenFileName>(),
                Owner = IntPtr.Zero,
                Filter = "CSVファイル (*.csv)\0*.csv\0すべてのファイル (*.*)\0*.*\0\0",
                FilterIndex = 1,
                File = buffer,
                MaxFile = maxChars,
                InitialDir = DictionaryDirectory(),
                Title = $"{selectedPlugin} に読み込むCSVを選択",
                DefaultExt = "csv",
                // OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR
                Flags = 0x00080000 | 0x00001000 | 0x00000800 | 0x00000008,
            };
            if (!GetOpenFileName(ref ofn)) return null;
            return Marshal.PtrToStringUni(buffer);
        }
        catch (Exception ex)
        {
            csvStatus = $"CSV選択画面を開けませんでした: {ex.Message}";
            return null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private string? ShowCsvSaveDialog(string pluginName, string? currentPath)
    {
        const int maxChars = 32768;
        var buffer = Marshal.AllocHGlobal(maxChars * sizeof(char));
        try
        {
            for (var i = 0; i < maxChars; i++) Marshal.WriteInt16(buffer, i * sizeof(char), 0);
            var initialName = !string.IsNullOrWhiteSpace(currentPath) ? Path.GetFileName(currentPath) : $"{pluginName}.csv";
            var chars = (initialName + "\0").ToCharArray();
            Marshal.Copy(chars, 0, buffer, Math.Min(chars.Length, maxChars));

            var initialDir = !string.IsNullOrWhiteSpace(currentPath) ? Path.GetDirectoryName(currentPath) : null;
            if (string.IsNullOrWhiteSpace(initialDir) || !Directory.Exists(initialDir)) initialDir = DictionaryDirectory();

            var ofn = new OpenFileName
            {
                StructSize = Marshal.SizeOf<OpenFileName>(),
                Owner = IntPtr.Zero,
                Filter = "CSVファイル (*.csv)\0*.csv\0すべてのファイル (*.*)\0*.*\0\0",
                FilterIndex = 1,
                File = buffer,
                MaxFile = maxChars,
                InitialDir = initialDir,
                Title = $"{pluginName} の翻訳CSVを名前を付けて保存",
                DefaultExt = "csv",
                Flags = 0x00080000 | 0x00000800 | 0x00000008 | 0x00000002,
            };
            if (!GetSaveFileName(ref ofn)) return null;
            return Marshal.PtrToStringUni(buffer);
        }
        catch (Exception ex)
        {
            csvStatus = $"CSV保存画面を開けませんでした: {ex.Message}";
            return null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string CsvEscape(string value) => "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";

    private static List<List<string>> ParseCsvRecords(string text)
    {
        var records = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(c);
                }
                continue;
            }

            if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c == '\r' || c == '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString());
                field.Clear();

                if (row.Any(x => x.Length > 0))
                    records.Add(row);

                row = new List<string>();
            }
            else
            {
                field.Append(c);
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            if (row.Any(x => x.Length > 0))
                records.Add(row);
        }

        return records;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else
            {
                if (c == '"') quoted = true;
                else if (c == ',') { result.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
        }
        result.Add(sb.ToString());
        return result;
    }

    private bool IsContextMenuPluginTranslationEnabled(string pluginName)
    {
        foreach (var (registeredName, state) in GetPluginConfigSnapshot())
        {
            if (state == null || !state.TranslationTarget || !state.Enabled) continue;
            if (PluginIdentityMatches(registeredName, pluginName)) return true;

            var keywords = (state.WindowKeyword ?? string.Empty)
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (keywords.Any(keyword => PluginIdentityMatches(keyword, pluginName))) return true;
        }
        return false;
    }

    private void OnContextMenuOpened(IMenuOpenedArgs args)
    {
        try
        {
            // Dalamud's MenuOpenedArgs keeps SelectedItems.Add in this private delegate.
            // The delegate target is the actual List<IMenuItem> that will be rendered immediately after this event.
            var property = args.GetType().GetProperty("OnAddMenuItem", BindingFlags.Instance | BindingFlags.NonPublic);
            if (property?.GetValue(args) is not Action<MenuItem> addMenuItem
                || addMenuItem.Target is not IEnumerable currentItems)
            {
                log.Debug("[PluginJPHelper][ContextMenuData] Could not access current menu item list.");
                return;
            }

            var items = currentItems.Cast<object>().OfType<IMenuItem>().ToArray();
            var translated = 0;

            foreach (var item in items)
            {
                var original = item.Name.TextValue;
                if (string.IsNullOrEmpty(original) || item.Prefix is not { } prefix) continue;

                foreach (var entry in ContextMenuTranslations)
                {
                    if (prefix != entry.Prefix) continue;
                    if (!string.Equals(original, entry.English, StringComparison.Ordinal)) continue;

                    var enabled = IsContextMenuPluginTranslationEnabled(entry.PluginName);
                    log.Information(
                        "[PluginJPHelper][ContextMenuData] Candidate Plugin={Plugin} Enabled={Enabled} Prefix={Prefix} English=\"{English}\"",
                        entry.PluginName, enabled, (int)entry.Prefix, entry.English);
                    if (!enabled) break;

                    if (string.Equals(entry.PluginName, ArtisanBehavior.PluginName, StringComparison.Ordinal)
                        && string.Equals(entry.English, "Artisan Crafting List", StringComparison.Ordinal))
                    {
                        WrapArtisanCraftingListSubmenu(item);
                    }

                    item.Name = (SeString)entry.Japanese;
                    translated++;
                    Interlocked.Increment(ref translatedCount);
                    log.Information(
                        "[PluginJPHelper][ContextMenuData] Translated Plugin={Plugin} Prefix={Prefix} English=\"{English}\" Japanese=\"{Japanese}\"",
                        entry.PluginName, (int)entry.Prefix, entry.English, entry.Japanese);
                    break;
                }
            }

            log.Debug("[PluginJPHelper][ContextMenuData] ObservedItems={Count} Translated={Translated}", items.Length, translated);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[PluginJPHelper][ContextMenuData] Translation failed");
        }
    }


    private void WrapArtisanCraftingListSubmenu(IMenuItem item)
    {
        var originalClicked = item.OnClicked;
        if (originalClicked == null) return;

        item.OnClicked = clickedArgs =>
        {
            var proxy = new ContextMenuClickedArgsProxy(clickedArgs, TranslateArtisanSubmenuItems);
            originalClicked(proxy);
        };
    }

    private IReadOnlyList<IMenuItem> TranslateArtisanSubmenuItems(IReadOnlyList<IMenuItem> items)
    {
        if (!IsContextMenuPluginTranslationEnabled(ArtisanBehavior.PluginName)) return items;

        var translated = 0;
        foreach (var item in items)
        {
            if (item.Prefix is not SeIconChar.BoxedLetterA) continue;
            var original = item.Name.TextValue;
            if (!ArtisanBehavior.SubmenuTranslations.TryGetValue(original, out var japanese)) continue;

            item.Name = (SeString)japanese;
            translated++;
            Interlocked.Increment(ref translatedCount);
            log.Information(
                "[PluginJPHelper][ContextMenuData] TranslatedSubmenu Plugin={Plugin} English=\"{English}\" Japanese=\"{Japanese}\"",
                ArtisanBehavior.PluginName, original, japanese);
        }

        log.Debug("[PluginJPHelper][ContextMenuData] ArtisanSubmenu ObservedItems={Count} Translated={Translated}", items.Count, translated);
        return items;
    }

    private sealed class ContextMenuClickedArgsProxy : IMenuItemClickedArgs
    {
        private readonly IMenuItemClickedArgs inner;
        private readonly Func<IReadOnlyList<IMenuItem>, IReadOnlyList<IMenuItem>> translateItems;

        public ContextMenuClickedArgsProxy(
            IMenuItemClickedArgs inner,
            Func<IReadOnlyList<IMenuItem>, IReadOnlyList<IMenuItem>> translateItems)
        {
            this.inner = inner;
            this.translateItems = translateItems;
        }

        public IReadOnlySet<nint> EventInterfaces => inner.EventInterfaces;
        public string? AddonName => inner.AddonName;
        public nint AddonPtr => inner.AddonPtr;
        public nint AgentPtr => inner.AgentPtr;
        public ContextMenuType MenuType => inner.MenuType;
        public MenuTarget Target => inner.Target;

        public void OpenSubmenu(SeString name, IReadOnlyList<IMenuItem> items)
            => inner.OpenSubmenu(name, translateItems(items));

        public void OpenSubmenu(IReadOnlyList<IMenuItem> items)
            => inner.OpenSubmenu(translateItems(items));
    }

    private void SaveConfig()
    {
        InvalidateDictionaryUiCache();
        MojibakeSafety.Configure(config);
        pluginInterface.SavePluginConfig(config);
    }

    public void Dispose()
    {
        captureEnabled = false; baselineCaptureEnabled = false;
        contextMenu.OnMenuOpened -= OnContextMenuOpened;
        pluginInterface.UiBuilder.Draw -= ProcessMojibakeSafetyIncidents; pluginInterface.UiBuilder.Draw -= pluginInstallerModule.Tick; pluginInterface.UiBuilder.Draw -= windowSystem.Draw; pluginInterface.UiBuilder.Draw -= DrainMainThreadWork; pluginInterface.UiBuilder.OpenConfigUi -= OpenUi; pluginInterface.UiBuilder.OpenMainUi -= OpenUi; commandManager.RemoveHandler(Command);
        windowSystem.RemoveAllWindows();
        pluginInstallerModule.Dispose();
        officialDictionaryHttp.Dispose();
        tableSetupColumnHook?.Dispose(); sliderIntHook?.Dispose(); beginMenuHook?.Dispose(); menuItemBoolPtrHook?.Dispose(); menuItemBoolHook?.Dispose(); beginTabItemHook?.Dispose(); drawListAddTextFontPtrHook?.Dispose(); drawListAddTextVec2Hook?.Dispose(); collapsingHeaderBoolPtrHook?.Dispose(); collapsingHeaderTreeNodeFlagsHook?.Dispose(); renderTextClippedHook?.Dispose(); renderTextWrappedHook?.Dispose(); renderTextHook?.Dispose(); bulletTextHook?.Dispose(); textWrappedHook?.Dispose(); treeNodeExStrHook?.Dispose(); treeNodeStrHook?.Dispose(); radioButtonIntPtrHook?.Dispose(); radioButtonBoolHook?.Dispose(); endPopupHook?.Dispose(); beginPopupModalHook?.Dispose(); beginPopupHook?.Dispose(); endHook?.Dispose(); beginHook?.Dispose(); separatorTextHook?.Dispose(); endComboHook?.Dispose(); beginComboHook?.Dispose(); comboFnPtrHook?.Dispose(); comboStrHook?.Dispose(); comboStrArrHook?.Dispose(); selectablePtrHook?.Dispose(); selectableHook?.Dispose(); buttonExHook?.Dispose(); buttonHook?.Dispose(); checkboxHook?.Dispose(); textDisabledVHook?.Dispose(); textColoredVHook?.Dispose(); textVHook?.Dispose(); textHook?.Dispose();
    }

    private sealed record ContextMenuTranslationEntry(string PluginName, SeIconChar Prefix, string English, string Japanese);
    private sealed record CapturedItem(string Text, string Kind, int Count, string Menu, string Section, string Window);
}
