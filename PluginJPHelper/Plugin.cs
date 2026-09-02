using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Configuration;
using Dalamud.Game.Command;
using Dalamud.Hooking;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace PluginJPHelper;

public sealed unsafe class Plugin : IDalamudPlugin
{
    private const string Command = "/pjph";
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly IGameInteropProvider interop;
    private readonly IPluginLog log;
    private readonly Configuration config;

    private readonly ConcurrentDictionary<string, CapturedItem> backgroundCaptured = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ConcurrentDictionary<string, CapturedItem>> pluginCaptured = new(StringComparer.Ordinal);
    private volatile bool baselineCaptureEnabled;
    private readonly Dictionary<string, string> editBuffers = new(StringComparer.Ordinal);
    // v0.0.66: 辞書画面の全カタログ再構築を毎フレーム行わない。
    private readonly Dictionary<string, KeyValuePair<string, string>[]> dictionaryCatalogCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, int>> dictionaryMenuCountCache = new(StringComparer.Ordinal);
    private bool windowOpen = false;
    private volatile bool captureEnabled;
    private long translatedCount;
    private string filter = string.Empty;
    private bool showUntranslatedOnly;
    private int dictionarySortMode; // 0=原文, 1=画面内項目, 2=日本語訳, 3=未訳優先
    private bool dictionarySortAscending = true;
    private string customPluginName = string.Empty;
    private string customWindowKeyword = string.Empty;
    private string installedPluginSelection = string.Empty;
    private string csvStatus = string.Empty;
    private string hookStatus = "未初期化";
    private string selectedPlugin = "RSR";
    private string capturePlugin = "RSR";
    [ThreadStatic] private static bool drawingOwnUi;

    private Hook<TextUnformattedDelegate>? textHook;
    private Hook<TextWrappedDelegate>? textWrappedHook;
    private Hook<CheckboxDelegate>? checkboxHook;
    private Hook<ButtonDelegate>? buttonHook;
    private Hook<SelectableDelegate>? selectableHook;
    private Hook<SelectablePtrDelegate>? selectablePtrHook;
    private Hook<ComboStrArrDelegate>? comboStrArrHook;
    private Hook<ComboStrDelegate>? comboStrHook;
    private Hook<ComboFnStrPtrDelegate>? comboFnStrPtrHook;
    private Hook<BeginComboDelegate>? beginComboHook;
    private Hook<EndComboDelegate>? endComboHook;
    private Hook<SeparatorTextDelegate>? separatorTextHook;
    private Hook<BeginDelegate>? beginHook;
    private Hook<EndDelegate>? endHook;
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
    private Hook<BeginTabItemDelegate>? beginTabItemHook;
    private Hook<MenuItemBoolDelegate>? menuItemBoolHook;
    private Hook<MenuItemBoolPtrDelegate>? menuItemBoolPtrHook;
    private Hook<BeginMenuDelegate>? beginMenuHook;
    [ThreadStatic] private static Stack<string>? windowStack;
    [ThreadStatic] private static string rsrCurrentMenu = string.Empty;
    [ThreadStatic] private static string rsrCurrentSection = string.Empty;
    [ThreadStatic] private static string rsrPendingMenuCandidate = string.Empty;
    [ThreadStatic] private static int comboOpenDepth;
    private long comboHookCalls;
    private long comboTranslatedItems;
    private long comboStrCalls;
    private long comboFnCalls;
    private long beginComboCalls;
    private long comboSelectableCalls;
    private readonly ConcurrentDictionary<string, int> seenWindows = new(StringComparer.Ordinal);
    private static Stack<string>? windowOwnerStack;
    private string lastExplicitWindowOwner = string.Empty;
    private long lastExplicitWindowOwnerTick;

    // プラグインごとに完全分離した標準辞書。
    // v0.0.4 は RSR を優先。BMR/BM は辞書枠のみ用意し、後続版で追加する。
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> StandardDictionaries =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["RSR"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Game version: 7.3"] = "ゲームバージョン：7.3",
                ["Kill it with kindness. And if that fails, kill it with sharp sticks or knives...or guns!"] = "優しさで仕留めろ。それでダメなら、尖った棒かナイフ……あるいは銃で仕留めろ！",
                ["Tip: Special thanks to supporter: catfourteen!"] = "ヒント：サポーターの catfourteen さんに特別な感謝！",
                ["Target Fate priority"] = "FATE対象を優先",
                ["Clicking actions random delay range."] = "アクション実行時のランダム遅延範囲",
                ["How soon before countdown is finished to start casting or attacking."] = "カウントダウン終了の何秒前から詠唱・攻撃を開始するか",
                ["The minimum time between updating RSR information. (Raising this will help with framerate issues but can cause issues with rotation performance)"] = "RSR情報を更新する最小間隔（長くすると負荷は下がりますが、ローテーション性能に影響する場合があります）",
                                ["Description"] = "説明",
                ["Status"] = "状態",
                ["Holds Tech Step if no targets in range (Warning, will drift)"] = "範囲内に対象がいない場合はテクニカルステップを温存（使用タイミングがずれる場合があります）",
                ["Holds Standard Step if no targets in range (Warning, will drift & Buff may fall off)"] = "範囲内に対象がいない場合はスタンダードステップを温存（使用タイミングがずれ、バフが切れる場合があります）",
                ["Dance Partner Name (If empty or not found uses default dance partner priority)"] = "ダンスパートナー名（空欄または見つからない場合は既定の優先順位を使用）",
                ["Dance Partner Priority"] = "ダンスパートナー優先順位",
                ["Reset to Default"] = "初期設定に戻す",
                ["Hard Target for all actions"] = "すべてのアクションで現在のターゲットを使用",
                ["Ignore target dummies"] = "木人を無視",
                ["Treat target dummy as a boss."] = "木人をボスとして扱う",
                ["Engage settings"] = "交戦設定",
                ["Target Hunt/Relic/Leve priority. (Relic behavior bugged)"] = "モブハント／レリック／リーヴ対象を優先（レリック動作に不具合あり）",
                ["Target quest priority (Overrides engage setting)."] = "クエスト対象を優先（交戦設定より優先）",
                ["Block targeting quest mobs belonging to other players."] = "他プレイヤーのクエスト対象をターゲットしない",
                ["Ignore all other FATE target when Forlorn available."] = "フォーローン出現中は他のFATE対象を無視",
                ["Cycles between states following settings in Target > Configuration."] = "Target > Configuration の設定に従って状態を切り替える",
                ["Prioritize mob/object targets with attack markers"] = "攻撃マーカー付きの敵／オブジェクトを優先",
                ["Prioritize enemy parts"] = "敵の部位を優先",
                ["Never attack targets with stop markers."] = "停止マーカー付きの対象は攻撃しない",
                ["Treat 1hp targets as invincible."] = "HP1の対象を無敵として扱う",
                ["Ignore Non-Fate targets while in a Fate and Fate targets if not in Fate."] = "FATE中はFATE外の敵を無視し、FATE外ではFATE対象を無視",
                ["Delay autotarget."] = "自動ターゲットを遅延",
                ["Prioritize Low HP targets instead of High HP targets when using Small Target and multiple Small targets present."] = "Small Targetで候補が複数いる場合、HPが低い対象を優先",
                ["Prioritize Low HP targets instead of High HP targets when using Big Target and multiple Big targets present."] = "Big Targetで候補が複数いる場合、HPが低い対象を優先",
                ["Set target to closest targetable enemy if no valid action target nearby and target not set while in combat (This works in Manual mode as well)"] = "戦闘中に有効な行動対象が近くになく未ターゲットなら、最も近い攻撃可能な敵を選択（Manualでも有効）",
                ["Only attack targets in view."] = "視界内の対象だけ攻撃",
                ["Only attack targets in vision cone"] = "視野角内の対象だけ攻撃",
                ["Move to the furthest position for targeting area movement actions."] = "範囲移動アクションでは最も遠い位置へ移動",
                ["The size of the sector angle that can be selected as the moveable target"] = "移動対象として選択できる扇形の角度",
                ["Use movement actions towards the object/mob in the center of the screen"] = "画面中央のオブジェクト／敵へ向かう移動アクションを使用",
                ["If enabled, movement actions target the object or mob at the center of your screen. If disabled, they target the object or mob your character is facing."] = "有効時は、移動アクションの対象を画面中央のオブジェクト／敵にします。無効時は、キャラクターが向いているオブジェクト／敵を対象にします。",
                ["If target's TTK is higher than this, regard it as boss."] = "対象のTTKがこの値より長ければボスとして扱う",
                ["If target's TTK is lower than this, regard it as dying."] = "対象のTTKがこの値より短ければ瀕死として扱う",
                ["If target's HP percentage is lower than this, regard it as dying."] = "対象のHP割合がこの値より低ければ瀕死として扱う",
                ["Enemy targeting logic. Adding more options cycles them when using /rotation Auto.\nUse /rotation Settings TargetingTypes add <option> to add,\n/rotation Settings TargetingTypes remove <option> to remove,\nand /rotation Settings TargetingTypes removeall to remove all options."] = "敵のターゲット選択方式。複数登録すると /rotation Auto 使用時に順番に切り替わる。\n追加: /rotation Settings TargetingTypes add <option>\n削除: /rotation Settings TargetingTypes remove <option>\n全削除: /rotation Settings TargetingTypes removeall",
                ["Configure basic settings."] = "基本設定",
                ["Configure general action usage and control settings."] = "アクション使用と操作の一般設定",
                ["Configure optional helpful features."] = "補助機能の設定",
                ["Configure reactive actions and status effect lists."] = "反応アクションとステータス効果リストの設定",
                ["Configure targeting settings."] = "ターゲット設定",
                ["Configure user interface settings."] = "UI設定",
                ["Configure abilities and custom conditions for your current job."] = "現在のジョブのアビリティとカスタム条件を設定",
                ["Rotation specific configs."] = "ローテーション固有設定",
                ["Which actions RSR can use"] = "RSRが使用できるアクション",
                ["What kind of AoE moves to use."] = "使用する範囲攻撃の種類",
                ["Action Ahead (Percent of your GCD time remaining on a GCD cycle before RSR will try to queue the next GCD)"] = "先行入力（次のGCDを予約し始める残りGCD時間の割合）",
                ["Intercepted action execution window (amount of time RSR is allowed to attempt to use an action after it has been intercepted)"] = "割り込みアクション実行猶予（割り込み後にRSRが実行を試みる時間）",
                ["Melee Ranged action usage offset"] = "近接ジョブの遠隔攻撃使用距離補正",
                ["Allow rotations that use this config to use abilities defined in the rotation as burst"] = "この設定を使うローテーションで、Burst指定アビリティの使用を許可",
                ["Only used automatically if coded into the rotation"] = "ローテーション側で指定されている場合のみ自動使用",
                ["Automatically use Anti-Knockback role actions (Arms Length, Surecast)"] = "ノックバック無効化ロールアクション（アームズレングス／堅実魔）を自動使用",
                ["Use interrupt abilities if possible."] = "可能なら詠唱中断アクションを使用",
                ["Use damaging gap closer abilites if the distance to your target is less than this."] = "対象との距離がこの値未満なら、ダメージ付き接近アビリティを使用",
                ["Provoke anything not on the no provoke list."] = "挑発禁止リスト以外の対象を挑発",
                ["Don't attack new mobs by AoE."] = "範囲攻撃で未交戦の敵を巻き込まない",
                ["Ignore status application against mobs that are status capped."] = "ステータス付与上限の敵にはステータス付与を行わない",
                ["Prevent the use of defense abilties during burst"] = "Burst中は防御アビリティを使用しない",
                ["Change how RSR automatically uses actions"] = "RSRの自動アクション使用方法を変更",
                ["Make /rotation Auto a toggle command. (Normal behavior cycles between targeting settings)"] = "/rotation Auto をON/OFF切替コマンドにする（通常はターゲット設定を順番に切替）",
                ["Make /rotation Manual a toggle command."] = "/rotation Manual をON/OFF切替コマンドにする",
                ["Start the addon in Auto mode. When out of combat or when combat starts, switches the target according to the set condition. \n Optionally: You can add the target type to the end of the command you want RSR to do. For example: /rotation Auto Big"] = "Autoモードで開始。非戦闘時または戦闘開始時に設定条件でターゲットを切り替える。\n必要ならコマンド末尾にターゲット種類を指定可能。例: /rotation Auto Big",
                ["Start the addon in Manual mode. You need to choose the target manually. This will bypass any engage settings that you have set up and will start attacking immediately once something is targeted."] = "Manualモードで開始。ターゲットは手動選択。交戦設定を無視し、対象を選ぶとすぐ攻撃を開始する。",
                ["Start in Target-Only mode. RSR will auto-select targets per normal logic but will not perform any actions."] = "Target-Onlyモードで開始。通常ロジックで自動ターゲットするが、アクションは実行しない。",
                ["Stop the addon. Always remember to turn it off when it is not in use!"] = "RSRを停止。使用しない時は必ずOFFにする。",
                ["Do the next action."] = "次のアクションを実行",
                ["Open a window to burst."] = "Burst用の一時ウィンドウを開く",
                ["Open a window to do not use the casting action."] = "詠唱アクションを使わない一時ウィンドウを開く",
                ["Open a window to move back."] = "後退用の一時ウィンドウを開く",
                ["Open a window to move forward."] = "前進用の一時ウィンドウを開く",
                ["Open a window to speed up."] = "加速用の一時ウィンドウを開く",
                ["Open a window to use AoE defense."] = "範囲防御用の一時ウィンドウを開く",
                ["Open a window to use AoE heal."] = "範囲回復用の一時ウィンドウを開く",
                ["Open a window to use Esuna, tank stance actions or True North."] = "エスナ／タンクスタンス／トゥルーノース用の一時ウィンドウを開く",
                ["Open a window to use knockback immunity actions."] = "ノックバック無効化用の一時ウィンドウを開く",
                ["Open a window to use Raise or Shirk."] = "蘇生／シャーク用の一時ウィンドウを開く",
                ["Open a window to use single defense."] = "単体防御用の一時ウィンドウを開く",
                ["Open a window to use single heal."] = "単体回復用の一時ウィンドウを開く",
                ["To end this special duration before the set time."] = "設定時間より前に特殊状態を終了",
                ["The duration of special windows opened by /rotation commands by default."] = "/rotation コマンドで開く特殊ウィンドウの既定継続時間",
                ["How RSR should use healing abilities"] = "RSRの回復アビリティ使用方法",
                ["Heal and raise Party NPCs."] = "PT NPCを回復・蘇生",
                ["Heal solo instance NPCs (Only enable as needed)"] = "ソロコンテンツのNPCを回復（必要時のみ有効化）",
                ["Heal party members when not in combat."] = "非戦闘時もPTメンバーを回復",
                ["Heal self first if your HP is lower than this."] = "自分のHPがこの値未満なら自分を優先して回復",
                ["Heal healer first if their HP is lower than this."] = "ヒーラーのHPがこの値未満ならヒーラーを優先して回復",
                ["Heal tank first if their HP is lower than this."] = "タンクのHPがこの値未満ならタンクを優先して回復",
                ["Only heal self when not a Healer"] = "ヒーラー以外では自分だけ回復",
                ["Only use healing abilities as a non-healer if there are no living healers in the party."] = "非ヒーラー時、PTに生存ヒーラーがいない場合のみ回復アビリティを使用",
                ["MP threshold under which to use Lucid Dreaming"] = "ルーシッドドリームを使用するMPしきい値",
                ["Hard cast Raise logic"] = "迅速魔なし蘇生の設定",
                ["How early before next GCD should RSR use swiftcast for raise"] = "次のGCDよりどれだけ早く蘇生用迅速魔を使うか",
                ["Never raise player if MP is less than this"] = "MPがこの値未満なら蘇生しない",
                ["Raise styles"] = "蘇生方式",
                ["Raise player by using Swiftcast/Dualcast if available"] = "使用可能なら迅速魔／連続魔で蘇生",
                ["Prioritize raising dead players over Healing/Defense."] = "回復／防御より蘇生を優先",
                ["Raise players that have the Brink of Death debuff"] = "衰弱［強］のプレイヤーも蘇生対象にする",
                ["Raise Red Mage and Summoners first if no Tanks or Healers are dead"] = "タンク／ヒーラーに死亡者がいなければ赤魔道士・召喚士を優先して蘇生",
                ["Random delay range for resurrecting players."] = "蘇生時のランダム遅延範囲",
                ["Random delay range for dispelling statuses."] = "状態異常解除時のランダム遅延範囲",
                ["Cleanse all dispellable debuffs regardless of healing."] = "回復設定に関係なく解除可能なデバフをすべて解除",
                ["Duty specific settings."] = "コンテンツ固有設定",
                ["Duty Start: "] = "コンテンツ開始: ",
                ["Duty End: "] = "コンテンツ終了: ",
                ["Below are plugins used by Autoduty and their current states"] = "AutoDutyが使用するプラグインと現在の状態",
                ["While the RSR Team has made effort to make RSR compatible with Autoduty, please keep in mind that RSR is not designed with botting in mind."] = "RSRはAutoDutyとの互換性に配慮されていますが、自動周回用途を前提に設計されたものではありません。",
                ["Show tooltips"] = "ツールチップを表示",
                ["Show Original Cooldown"] = "元のクールダウン表示を使用",
                ["Always Show Cooldowns"] = "クールダウンを常に表示",
                ["Display do action feedback on toast"] = "アクション実行結果をトースト表示",
                ["Display plugin status in toast popup"] = "プラグイン状態をトースト表示",
                ["Enable changelog window popup on update"] = "更新時に変更履歴ウィンドウを表示",
                ["Show random usage hints at top of configuration window"] = "設定画面上部にランダムな使用ヒントを表示",
                ["Show toggled setting and new value in chat."] = "切り替えた設定名と新しい値をチャットに表示",
                ["Hide all warnings"] = "すべての警告を非表示",
                ["Lock Control Window"] = "操作ウィンドウを固定",
                ["Only show these windows if there are enemies or in duty"] = "敵がいる時またはコンテンツ中のみこれらのウィンドウを表示",
                ["Show Action Context Menu Enable/Disable toggle"] = "アクション右クリックメニューに有効／無効切替を表示",
                ["Show Intercepted Action Window"] = "割り込みアクションウィンドウを表示",
                ["Enable RSR click counter in main menu"] = "メインメニューにRSRクリック回数を表示",
                ["Attacks the target first if it has one of these statuses"] = "これらのステータスがある対象を優先攻撃",
                ["Ignores target if it has one of these statuses"] = "これらのステータスがある対象を無視",
                ["Do not take action if you have one of these debuffs"] = "自分にこれらのデバフがある間は行動しない",
                ["Dispellable debuffs list"] = "解除対象デバフ一覧",
                ["Enemies that will never be provoked"] = "挑発しない敵一覧",
                ["Enemies that will never be targeted"] = "ターゲットしない敵一覧",
                ["Stop casting or taking actions if the enemy is casting this ability"] = "敵がこのアクションを詠唱中は詠唱・行動を停止",
                ["Use AoE damage mitigation abilities if the target is casting any of these actions"] = "対象がこれらを詠唱中なら範囲軽減を使用",
                ["Use knockback prevention abilities if the target is casting any of these actions"] = "対象がこれらを詠唱中ならノックバック無効化を使用",
                ["Use tank personal damage mitigation abilities if the target is casting any of these actions"] = "対象がこれらを詠唱中ならタンク個人軽減を使用",
                ["Record AOE actions"] = "範囲攻撃を記録",
                ["Record knockback actions"] = "ノックバック攻撃を記録",
                ["O12S - Packet Filter logic."] = "O12S - パケットフィルター処理",
                ["M8S - Wolf Pack/Stone Pack logic."] = "M8S - Wolf Pack／Stone Pack処理",
                ["M9S - Only use cleave logic."] = "M9S - 範囲巻き込み攻撃のみ使用",
                ["M9S - Cell Targeting logic."] = "M9S - Cellターゲット処理",
                ["M9S - Ads Targeting logic."] = "M9S - 雑魚ターゲット処理",
                ["M10S - Firesnaking/Watersnaking targeting logic."] = "M10S - Firesnaking／Watersnakingターゲット処理",
                ["Crucible of the Unbroken"] = "不壊の坩堝",
                ["BMR Integration Enabled"] = "BMR連携を有効化",
                ["Use BMR intergration to verify safety of movement actions/actions that cause movement for automatic usage. (Experimental)"] = "自動使用時、移動アクション／移動を伴うアクションの安全性をBMR連携で確認（実験的）",
                ["Use BMR intergration to verify safety of movement actions/actions that cause movement for intercepted usage. (Experimental)"] = "割り込み使用時、移動アクション／移動を伴うアクションの安全性をBMR連携で確認（実験的）",
                ["Nearest"] = "最も近い敵",
                ["Manual"] = "手動ターゲット",
                ["Highest Max HP"] = "最大HPが最も高い敵",
                ["Previously Engaged"] = "以前に攻撃した敵",
                ["Current Target"] = "現在のターゲット",
                ["Operating Mode"] = "動作モード",
                ["Targeting Type"] = "ターゲット選択方式",
                ["Configuration"] = "設定",
                ["Hostile"] = "敵対対象",
                ["Friendly"] = "友好的対象",
                ["Auto"] = "自動",
                ["TargetOnly"] = "ターゲットのみ",
                ["Main"] = "メイン",
                ["Actions"] = "アクション",
                ["List"] = "リスト",
                ["Basic"] = "基本",
                ["UI"] = "UI",
                ["Target"] = "ターゲット",
                ["Duty"] = "コンテンツ",
                ["Extra"] = "その他",
                ["Debug"] = "デバッグ",
                ["Big"] = "大型対象",
                ["Small"] = "小型対象",
                ["HighHP"] = "現在HPが高い敵",
                ["LowHP"] = "現在HPが低い敵",
                ["HighHPPercent"] = "HP割合が高い敵",
                ["LowHPPercent"] = "HP割合が低い敵",
                ["HighMaxHP"] = "最大HPが高い敵",
                ["LowMaxHP"] = "最大HPが低い敵",
                ["Farthest"] = "最も遠い敵",
                ["PvPHealers"] = "PvP：ヒーラー優先",
                ["PvPTanks"] = "PvP：タンク優先",
                ["PvPDPS"] = "PvP：DPS優先",
                ["Action and Setting Macros"] = "アクション・設定マクロ",
                ["Compatibility"] = "互換性",
                ["Disable Wrath Combo"] = "Wrath Comboを無効化",
                ["Cycle between first Auto, Manual, and Off"] = "最初のAuto・Manual・Offを順に切替",
                ["Cycle between each Auto, Manual, and Off"] = "各Auto・Manual・Offを順に切替",
                ["Cycle between Auto and Off"] = "AutoとOffを切替",
                ["Cycle between Manual and Off"] = "ManualとOffを切替",
                ["Cycle between Manual and Auto"] = "ManualとAutoを切替",
                ["All targets that are in range for any abilities (Tanks/Autoduty)"] = "射程内の全敵を対象（タンク／AutoDuty）",
                ["Previously engaged targets (Non-Tanks)"] = "交戦済みの敵のみ（非タンク）",
                ["All targets when solo in duty (includes Occult Crescent), or previously engaged."] = "コンテンツ内ソロ時は全敵、それ以外は交戦済みのみ",
                ["All targets when solo, or previously engaged."] = "ソロ時は全敵、PT時は交戦済みのみ",
                ["Solo Deep Dungeons: if solo, out of combat pull the nearest single enemy; in combat only previously engaged."] = "DDソロ：非戦闘時は最寄り1体、戦闘中は交戦済みのみ",
                ["GCD-Attack"] = "GCD攻撃",
                ["GCD-Friendly"] = "GCD支援",
                ["oGCD-Attack"] = "oGCD攻撃",
                ["oGCD-Friendly"] = "oGCD支援",
                ["Item"] = "アイテム",
                ["Links"] = "リンク",
                ["Many thanks to Ko-fi sponsors."] = "Ko-fiスポンサーの皆様に感謝",
                ["Open First Start Tutorial"] = "初回チュートリアルを開く",
                ["Role Action"] = "ロールアクション",
                ["State Macros"] = "状態マクロ",
                ["System Action"] = "システムアクション",
                ["Analyzes PvE combat information in every frame and finds the best action."] = "毎フレームPvE戦闘情報を解析し、最適なアクションを選択します。",
                ["Click to view plugin compatibility information. Right-click to dismiss warning."] = "プラグインの互換性情報を表示します。右クリックで警告を閉じます。",
                ["Used to customize when RSR uses specific actions automatically. Click on an action's icon in the left list. Below, you may set the conditions for when that specific action is used. Each action can have different conditions to override the default rotation behavior."] = "RSRが特定のアクションを自動使用する条件を設定します。左の一覧からアクションのアイコンを選び、そのアクション専用の使用条件を設定できます。設定した条件は通常のローテーション動作より優先されます。",
                ["This includes almost all information available in one combat frame, including the status of all party members, hostile target statuses, skill cooldowns, MP and HP of characters, character locations, hostile target casting status, combo state, combat duration, player level, etc.\n\nIt will then highlight the best action on the hotbar, or help you click it."] = "1フレーム内で取得できる戦闘情報（PTメンバーの状態、敵のステータス、スキルのリキャスト、HP／MP、位置、敵の詠唱、コンボ状態、戦闘時間、レベルなど）を解析します。\n\nその結果から最適なアクションをホットバー上で強調表示し、必要に応じて実行を補助します。",
                ["This is designed for GENERAL COMBAT, not for Savage or Ultimate content. \n\nUse it carefully! While not designed specifically for Savage or Ultimate content RSR works fine in them, but it will not solve mechanics for you. Pay attention and use macros."] = "これは一般戦闘向けに設計されており、零式・絶専用ではありません。\n\n零式や絶でも動作しますが、ギミックを自動攻略するものではありません。状況を確認し、必要に応じてマクロを使用してください。",
                [" Defense Area Ability"] = " 範囲防御アビリティ",
                [" Heal Area Ability"] = " 範囲回復アビリティ",
                [" Move Forward Ability"] = " 前進アビリティ",
                [" Speed Ability"] = " 移動速度アビリティ",
                ["/rotation Cycle behaviour"] = "/rotation Cycle の動作",
                ["Action Usage and Control"] = "アクション使用・制御",
                ["Add Action"] = "アクションを追加",
                ["Add beneficial AoE location"] = "有利な範囲効果位置を追加",
                ["Add Events"] = "イベントを追加",
                ["Add Plugin"] = "プラグインを追加",
                ["Add Repo"] = "リポジトリを追加",
                ["Alliance Raid"] = "アライアンスレイド",
                ["Allow Sprint when no target is set even if youre in range of hostiles (Experimental)."] = "敵の近くでもターゲット未選択時はスプリントを許可（実験的）",
                ["AntiAfkKick is not enabled"] = "AntiAfkKick は無効です",
                ["Auto Switch"] = "自動切替",
                ["Auto turn off when dead in PvE."] = "PvEで死亡したら自動OFF",
                ["Auto turn off when dead in PvP."] = "PvPで死亡したら自動OFF",
                ["Auto turn off when duty is completed."] = "コンテンツ完了時に自動OFF",
                ["Auto turn off when PvP match ends."] = "PvP終了時に自動OFF",
                ["Auto turn off when switching jobs"] = "ジョブ変更時に自動OFF",
                ["Auto turn on when PvP match starts."] = "PvP開始時に自動ON",
                ["AutoDuty is installed and enabled"] = "AutoDuty は導入済み・有効",
                ["AutoRetainer is installed and enabled"] = "AutoRetainer は導入済み・有効",
                ["Avarice is not enabled"] = "Avarice は無効です",
                ["Backup Configs"] = "設定をバックアップ",
                ["Boss Mod is installed and enabled. Both Boss Mods cannot be installed and enabled at the same time. Please disable Boss Mod."] = "Boss Mod が導入済み・有効です。Boss Mod と BossMod Reborn は同時に有効化できません。Boss Mod を無効にしてください。",
                ["BossModReborn is installed and enabled"] = "BossMod Reborn は導入済み・有効",
                ["Cancel auto mode if combat starts early during countdown"] = "カウントダウン中に早く戦闘が始まったらAutoモードを解除",
                ["Cancel cast in PvP if the target gains Guard and the action does not ignore Guard."] = "PvPで対象が防御を使用し、その防御を無視できないアクションなら詠唱を中止",
                ["Chaotic Alliance Raid"] = "滅アライアンスレイド",
                ["Clear Trace"] = "トレースをクリア",
                ["Configs/Backups location: "] = "設定／バックアップ保存先: ",
                ["Configure AutoDuty settings and view related information."] = "AutoDuty設定と関連情報を表示",
                ["Copy to Clipboard"] = "クリップボードへコピー",
                ["Dancing Mad (Ultimate) - Hero/Villain logic."] = "Dancing Mad（絶）- Hero/Villain 判定ロジック",
                ["Debug Mode"] = "デバッグモード",
                ["Debug options for developers and rotation writers (disable when not in use)."] = "開発者・ローテーション作成者向けデバッグ設定（通常は無効推奨）",
                ["Deep Dungeon"] = "ディープダンジョン",
                ["Disable automatically during area transitions."] = "エリア移動中は自動で無効化",
                ["Disable automatically during cutscenes."] = "カットシーン中は自動で無効化",
                ["Don't use any actions while in Guard."] = "防御中はアクションを使用しない",
                ["DTR Behaviour"] = "DTRバーの動作",
                ["Dungeon"] = "ダンジョン",
                ["Elemental Weakness Tracking."] = "属性弱点を追跡",
                ["Enable cactbot timeline integration (Extremely experimental)"] = "cactbotタイムライン連携を有効化（非常に実験的）",
                ["Event"] = "イベント",
                ["Extreme"] = "極",
                ["Field Ops"] = "フィールド探索型コンテンツ",
                ["From Clipboard"] = "クリップボードから読込",
                ["Gearsetter is not enabled"] = "Gearsetter は無効です",
                ["Healing Usage and Control"] = "回復使用・制御",
                ["HP standard deviation for using AoE heal."] = "範囲回復を使用するHPの標準偏差",
                ["Ignore Invincibility for PvP purposes."] = "PvPでは無敵判定を無視",
                ["Ignore TTK for PvP purposes."] = "PvPではTTK判定を無視",
                ["In this window, you can set parameters that can be customized using lists."] = "この画面では、リストでカスタマイズできる各種項目を設定します。",
                ["In this window, you can set which macro will be triggered after using an action."] = "この画面では、アクション使用後に実行するマクロを設定します。",
                ["Information"] = "情報",
                ["Internal"] = "内部",
                ["Is Shared"] = "共有する",
                ["Lock actions when casting Collective Unconscious during AOE mitigations."] = "範囲軽減中に運命の輪を使用している間は他アクションを抑制",
                ["Lock actions when casting Passage Of Arms during AOE mitigations."] = "範囲軽減中にパッセージ・オブ・アームズを使用している間は他アクションを抑制",
                ["Lock actions when casting Phantom Flurry."] = "幻影の闘舞中は他アクションを抑制",
                ["Lock out GCD cycle if you are below 50% HP and have over 2000 MP for heals (Experimental)."] = "HP50%未満かつMP2000以上なら回復のためGCDサイクルを抑制（実験的）",
                ["Map-specific settings"] = "マップ固有設定",
                ["Not In Combat random delay range."] = "非戦闘時のランダム遅延範囲",
                ["Open Action Trace File"] = "アクショントレースを開く",
                ["Others"] = "その他",
                ["Prioritize Low HP tank for tankbusters."] = "タンク強攻撃ではHPが低いタンクを優先",
                ["PvP"] = "PvP",
                ["Raise non-Healers from bottom of party list to the top (Light Party 2 Healer Behavior)"] = "非ヒーラーの蘇生優先度をPTリスト下から上へ（ライトPTでヒーラー2人時の動作）",
                ["Random range of simulated presses per action"] = "アクションごとの疑似入力回数のランダム範囲",
                ["Reset and Stop Casting List"] = "詠唱停止リストを初期化して停止",
                ["Reset and Update AOE List"] = "範囲攻撃リストを初期化して更新",
                ["Reset and Update Dispell Debuff List"] = "解除対象デバフリストを初期化して更新",
                ["Reset and Update Invuln Status List"] = "無敵ステータスリストを初期化して更新",
                ["Reset and Update Knockback List"] = "ノックバックリストを初期化して更新",
                ["Reset and Update No Casting Status List"] = "詠唱禁止ステータスリストを初期化して更新",
                ["Reset and Update Priority Status List"] = "優先ステータスリストを初期化して更新",
                ["Reset and Update Tankbuster List"] = "タンク強攻撃リストを初期化して更新",
                ["Restore Configs"] = "設定を復元",
                ["Right-click to copy this tip."] = "右クリックでこのヒントをコピー",
                ["RSR focuses on the rotation itself. These are side features. Subject to removal at any time."] = "RSRはローテーション本体を重視しています。ここは補助機能で、将来削除される可能性があります。",
                ["Savage"] = "零式",
                ["Set Blue Mage Actions Automatically"] = "青魔法を自動設定",
                ["Set RSR to PvP specific state when enabled in PvP zone."] = "PvPエリアで有効化された場合、RSRをPvP用状態に切り替える",
                ["Show cactbot event toasts (debug)"] = "cactbotイベントをトースト表示（デバッグ）",
                ["SkipCutscene is not enabled"] = "SkipCutscene は無効です",
                ["Statuses"] = "ステータス",
                ["Stop single target GCD healing after reaching threshold. (EXTREMELY Experimental)"] = "しきい値到達後は単体GCD回復を停止（非常に実験的）",
                ["The HP for using Guard."] = "防御を使用するHP",
                ["The HP%% for tank to use invulnerability"] = "タンクが無敵技を使用するHP％",
                ["The range of random delay before interrupting hostile targets."] = "敵の詠唱中断までのランダム遅延範囲",
                ["The Masked Carnivale"] = "マスクカーニバル",
                ["This config is job-specific"] = "この設定はジョブ専用です",
                ["Timer"] = "タイマー",
                ["Treasure Dungeon"] = "宝物庫",
                ["Treasure Dungeons - Numbered mob logic."] = "宝物庫 - 番号付き敵のターゲットロジック",
                ["Treasure Dungeons - Timed mob logic."] = "宝物庫 - 時間制敵のターゲットロジック",
                ["Treat focus targeted player as party member in alliance raids"] = "アライアンスレイドではフォーカスターゲットしたプレイヤーをPTメンバーとして扱う",
                ["Treat your chocobo as a party member"] = "チョコボをPTメンバーとして扱う",
                ["Ultimate"] = "絶",
                ["Use Purify to remove Bind debuff"] = "浄化でバインドを解除",
                ["Use Purify to remove Deep Freeze debuff"] = "浄化で氷結を解除",
                ["Use Purify to remove Heavy debuff"] = "浄化でヘヴィを解除",
                ["Use Purify to remove Miracle of Nature debuff"] = "浄化でミラクル・オブ・ネイチャーを解除",
                ["Use Purify to remove Silence debuff"] = "浄化で沈黙を解除",
                ["Use Purify to remove Stun debuff"] = "浄化でスタンを解除",
                ["Useful information and macro list."] = "便利な情報とマクロ一覧",
                ["Variant Dungeon"] = "ヴァリアントダンジョン",
                ["vnavmesh is installed and enabled"] = "vnavmesh は導入済み・有効",
                ["Windows"] = "ウィンドウ",
            },
            ["BMR"] = new Dictionary<string, string>(StringComparer.Ordinal),
            ["BM"] = new Dictionary<string, string>(StringComparer.Ordinal),
        };

    // v0.0.28: v0.0.23の取得結果を基準にしたRSR所属表。
    // 同一原文が複数画面で検出されたものは未分類のままにし、意味からの推測では埋めない。
    private static readonly IReadOnlyDictionary<string, DictionaryLocation> RsrBundledLocations =
        new Dictionary<string, DictionaryLocation>(StringComparer.Ordinal)
        {
            ["Target Fate priority"] = new DictionaryLocation { Menu = "Target", Section = "Configuration" },
            ["Clicking actions random delay range."] = new DictionaryLocation { Menu = "Basic", Section = "Timer" },
            ["How soon before countdown is finished to start casting or attacking."] = new DictionaryLocation { Menu = "Basic", Section = "Timer" },
            ["The minimum time between updating RSR information. (Raising this will help with framerate issues but can cause issues with rotation performance)"] = new DictionaryLocation { Menu = "Basic", Section = "Timer" },
            ["Description"] = new DictionaryLocation { Menu = "DNC", Section = "Configuration" },
            ["Status"] = new DictionaryLocation { Menu = "DNC", Section = "Configuration" },
            ["Holds Tech Step if no targets in range (Warning, will drift)"] = new DictionaryLocation { Menu = "DNC", Section = "Configuration" },
            ["Holds Standard Step if no targets in range (Warning, will drift & Buff may fall off)"] = new DictionaryLocation { Menu = "DNC", Section = "Configuration" },
            ["Dance Partner Name (If empty or not found uses default dance partner priority)"] = new DictionaryLocation { Menu = "DNC", Section = "Configuration" },
            ["Dance Partner Priority"] = new DictionaryLocation { Menu = "DNC", Section = "Configuration" },
            ["Reset to Default"] = new DictionaryLocation { Menu = "DNC", Section = "Configuration" },
            ["Hard Target for all actions"] = new DictionaryLocation { Menu = "Target", Section = "Configuration" },
            ["Ignore target dummies"] = new DictionaryLocation { Menu = "Target", Section = "Configuration" },
            ["Treat target dummy as a boss."] = new DictionaryLocation { Menu = "Target", Section = "Configuration" },
            ["Engage settings"] = new DictionaryLocation { Menu = "Target", Section = "Configuration" },
            ["Target Hunt/Relic/Leve priority. (Relic behavior bugged)"] = new DictionaryLocation { Menu = "Target", Section = "Configuration" },
            ["Target quest priority (Overrides engage setting)."] = new DictionaryLocation { Menu = "Target", Section = "Configuration" },
            ["Block targeting quest mobs belonging to other players."] = new DictionaryLocation { Menu = "Target", Section = "Configuration" },
            ["Ignore all other FATE target when Forlorn available."] = new DictionaryLocation { Menu = "Target", Section = "Configuration" },
            ["Cycles between states following settings in Target > Configuration."] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["Prioritize mob/object targets with attack markers"] = new DictionaryLocation { Menu = "Target", Section = "Configuration" },
            ["Prioritize enemy parts"] = new DictionaryLocation { Menu = "Target", Section = "Configuration" },
            ["Never attack targets with stop markers."] = new DictionaryLocation { Menu = "Target", Section = "Configuration" },
            ["Treat 1hp targets as invincible."] = new DictionaryLocation { Menu = "Target", Section = "Configuration" },
            ["Ignore Non-Fate targets while in a Fate and Fate targets if not in Fate."] = new DictionaryLocation { Menu = "Target", Section = "Configuration" },
            ["Delay autotarget."] = new DictionaryLocation { Menu = "Target", Section = "Configuration" },
            ["Prioritize Low HP targets instead of High HP targets when using Small Target and multiple Small targets present."] = new DictionaryLocation { Menu = "Target", Section = "Configuration" },
            ["Prioritize Low HP targets instead of High HP targets when using Big Target and multiple Big targets present."] = new DictionaryLocation { Menu = "Target", Section = "Configuration" },
            ["Set target to closest targetable enemy if no valid action target nearby and target not set while in combat (This works in Manual mode as well)"] = new DictionaryLocation { Menu = "Target", Section = "Configuration" },
            ["Only attack targets in view."] = new DictionaryLocation { Menu = "Target", Section = "Configuration" },
            ["Only attack targets in vision cone"] = new DictionaryLocation { Menu = "Target", Section = "Configuration" },
            ["Move to the furthest position for targeting area movement actions."] = new DictionaryLocation { Menu = "Target", Section = "Configuration" },
            ["The size of the sector angle that can be selected as the moveable target"] = new DictionaryLocation { Menu = "Target", Section = "Configuration" },
            ["Use movement actions towards the object/mob in the center of the screen"] = new DictionaryLocation { Menu = "Target", Section = "Configuration" },
            ["If enabled, movement actions target the object or mob at the center of your screen. If disabled, they target the object or mob your character is facing."] = new DictionaryLocation { Menu = "Target", Section = "Configuration" },
            ["If target's TTK is higher than this, regard it as boss."] = new DictionaryLocation { Menu = "Target", Section = "Configuration" },
            ["If target's TTK is lower than this, regard it as dying."] = new DictionaryLocation { Menu = "Target", Section = "Configuration" },
            ["If target's HP percentage is lower than this, regard it as dying."] = new DictionaryLocation { Menu = "Target", Section = "Configuration" },
            ["Enemy targeting logic. Adding more options cycles them when using /rotation Auto.\nUse /rotation Settings TargetingTypes add <option> to add,\n/rotation Settings TargetingTypes remove <option> to remove,\nand /rotation Settings TargetingTypes removeall to remove all options."] = new DictionaryLocation { Menu = "Target", Section = "Hostile" },
            ["Configure basic settings."] = new DictionaryLocation { Menu = "Basic", Section = "" },
            ["Configure general action usage and control settings."] = new DictionaryLocation { Menu = "Auto", Section = "Action Usage and Control" },
            ["Configure optional helpful features."] = new DictionaryLocation { Menu = "Extra", Section = "" },
            ["Configure reactive actions and status effect lists."] = new DictionaryLocation { Menu = "List", Section = "" },
            ["Configure targeting settings."] = new DictionaryLocation { Menu = "Target", Section = "" },
            ["Configure user interface settings."] = new DictionaryLocation { Menu = "UI", Section = "" },
            ["Configure abilities and custom conditions for your current job."] = new DictionaryLocation { Menu = "Actions", Section = "" },
            ["Rotation specific configs."] = new DictionaryLocation { Menu = "DNC", Section = "" },
            ["Which actions RSR can use"] = new DictionaryLocation { Menu = "Auto", Section = "Action Usage and Control" },
            ["What kind of AoE moves to use."] = new DictionaryLocation { Menu = "Auto", Section = "Action Usage and Control" },
            ["Action Ahead (Percent of your GCD time remaining on a GCD cycle before RSR will try to queue the next GCD)"] = new DictionaryLocation { Menu = "Basic", Section = "Timer" },
            ["Intercepted action execution window (amount of time RSR is allowed to attempt to use an action after it has been intercepted)"] = new DictionaryLocation { Menu = "Auto", Section = "Action Usage and Control" },
            ["Melee Ranged action usage offset"] = new DictionaryLocation { Menu = "Auto", Section = "Action Usage and Control" },
            ["Allow rotations that use this config to use abilities defined in the rotation as burst"] = new DictionaryLocation { Menu = "Auto", Section = "Action Usage and Control" },
            ["Only used automatically if coded into the rotation"] = new DictionaryLocation { Menu = "Auto", Section = "Action Usage and Control" },
            ["Automatically use Anti-Knockback role actions (Arms Length, Surecast)"] = new DictionaryLocation { Menu = "Auto", Section = "Action Usage and Control" },
            ["Use interrupt abilities if possible."] = new DictionaryLocation { Menu = "Auto", Section = "Action Usage and Control" },
            ["Use damaging gap closer abilites if the distance to your target is less than this."] = new DictionaryLocation { Menu = "Auto", Section = "Action Usage and Control" },
            ["Provoke anything not on the no provoke list."] = new DictionaryLocation { Menu = "Auto", Section = "Action Usage and Control" },
            ["Don't attack new mobs by AoE."] = new DictionaryLocation { Menu = "Auto", Section = "Action Usage and Control" },
            ["Ignore status application against mobs that are status capped."] = new DictionaryLocation { Menu = "Auto", Section = "Action Usage and Control" },
            ["Prevent the use of defense abilties during burst"] = new DictionaryLocation { Menu = "DNC", Section = "Configuration" },
            ["Change how RSR automatically uses actions"] = new DictionaryLocation { Menu = "Auto", Section = "Action Usage and Control" },
            ["Make /rotation Auto a toggle command. (Normal behavior cycles between targeting settings)"] = new DictionaryLocation { Menu = "Basic", Section = "Others" },
            ["Make /rotation Manual a toggle command."] = new DictionaryLocation { Menu = "Basic", Section = "Others" },
            ["Start the addon in Auto mode. When out of combat or when combat starts, switches the target according to the set condition. \n Optionally: You can add the target type to the end of the command you want RSR to do. For example: /rotation Auto Big"] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["Start the addon in Manual mode. You need to choose the target manually. This will bypass any engage settings that you have set up and will start attacking immediately once something is targeted."] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["Start in Target-Only mode. RSR will auto-select targets per normal logic but will not perform any actions."] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["Stop the addon. Always remember to turn it off when it is not in use!"] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["Do the next action."] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["Open a window to burst."] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["Open a window to do not use the casting action."] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["Open a window to move back."] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["Open a window to move forward."] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["Open a window to speed up."] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["Open a window to use AoE defense."] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["Open a window to use AoE heal."] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["Open a window to use Esuna, tank stance actions or True North."] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["Open a window to use knockback immunity actions."] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["Open a window to use Raise or Shirk."] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["Open a window to use single defense."] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["Open a window to use single heal."] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["To end this special duration before the set time."] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["The duration of special windows opened by /rotation commands by default."] = new DictionaryLocation { Menu = "Basic", Section = "Timer" },
            ["How RSR should use healing abilities"] = new DictionaryLocation { Menu = "Auto", Section = "Healing Usage and Control" },
            ["Heal and raise Party NPCs."] = new DictionaryLocation { Menu = "Auto", Section = "Healing Usage and Control" },
            ["Heal solo instance NPCs (Only enable as needed)"] = new DictionaryLocation { Menu = "Auto", Section = "Healing Usage and Control" },
            ["Heal party members when not in combat."] = new DictionaryLocation { Menu = "Auto", Section = "Healing Usage and Control" },
            ["Heal self first if your HP is lower than this."] = new DictionaryLocation { Menu = "Auto", Section = "Healing Usage and Control" },
            ["Heal healer first if their HP is lower than this."] = new DictionaryLocation { Menu = "Auto", Section = "Healing Usage and Control" },
            ["Heal tank first if their HP is lower than this."] = new DictionaryLocation { Menu = "Auto", Section = "Healing Usage and Control" },
            ["Only heal self when not a Healer"] = new DictionaryLocation { Menu = "Auto", Section = "Healing Usage and Control" },
            ["Only use healing abilities as a non-healer if there are no living healers in the party."] = new DictionaryLocation { Menu = "Auto", Section = "Healing Usage and Control" },
            ["MP threshold under which to use Lucid Dreaming"] = new DictionaryLocation { Menu = "Auto", Section = "Healing Usage and Control" },
            ["Hard cast Raise logic"] = new DictionaryLocation { Menu = "Auto", Section = "Healing Usage and Control" },
            ["How early before next GCD should RSR use swiftcast for raise"] = new DictionaryLocation { Menu = "Auto", Section = "Healing Usage and Control" },
            ["Never raise player if MP is less than this"] = new DictionaryLocation { Menu = "Auto", Section = "Healing Usage and Control" },
            ["Raise styles"] = new DictionaryLocation { Menu = "Auto", Section = "Healing Usage and Control" },
            ["Raise player by using Swiftcast/Dualcast if available"] = new DictionaryLocation { Menu = "Auto", Section = "Healing Usage and Control" },
            ["Prioritize raising dead players over Healing/Defense."] = new DictionaryLocation { Menu = "Auto", Section = "Healing Usage and Control" },
            ["Raise players that have the Brink of Death debuff"] = new DictionaryLocation { Menu = "Auto", Section = "Healing Usage and Control" },
            ["Raise Red Mage and Summoners first if no Tanks or Healers are dead"] = new DictionaryLocation { Menu = "Auto", Section = "Healing Usage and Control" },
            ["Random delay range for resurrecting players."] = new DictionaryLocation { Menu = "Auto", Section = "Healing Usage and Control" },
            ["Random delay range for dispelling statuses."] = new DictionaryLocation { Menu = "Auto", Section = "Healing Usage and Control" },
            ["Cleanse all dispellable debuffs regardless of healing."] = new DictionaryLocation { Menu = "Auto", Section = "Healing Usage and Control" },
            ["Duty specific settings."] = new DictionaryLocation { Menu = "Duty", Section = "" },
            ["Duty Start: "] = new DictionaryLocation { Menu = "Extra", Section = "Event" },
            ["Duty End: "] = new DictionaryLocation { Menu = "Extra", Section = "Event" },
            ["Below are plugins used by Autoduty and their current states"] = new DictionaryLocation { Menu = "AutoDuty", Section = "" },
            ["While the RSR Team has made effort to make RSR compatible with Autoduty, please keep in mind that RSR is not designed with botting in mind."] = new DictionaryLocation { Menu = "AutoDuty", Section = "" },
            ["Show tooltips"] = new DictionaryLocation { Menu = "UI", Section = "Information" },
            ["Show Original Cooldown"] = new DictionaryLocation { Menu = "UI", Section = "Information" },
            ["Always Show Cooldowns"] = new DictionaryLocation { Menu = "UI", Section = "Information" },
            ["Display do action feedback on toast"] = new DictionaryLocation { Menu = "UI", Section = "Information" },
            ["Display plugin status in toast popup"] = new DictionaryLocation { Menu = "UI", Section = "Information" },
            ["Enable changelog window popup on update"] = new DictionaryLocation { Menu = "UI", Section = "Information" },
            ["Show random usage hints at top of configuration window"] = new DictionaryLocation { Menu = "UI", Section = "Information" },
            ["Show toggled setting and new value in chat."] = new DictionaryLocation { Menu = "UI", Section = "Information" },
            ["Hide all warnings"] = new DictionaryLocation { Menu = "UI", Section = "Information" },
            ["Lock Control Window"] = new DictionaryLocation { Menu = "UI", Section = "Windows" },
            ["Only show these windows if there are enemies or in duty"] = new DictionaryLocation { Menu = "UI", Section = "Windows" },
            ["Show Action Context Menu Enable/Disable toggle"] = new DictionaryLocation { Menu = "UI", Section = "Windows" },
            ["Show Intercepted Action Window"] = new DictionaryLocation { Menu = "UI", Section = "Windows" },
            ["Enable RSR click counter in main menu"] = new DictionaryLocation { Menu = "Extra", Section = "Others" },
            ["Attacks the target first if it has one of these statuses"] = new DictionaryLocation { Menu = "List", Section = "Statuses" },
            ["Ignores target if it has one of these statuses"] = new DictionaryLocation { Menu = "List", Section = "Statuses" },
            ["Do not take action if you have one of these debuffs"] = new DictionaryLocation { Menu = "List", Section = "Statuses" },
            ["Dispellable debuffs list"] = new DictionaryLocation { Menu = "List", Section = "Statuses" },
            ["Enemies that will never be provoked"] = new DictionaryLocation { Menu = "List", Section = "Map-specific settings" },
            ["Enemies that will never be targeted"] = new DictionaryLocation { Menu = "List", Section = "Map-specific settings" },
            ["Stop casting or taking actions if the enemy is casting this ability"] = new DictionaryLocation { Menu = "Actions", Section = "" },
            ["Use AoE damage mitigation abilities if the target is casting any of these actions"] = new DictionaryLocation { Menu = "Actions", Section = "" },
            ["Use knockback prevention abilities if the target is casting any of these actions"] = new DictionaryLocation { Menu = "Actions", Section = "" },
            ["Use tank personal damage mitigation abilities if the target is casting any of these actions"] = new DictionaryLocation { Menu = "Actions", Section = "" },
            ["Record AOE actions"] = new DictionaryLocation { Menu = "Actions", Section = "" },
            ["Record knockback actions"] = new DictionaryLocation { Menu = "Actions", Section = "" },
            ["Use BMR intergration to verify safety of movement actions/actions that cause movement for automatic usage. (Experimental)"] = new DictionaryLocation { Menu = "Auto", Section = "Action Usage and Control" },
            ["Use BMR intergration to verify safety of movement actions/actions that cause movement for intercepted usage. (Experimental)"] = new DictionaryLocation { Menu = "Auto", Section = "Action Usage and Control" },
            ["Nearest"] = new DictionaryLocation { Menu = "Target", Section = "Hostile" },
            ["Manual"] = new DictionaryLocation { Menu = "Target", Section = "Hostile" },
            ["Highest Max HP"] = new DictionaryLocation { Menu = "Target", Section = "Hostile" },
            ["Previously Engaged"] = new DictionaryLocation { Menu = "Target", Section = "Hostile" },
            ["Current Target"] = new DictionaryLocation { Menu = "Target", Section = "Hostile" },
            ["Operating Mode"] = new DictionaryLocation { Menu = "Target", Section = "Configuration" },
            ["Targeting Type"] = new DictionaryLocation { Menu = "Target", Section = "Hostile" },
            ["Configuration"] = new DictionaryLocation { Menu = "Target", Section = "Configuration" },
            ["Hostile"] = new DictionaryLocation { Menu = "Target", Section = "Hostile" },
            ["Friendly"] = new DictionaryLocation { Menu = "Target", Section = "Hostile" },
            ["Auto"] = new DictionaryLocation { Menu = "Auto", Section = "" },
            ["TargetOnly"] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["Main"] = new DictionaryLocation { Menu = "Main", Section = "" },
            ["Actions"] = new DictionaryLocation { Menu = "Actions", Section = "" },
            ["List"] = new DictionaryLocation { Menu = "List", Section = "" },
            ["Basic"] = new DictionaryLocation { Menu = "Basic", Section = "" },
            ["UI"] = new DictionaryLocation { Menu = "UI", Section = "" },
            ["Target"] = new DictionaryLocation { Menu = "Target", Section = "" },
            ["Duty"] = new DictionaryLocation { Menu = "Duty", Section = "" },
            ["Extra"] = new DictionaryLocation { Menu = "Extra", Section = "" },
            ["Debug"] = new DictionaryLocation { Menu = "Debug", Section = "" },
            ["Big"] = new DictionaryLocation { Menu = "Target", Section = "Hostile" },
            ["Small"] = new DictionaryLocation { Menu = "Target", Section = "Hostile" },
            ["HighHP"] = new DictionaryLocation { Menu = "Target", Section = "Hostile" },
            ["LowHP"] = new DictionaryLocation { Menu = "Target", Section = "Hostile" },
            ["HighHPPercent"] = new DictionaryLocation { Menu = "Target", Section = "Hostile" },
            ["LowHPPercent"] = new DictionaryLocation { Menu = "Target", Section = "Hostile" },
            ["HighMaxHP"] = new DictionaryLocation { Menu = "Target", Section = "Hostile" },
            ["LowMaxHP"] = new DictionaryLocation { Menu = "Target", Section = "Hostile" },
            ["Farthest"] = new DictionaryLocation { Menu = "Target", Section = "Hostile" },
            ["PvPHealers"] = new DictionaryLocation { Menu = "Target", Section = "Hostile" },
            ["PvPTanks"] = new DictionaryLocation { Menu = "Target", Section = "Hostile" },
            ["PvPDPS"] = new DictionaryLocation { Menu = "Target", Section = "Hostile" },
            ["Action and Setting Macros"] = new DictionaryLocation { Menu = "Main", Section = "Action and Setting Macros" },
            ["Compatibility"] = new DictionaryLocation { Menu = "Main", Section = "Compatibility" },
            ["Disable Wrath Combo"] = new DictionaryLocation { Menu = "Main", Section = "Compatibility" },
            ["GCD-Attack"] = new DictionaryLocation { Menu = "Actions", Section = "" },
            ["GCD-Friendly"] = new DictionaryLocation { Menu = "Actions", Section = "" },
            ["oGCD-Attack"] = new DictionaryLocation { Menu = "Actions", Section = "" },
            ["oGCD-Friendly"] = new DictionaryLocation { Menu = "Actions", Section = "" },
            ["Item"] = new DictionaryLocation { Menu = "Actions", Section = "" },
            ["Links"] = new DictionaryLocation { Menu = "Main", Section = "Links" },
            ["Many thanks to Ko-fi sponsors."] = new DictionaryLocation { Menu = "Main", Section = "Many thanks to Ko-fi sponsors." },
            ["Open First Start Tutorial"] = new DictionaryLocation { Menu = "Main", Section = "Links" },
            ["Role Action"] = new DictionaryLocation { Menu = "Actions", Section = "" },
            ["State Macros"] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["System Action"] = new DictionaryLocation { Menu = "Actions", Section = "" },
            ["Analyzes PvE combat information in every frame and finds the best action."] = new DictionaryLocation { Menu = "Main", Section = "" },
            ["Click to view plugin compatibility information. Right-click to dismiss warning."] = new DictionaryLocation { Menu = "Main", Section = "Compatibility" },
            ["Used to customize when RSR uses specific actions automatically. Click on an action's icon in the left list. Below, you may set the conditions for when that specific action is used. Each action can have different conditions to override the default rotation behavior."] = new DictionaryLocation { Menu = "Actions", Section = "" },
            ["This includes almost all information available in one combat frame, including the status of all party members, hostile target statuses, skill cooldowns, MP and HP of characters, character locations, hostile target casting status, combo state, combat duration, player level, etc.\n\nIt will then highlight the best action on the hotbar, or help you click it."] = new DictionaryLocation { Menu = "Main", Section = "Links" },
            ["This is designed for GENERAL COMBAT, not for Savage or Ultimate content. \n\nUse it carefully! While not designed specifically for Savage or Ultimate content RSR works fine in them, but it will not solve mechanics for you. Pay attention and use macros."] = new DictionaryLocation { Menu = "Main", Section = "Links" },
            [" Defense Area Ability"] = new DictionaryLocation { Menu = "DNC", Section = "Configuration" },
            [" Heal Area Ability"] = new DictionaryLocation { Menu = "DNC", Section = "Configuration" },
            [" Move Forward Ability"] = new DictionaryLocation { Menu = "DNC", Section = "Configuration" },
            [" Speed Ability"] = new DictionaryLocation { Menu = "DNC", Section = "Configuration" },
            ["/rotation Cycle behaviour"] = new DictionaryLocation { Menu = "Target", Section = "Configuration" },
            ["Action Usage and Control"] = new DictionaryLocation { Menu = "Auto", Section = "Action Usage and Control" },
            ["Add Action"] = new DictionaryLocation { Menu = "Actions", Section = "" },
            ["Add beneficial AoE location"] = new DictionaryLocation { Menu = "List", Section = "Map-specific settings" },
            ["Add Events"] = new DictionaryLocation { Menu = "Extra", Section = "Event" },
            ["Add Plugin"] = new DictionaryLocation { Menu = "AutoDuty", Section = "" },
            ["Add Repo"] = new DictionaryLocation { Menu = "AutoDuty", Section = "" },
            ["Alliance Raid"] = new DictionaryLocation { Menu = "Duty", Section = "Alliance Raid" },
            ["Allow Sprint when no target is set even if youre in range of hostiles (Experimental)."] = new DictionaryLocation { Menu = "Duty", Section = "PvP" },
            ["AntiAfkKick is not enabled"] = new DictionaryLocation { Menu = "AutoDuty", Section = "" },
            ["Auto Switch"] = new DictionaryLocation { Menu = "Auto", Section = "Auto Switch" },
            ["Auto turn off when dead in PvE."] = new DictionaryLocation { Menu = "Auto", Section = "Auto Switch" },
            ["Auto turn off when dead in PvP."] = new DictionaryLocation { Menu = "Auto", Section = "Auto Switch" },
            ["Auto turn off when duty is completed."] = new DictionaryLocation { Menu = "Auto", Section = "Auto Switch" },
            ["Auto turn off when PvP match ends."] = new DictionaryLocation { Menu = "Auto", Section = "Auto Switch" },
            ["Auto turn off when switching jobs"] = new DictionaryLocation { Menu = "Auto", Section = "Auto Switch" },
            ["Auto turn on when PvP match starts."] = new DictionaryLocation { Menu = "Auto", Section = "Auto Switch" },
            ["AutoDuty is installed and enabled"] = new DictionaryLocation { Menu = "AutoDuty", Section = "" },
            ["AutoRetainer is installed and enabled"] = new DictionaryLocation { Menu = "AutoDuty", Section = "" },
            ["Avarice is not enabled"] = new DictionaryLocation { Menu = "AutoDuty", Section = "" },
            ["Backup Configs"] = new DictionaryLocation { Menu = "Extra", Section = "Internal" },
            ["Boss Mod is installed and enabled. Both Boss Mods cannot be installed and enabled at the same time. Please disable Boss Mod."] = new DictionaryLocation { Menu = "AutoDuty", Section = "" },
            ["BossModReborn is installed and enabled"] = new DictionaryLocation { Menu = "AutoDuty", Section = "" },
            ["Cancel auto mode if combat starts early during countdown"] = new DictionaryLocation { Menu = "Auto", Section = "Auto Switch" },
            ["Cancel cast in PvP if the target gains Guard and the action does not ignore Guard."] = new DictionaryLocation { Menu = "Duty", Section = "PvP" },
            ["Chaotic Alliance Raid"] = new DictionaryLocation { Menu = "Duty", Section = "Chaotic Alliance Raid" },
            ["Clear Trace"] = new DictionaryLocation { Menu = "Debug", Section = "" },
            ["Configs/Backups location: "] = new DictionaryLocation { Menu = "Extra", Section = "Internal" },
            ["Configure AutoDuty settings and view related information."] = new DictionaryLocation { Menu = "AutoDuty", Section = "" },
            ["Copy to Clipboard"] = new DictionaryLocation { Menu = "Actions", Section = "" },
            ["Dancing Mad (Ultimate) - Hero/Villain logic."] = new DictionaryLocation { Menu = "Duty", Section = "Ultimate" },
            ["Debug Mode"] = new DictionaryLocation { Menu = "Debug", Section = "" },
            ["Debug options for developers and rotation writers (disable when not in use)."] = new DictionaryLocation { Menu = "Debug", Section = "" },
            ["Deep Dungeon"] = new DictionaryLocation { Menu = "Duty", Section = "Deep Dungeon" },
            ["Disable automatically during area transitions."] = new DictionaryLocation { Menu = "Auto", Section = "Auto Switch" },
            ["Disable automatically during cutscenes."] = new DictionaryLocation { Menu = "Auto", Section = "Auto Switch" },
            ["Don't use any actions while in Guard."] = new DictionaryLocation { Menu = "Duty", Section = "PvP" },
            ["DTR Behaviour"] = new DictionaryLocation { Menu = "UI", Section = "Information" },
            ["Dungeon"] = new DictionaryLocation { Menu = "Duty", Section = "Dungeon" },
            ["Elemental Weakness Tracking."] = new DictionaryLocation { Menu = "Duty", Section = "Field Ops" },
            ["Enable cactbot timeline integration (Extremely experimental)"] = new DictionaryLocation { Menu = "Extra", Section = "Others" },
            ["Event"] = new DictionaryLocation { Menu = "Extra", Section = "Event" },
            ["Extreme"] = new DictionaryLocation { Menu = "Duty", Section = "Extreme" },
            ["Field Ops"] = new DictionaryLocation { Menu = "Duty", Section = "Field Ops" },
            ["From Clipboard"] = new DictionaryLocation { Menu = "Actions", Section = "" },
            ["Gearsetter is not enabled"] = new DictionaryLocation { Menu = "AutoDuty", Section = "" },
            ["Healing Usage and Control"] = new DictionaryLocation { Menu = "Auto", Section = "Healing Usage and Control" },
            ["HP standard deviation for using AoE heal."] = new DictionaryLocation { Menu = "Auto", Section = "Healing Usage and Control" },
            ["Ignore Invincibility for PvP purposes."] = new DictionaryLocation { Menu = "Duty", Section = "PvP" },
            ["Ignore TTK for PvP purposes."] = new DictionaryLocation { Menu = "Duty", Section = "PvP" },
            ["In this window, you can set parameters that can be customized using lists."] = new DictionaryLocation { Menu = "List", Section = "" },
            ["In this window, you can set which macro will be triggered after using an action."] = new DictionaryLocation { Menu = "Extra", Section = "Event" },
            ["Information"] = new DictionaryLocation { Menu = "UI", Section = "Information" },
            ["Internal"] = new DictionaryLocation { Menu = "Extra", Section = "Internal" },
            ["Is Shared"] = new DictionaryLocation { Menu = "Extra", Section = "Event" },
            ["Lock actions when casting Collective Unconscious during AOE mitigations."] = new DictionaryLocation { Menu = "Extra", Section = "Others" },
            ["Lock actions when casting Passage Of Arms during AOE mitigations."] = new DictionaryLocation { Menu = "Extra", Section = "Others" },
            ["Lock actions when casting Phantom Flurry."] = new DictionaryLocation { Menu = "Extra", Section = "Others" },
            ["Lock out GCD cycle if you are below 50% HP and have over 2000 MP for heals (Experimental)."] = new DictionaryLocation { Menu = "Duty", Section = "PvP" },
            ["Map-specific settings"] = new DictionaryLocation { Menu = "List", Section = "Map-specific settings" },
            ["Not In Combat random delay range."] = new DictionaryLocation { Menu = "Basic", Section = "Timer" },
            ["Open Action Trace File"] = new DictionaryLocation { Menu = "Debug", Section = "" },
            ["Others"] = new DictionaryLocation { Menu = "Extra", Section = "Others" },
            ["Prioritize Low HP tank for tankbusters."] = new DictionaryLocation { Menu = "Auto", Section = "Healing Usage and Control" },
            ["PvP"] = new DictionaryLocation { Menu = "Duty", Section = "PvP" },
            ["Raise non-Healers from bottom of party list to the top (Light Party 2 Healer Behavior)"] = new DictionaryLocation { Menu = "Auto", Section = "Healing Usage and Control" },
            ["Random range of simulated presses per action"] = new DictionaryLocation { Menu = "UI", Section = "Information" },
            ["Reset and Stop Casting List"] = new DictionaryLocation { Menu = "Actions", Section = "" },
            ["Reset and Update AOE List"] = new DictionaryLocation { Menu = "Actions", Section = "" },
            ["Reset and Update Dispell Debuff List"] = new DictionaryLocation { Menu = "List", Section = "Statuses" },
            ["Reset and Update Invuln Status List"] = new DictionaryLocation { Menu = "List", Section = "Statuses" },
            ["Reset and Update Knockback List"] = new DictionaryLocation { Menu = "Actions", Section = "" },
            ["Reset and Update No Casting Status List"] = new DictionaryLocation { Menu = "List", Section = "Statuses" },
            ["Reset and Update Priority Status List"] = new DictionaryLocation { Menu = "List", Section = "Statuses" },
            ["Reset and Update Tankbuster List"] = new DictionaryLocation { Menu = "Actions", Section = "" },
            ["Restore Configs"] = new DictionaryLocation { Menu = "Extra", Section = "Internal" },
            ["Right-click to copy this tip."] = new DictionaryLocation { Menu = "Main", Section = "Many thanks to Ko-fi sponsors." },
            ["RSR focuses on the rotation itself. These are side features. Subject to removal at any time."] = new DictionaryLocation { Menu = "Extra", Section = "Others" },
            ["Savage"] = new DictionaryLocation { Menu = "Duty", Section = "Savage" },
            ["Set Blue Mage Actions Automatically"] = new DictionaryLocation { Menu = "Extra", Section = "Others" },
            ["Set RSR to PvP specific state when enabled in PvP zone."] = new DictionaryLocation { Menu = "Duty", Section = "PvP" },
            ["Show cactbot event toasts (debug)"] = new DictionaryLocation { Menu = "Extra", Section = "Others" },
            ["SkipCutscene is not enabled"] = new DictionaryLocation { Menu = "AutoDuty", Section = "" },
            ["Statuses"] = new DictionaryLocation { Menu = "List", Section = "Statuses" },
            ["Stop single target GCD healing after reaching threshold. (EXTREMELY Experimental)"] = new DictionaryLocation { Menu = "Auto", Section = "Healing Usage and Control" },
            ["The HP for using Guard."] = new DictionaryLocation { Menu = "Duty", Section = "PvP" },
            ["The HP%% for tank to use invulnerability"] = new DictionaryLocation { Menu = "Auto", Section = "Action Usage and Control" },
            ["The range of random delay before interrupting hostile targets."] = new DictionaryLocation { Menu = "Auto", Section = "Action Usage and Control" },
            ["The Masked Carnivale"] = new DictionaryLocation { Menu = "Duty", Section = "The Masked Carnivale" },
            ["Timer"] = new DictionaryLocation { Menu = "Basic", Section = "Timer" },
            ["Treasure Dungeon"] = new DictionaryLocation { Menu = "Duty", Section = "Treasure Dungeon" },
            ["Treasure Dungeons - Numbered mob logic."] = new DictionaryLocation { Menu = "Duty", Section = "Treasure Dungeon" },
            ["Treasure Dungeons - Timed mob logic."] = new DictionaryLocation { Menu = "Duty", Section = "Treasure Dungeon" },
            ["Treat focus targeted player as party member in alliance raids"] = new DictionaryLocation { Menu = "Auto", Section = "Healing Usage and Control" },
            ["Treat your chocobo as a party member"] = new DictionaryLocation { Menu = "Auto", Section = "Healing Usage and Control" },
            ["Ultimate"] = new DictionaryLocation { Menu = "Duty", Section = "Ultimate" },
            ["Use Purify to remove Bind debuff"] = new DictionaryLocation { Menu = "Duty", Section = "PvP" },
            ["Use Purify to remove Deep Freeze debuff"] = new DictionaryLocation { Menu = "Duty", Section = "PvP" },
            ["Use Purify to remove Heavy debuff"] = new DictionaryLocation { Menu = "Duty", Section = "PvP" },
            ["Use Purify to remove Miracle of Nature debuff"] = new DictionaryLocation { Menu = "Duty", Section = "PvP" },
            ["Use Purify to remove Silence debuff"] = new DictionaryLocation { Menu = "Duty", Section = "PvP" },
            ["Use Purify to remove Stun debuff"] = new DictionaryLocation { Menu = "Duty", Section = "PvP" },
            ["Useful information and macro list."] = new DictionaryLocation { Menu = "Main", Section = "Links" },
            ["Variant Dungeon"] = new DictionaryLocation { Menu = "Duty", Section = "Variant Dungeon" },
            ["vnavmesh is installed and enabled"] = new DictionaryLocation { Menu = "AutoDuty", Section = "" },
            ["Windows"] = new DictionaryLocation { Menu = "UI", Section = "Windows" },
        };

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void TextUnformattedDelegate(byte* text, byte* textEnd);
    // Dalamud Bindings (2025+) の ImGui.TextWrapped(string) は安全化された cimgui 経路を使用する。
    // RSR のチェック項目ラベルは TextUnformatted ではなくこの経路に来る。
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void TextWrappedDelegate(byte* text);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte CheckboxDelegate(byte* label, byte* value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte ButtonDelegate(byte* label, Vector2 size);
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
    private delegate byte ComboFnStrPtrDelegate(byte* label, int* currentItem, nint getter, void* userData, int itemsCount, int popupMaxHeightInItems);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte BeginComboDelegate(byte* label, byte* previewValue, int flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void EndComboDelegate();
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

    // v0.0.66: v0.0.35 の未翻訳取得結果を基準にした所属補完表。
    // 日本語訳がまだ空欄の項目も Locations に登録し、翻訳辞書で正しいメニューへ表示する。
    // 同一文字列が複数セクションで観測された場合は、共通するメニューだけを保持する。
    private static readonly IReadOnlyDictionary<string, DictionaryLocation> RsrCapturedLocationsV42 =
        new Dictionary<string, DictionaryLocation>(StringComparer.Ordinal)
        {
            ["/rotation AntiKnockback"] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["/rotation Auto"] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["/rotation Burst"] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["/rotation Cycle"] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["/rotation DefenseArea"] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["/rotation DefenseSingle"] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["/rotation DispelStancePositional"] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["/rotation EndSpecial"] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["/rotation HealArea"] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["/rotation HealSingle"] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["/rotation Manual"] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["/rotation MoveBack"] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["/rotation MoveForward"] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["/rotation NextAction"] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["/rotation NoCasting"] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["/rotation Off"] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["/rotation RaiseShirk"] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["/rotation Speed"] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["/rotation TargetOnly"] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["Activate auto mode when countdown starts"] = new DictionaryLocation { Menu = "Auto", Section = "Auto Switch" },
            ["AI slop clone of RSR"] = new DictionaryLocation { Menu = "Main", Section = "Compatibility" },
            ["Auto rotation and targetting conflicts"] = new DictionaryLocation { Menu = "Main", Section = "Compatibility" },
            ["Auto turn off RSR when combat is over for more than:"] = new DictionaryLocation { Menu = "Auto", Section = "Auto Switch" },
            ["Auto-use oGCD abilities"] = new DictionaryLocation { Menu = "Auto", Section = "Action Usage and Control" },
            ["Automatic Healing Thresholds"] = new DictionaryLocation { Menu = "Auto", Section = "Healing Usage and Control" },
            ["Automatically turn off combat state during cutscenes."] = new DictionaryLocation { Menu = "Auto", Section = "Auto Switch" },
            ["Automatically turn off combat state when a duty (instance) ends."] = new DictionaryLocation { Menu = "Auto", Section = "Auto Switch" },
            ["Automatically turn off combat state when moving between different areas."] = new DictionaryLocation { Menu = "Auto", Section = "Auto Switch" },
            ["Automatically turn off combat state when you change your job/class."] = new DictionaryLocation { Menu = "Auto", Section = "Auto Switch" },
            ["Automatically turn off combat state when your character dies."] = new DictionaryLocation { Menu = "Auto", Section = "Auto Switch" },
            ["Automatically use HP Potions"] = new DictionaryLocation { Menu = "Auto", Section = "Action Usage and Control" },
            ["Automatically use MP Potions"] = new DictionaryLocation { Menu = "Auto", Section = "Action Usage and Control" },
            ["Automatically use Phoenix Down"] = new DictionaryLocation { Menu = "Auto", Section = "Action Usage and Control" },
            ["Broken"] = new DictionaryLocation { Menu = "Main", Section = "Compatibility" },
            ["Cinder Drift Extreme - Pall Targeting logic."] = new DictionaryLocation { Menu = "Duty", Section = "Extreme" },
            ["Click to execute the command: /rotation AntiKnockback\nRight-click to copy the command: /rotation AntiKnockback"] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["Click to execute the command: /rotation Auto\nRight-click to copy the command: /rotation Auto"] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["Click to execute the command: /rotation Burst\nRight-click to copy the command: /rotation Burst"] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["Click to execute the command: /rotation Cycle\nRight-click to copy the command: /rotation Cycle"] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["Click to execute the command: /rotation DefenseArea\nRight-click to copy the command: /rotation DefenseArea"] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["Click to execute the command: /rotation DefenseSingle\nRight-click to copy the command: /rotation DefenseSingle"] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["Click to execute the command: /rotation DispelStancePositional\nRight-click to copy the command: /rotation DispelStancePositional"] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["Click to execute the command: /rotation EndSpecial\nRight-click to copy the command: /rotation EndSpecial"] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["Click to execute the command: /rotation HealArea\nRight-click to copy the command: /rotation HealArea"] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["Click to execute the command: /rotation HealSingle\nRight-click to copy the command: /rotation HealSingle"] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["Click to execute the command: /rotation Manual\nRight-click to copy the command: /rotation Manual"] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["Click to execute the command: /rotation MoveBack\nRight-click to copy the command: /rotation MoveBack"] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["Click to execute the command: /rotation MoveForward\nRight-click to copy the command: /rotation MoveForward"] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["Click to execute the command: /rotation NextAction\nRight-click to copy the command: /rotation NextAction"] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["Click to execute the command: /rotation NoCasting\nRight-click to copy the command: /rotation NoCasting"] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["Click to execute the command: /rotation Off\nRight-click to copy the command: /rotation Off"] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["Click to execute the command: /rotation RaiseShirk\nRight-click to copy the command: /rotation RaiseShirk"] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["Click to execute the command: /rotation Speed\nRight-click to copy the command: /rotation Speed"] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["Click to execute the command: /rotation TargetOnly\nRight-click to copy the command: /rotation TargetOnly"] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["Cloud of Darkness - Ads phase logic."] = new DictionaryLocation { Menu = "Duty", Section = "Chaotic Alliance Raid" },
            ["Color disabled actions on hotbars"] = new DictionaryLocation { Menu = "UI", Section = "Information" },
            ["Combat Reborn fork of Bossmod, may have issues with auto rotation and targetting conflicts, though mitigations exist"] = new DictionaryLocation { Menu = "Main", Section = "Compatibility" },
            ["Connects to OverlayPlugin's WebSocket server and reacts to cactbot broadcast messages (raidwide, tankbuster, knockback, downtime/untargetable)."] = new DictionaryLocation { Menu = "Extra", Section = "Others" },
            ["Controls how much party members' HP must differ before using AoE healing instead of single-target heals. Lower values require party members to have more similar HP for AoE healing to trigger (more selective). Higher values allow AoE healing even when HP differences are larger (less selective). Adjust only if you want to fine-tune AoE heal behavior.\nPercent"] = new DictionaryLocation { Menu = "Extra", Section = "Others" },
            ["Crash"] = new DictionaryLocation { Menu = "Main", Section = "Compatibility" },
            ["Crucible of the Unbroken"] = new DictionaryLocation { Menu = "Duty", Section = "" },
            ["Custom logic to treat certain drakes as immune to kill them in a specific order for the purposes Variant path 12."] = new DictionaryLocation { Menu = "Duty", Section = "Variant Dungeon" },
            ["Disable hostile actions if something is casting an action on the Gaze/Stop list (EXPERIMENTAL)"] = new DictionaryLocation { Menu = "Auto", Section = "Action Usage and Control" },
            ["Display the plugin's current status in the server information bar."] = new DictionaryLocation { Menu = "UI", Section = "Information" },
            ["Displays rotating tips in the main panel; updates every 7 seconds."] = new DictionaryLocation { Menu = "UI", Section = "Information" },
            ["Do NOT use with XivAlexander, BMR tweaks enabled, or NoClippy - this should automatically disable itself if they are detected, but double check first!"] = new DictionaryLocation { Menu = "Extra", Section = "Others" },
            ["Dohn Mheg - Liars Lyre mechancic."] = new DictionaryLocation { Menu = "Duty", Section = "Dungeon" },
            ["Dynamically adjusts cooldown and animation locks to ensure queued actions resolve immediately regardless of framerate limitations"] = new DictionaryLocation { Menu = "Extra", Section = "Others" },
            ["Enable Action Tracer (ONLY toggle this at dev request)"] = new DictionaryLocation { Menu = "Debug", Section = "" },
            ["Enable to allow the plugin to use HP potions automatically."] = new DictionaryLocation { Menu = "Auto", Section = "Action Usage and Control" },
            ["Enable to allow the plugin to use MP potions automatically."] = new DictionaryLocation { Menu = "Auto", Section = "Action Usage and Control" },
            ["Enable to allow the plugin to use Phoenix Down item. (Experimental feature)"] = new DictionaryLocation { Menu = "Auto", Section = "Action Usage and Control" },
            ["Enable to automatically use anti-knockback abilities when needed based on anti-knockback action list in List menu."] = new DictionaryLocation { Menu = "Auto", Section = "Action Usage and Control" },
            ["Enable tracking of elemental weaknesses for debug. This enables you to track weaknesses to report to dev. See Debug > Occult Crescent Weaknesses."] = new DictionaryLocation { Menu = "Duty", Section = "Field Ops" },
            ["Enemy parts, such as Titan's Heart, will be prioritized as targets."] = new DictionaryLocation { Menu = "Target", Section = "Configuration" },
            ["Experimental, includes Chaotic."] = new DictionaryLocation { Menu = "Auto", Section = "Healing Usage and Control" },
            ["Experimental."] = new DictionaryLocation { Menu = "Auto", Section = "Healing Usage and Control" },
            ["FinishTheDance Logic: False"] = new DictionaryLocation { Menu = "DNC", Section = "" },
            ["Fork of XIV Combo Expanded, may have issues with auto rotation and targetting conflicts"] = new DictionaryLocation { Menu = "Main", Section = "Compatibility" },
            ["Fork of XIV Combo, may have issues with auto rotation and targetting conflicts"] = new DictionaryLocation { Menu = "Main", Section = "Compatibility" },
            ["Fork of XIVSlothCombo, may have issues with auto rotation and targetting conflicts"] = new DictionaryLocation { Menu = "Main", Section = "Compatibility" },
            ["Forked Tower Blood - Dead Star logic."] = new DictionaryLocation { Menu = "Duty", Section = "Field Ops" },
            ["Forked Tower Magic - First Boss Villian/Hero."] = new DictionaryLocation { Menu = "Duty", Section = "Field Ops" },
            ["Full: Use all available AoE actions.\nCleave: Use only single-target AoE actions.\nOff: Do not use any AoE actions."] = new DictionaryLocation { Menu = "Auto", Section = "Action Usage and Control" },
            ["Futures Rewritten (Ultimate) - Crystal Of Darkness logic."] = new DictionaryLocation { Menu = "Duty", Section = "Ultimate" },
            ["HandleTillana Logic: False"] = new DictionaryLocation { Menu = "DNC", Section = "" },
            ["Heal party members with GCD if there is nothing to do in combat."] = new DictionaryLocation { Menu = "Auto", Section = "Healing Usage and Control" },
            ["I understand that Auto On settings will turn RSRs autorotation on, automatically."] = new DictionaryLocation { Menu = "Auto", Section = "Auto Switch" },
            ["If a mob is weak to a specific aspect, only use actions of that aspect."] = new DictionaryLocation { Menu = "Duty", Section = "The Masked Carnivale" },
            ["If the selection mode is based on character facing, i.e., targets within the character's viewpoint are moveable targets.\nIf the selection mode is screen-centered, i.e., targets within a sector drawn upward from the character's point are movable targets.\nDegree"] = new DictionaryLocation { Menu = "Target", Section = "Configuration" },
            ["If this is disabled, RSR will only use the game's built-in soft-targeting for allies for heals, shields, etc."] = new DictionaryLocation { Menu = "Target", Section = "Configuration" },
            ["If this is disabled, you will never use Swiftcast/Dualcast to raise players."] = new DictionaryLocation { Menu = "Auto", Section = "Healing Usage and Control" },
            ["If you have another healer on the team, their healing might put the target player(s) above the healing threshold and you'll waste MP. This interrupts the cast if it happens."] = new DictionaryLocation { Menu = "Auto", Section = "Healing Usage and Control" },
            ["InBurstStatus: False"] = new DictionaryLocation { Menu = "DNC", Section = "" },
            ["InBurstStatus: True"] = new DictionaryLocation { Menu = "DNC", Section = "" },
            ["Info Window's Background"] = new DictionaryLocation { Menu = "UI", Section = "Windows" },
            ["Intercept player input and queue it for RSR to execute the action. (PvE only)"] = new DictionaryLocation { Menu = "Auto", Section = "Action Usage and Control" },
            ["It is recommended to check this option if you are playing Raids or you can plan the heal and defense ability usage by yourself."] = new DictionaryLocation { Menu = "Auto", Section = "Action Usage and Control" },
            ["Jeuno: The First Walk - The Ark Angels logic."] = new DictionaryLocation { Menu = "Duty", Section = "Alliance Raid" },
            ["Limitless Blue Extreme - Whaleback logic."] = new DictionaryLocation { Menu = "Duty", Section = "Extreme" },
            ["Lock movement when casting or performing certain actions."] = new DictionaryLocation { Menu = "Extra", Section = "Others" },
            ["M10S - Firesnaking/Watersnaking targeting logic."] = new DictionaryLocation { Menu = "Duty", Section = "Savage" },
            ["M8S - Wolf Pack/Stone Pack logic."] = new DictionaryLocation { Menu = "Duty", Section = "Savage" },
            ["M9S - Ads Targeting logic."] = new DictionaryLocation { Menu = "Duty", Section = "Savage" },
            ["M9S - Cell Targeting logic."] = new DictionaryLocation { Menu = "Duty", Section = "Savage" },
            ["M9S - Only use cleave logic."] = new DictionaryLocation { Menu = "Duty", Section = "Savage" },
            ["May have issues with Auto rotation and targetting conflicts"] = new DictionaryLocation { Menu = "Main", Section = "Compatibility" },
            ["May have issues with skill queueing settings"] = new DictionaryLocation { Menu = "Main", Section = "Compatibility" },
            ["Never use any AoE action when this may attack mobs that are not hostile targets."] = new DictionaryLocation { Menu = "Auto", Section = "Action Usage and Control" },
            ["No"] = new DictionaryLocation { Menu = "Main", Section = "Compatibility" },
            ["None"] = new DictionaryLocation { Menu = "UI", Section = "Information" },
            ["North Horn CE - Tiny Mage targetting logic."] = new DictionaryLocation { Menu = "Duty", Section = "Field Ops" },
            ["O12S - Packet Filter logic."] = new DictionaryLocation { Menu = "Duty", Section = "Savage" },
            ["Open Config Folder"] = new DictionaryLocation { Menu = "Main", Section = "Links" },
            ["Pilgrim's Traverse - Eminent Grief logic."] = new DictionaryLocation { Menu = "Duty", Section = "Deep Dungeon" },
            ["Prevent specific aspected actions from being used against mobs that are immune to that aspect."] = new DictionaryLocation { Menu = "Duty", Section = "The Masked Carnivale" },
            ["Prevent specific Bind actions from being used against mobs that are immune to those effects."] = new DictionaryLocation { Menu = "Duty", Section = "The Masked Carnivale" },
            ["Prevent specific Blind actions from being used against mobs that are immune to those effects."] = new DictionaryLocation { Menu = "Duty", Section = "The Masked Carnivale" },
            ["Prevent specific Flat Damage/Death actions from being used against mobs that are immune to those effects."] = new DictionaryLocation { Menu = "Duty", Section = "The Masked Carnivale" },
            ["Prevent specific Heavy actions from being used against mobs that are immune to those effects."] = new DictionaryLocation { Menu = "Duty", Section = "The Masked Carnivale" },
            ["Prevent specific Interrupt actions from being used against mobs that are immune to those effects."] = new DictionaryLocation { Menu = "Duty", Section = "The Masked Carnivale" },
            ["Prevent specific Paralysis actions from being used against mobs that are immune to those effects."] = new DictionaryLocation { Menu = "Duty", Section = "The Masked Carnivale" },
            ["Prevent specific Petrification actions from being used against mobs that are immune to those effects."] = new DictionaryLocation { Menu = "Duty", Section = "The Masked Carnivale" },
            ["Prevent specific Sleep actions from being used against mobs that are immune to those effects."] = new DictionaryLocation { Menu = "Duty", Section = "The Masked Carnivale" },
            ["Prevent specific Slow actions from being used against mobs that are immune to those effects."] = new DictionaryLocation { Menu = "Duty", Section = "The Masked Carnivale" },
            ["Prevent specific Stun actions from being used against mobs that are immune to those effects."] = new DictionaryLocation { Menu = "Duty", Section = "The Masked Carnivale" },
            ["Prevents your character from moving while casting or using specific actions."] = new DictionaryLocation { Menu = "Extra", Section = "Others" },
            ["Previously engaged targets (Non-Tanks)"] = new DictionaryLocation { Menu = "Target", Section = "Hostile" },
            ["Prioritize A tier tomeliths in Shatter."] = new DictionaryLocation { Menu = "Duty", Section = "PvP" },
            ["Prioritize B tier tomeliths in Shatter."] = new DictionaryLocation { Menu = "Duty", Section = "PvP" },
            ["Prioritize Doornail or Flail based on role and distance to target."] = new DictionaryLocation { Menu = "Duty", Section = "Savage" },
            ["Priotize Red Hot if you have firesnaking buff, and Deep Blue if you have watersnaking buff (also applies to Normal)."] = new DictionaryLocation { Menu = "Duty", Section = "Savage" },
            ["Priotizes limited time mobs in treasure dungeons to resolve extra loot mechanic."] = new DictionaryLocation { Menu = "Duty", Section = "Treasure Dungeon" },
            ["Remove extra framerate-induced cooldown delay"] = new DictionaryLocation { Menu = "Extra", Section = "Others" },
            ["Remove extra lag-induced animation lock delay from instant casts (read tooltip!)"] = new DictionaryLocation { Menu = "Extra", Section = "Others" },
            ["Rotation Solver helps you choose targets and click actions. Any plugin that changes these will affect its decisions.\n\nHere is a list of plugins that have historically (but not always) caused compatibility issues:"] = new DictionaryLocation { Menu = "Main", Section = "Compatibility" },
            ["Sets mobs with number plates as top priority in order from lowest to highest to resolve extra loot mechanic."] = new DictionaryLocation { Menu = "Duty", Section = "Treasure Dungeon" },
            ["ShouldUseLastDance Logic: True"] = new DictionaryLocation { Menu = "DNC", Section = "" },
            ["Show a popup window with the changelog when the plugin updates."] = new DictionaryLocation { Menu = "UI", Section = "Information" },
            ["Show a toast notification with the combat state when changed."] = new DictionaryLocation { Menu = "UI", Section = "Information" },
            ["Show a toast when a cactbot broadcast is received and mapped to a RotationSolver special."] = new DictionaryLocation { Menu = "Extra", Section = "Others" },
            ["Show Action Timeline Window"] = new DictionaryLocation { Menu = "UI", Section = "Windows" },
            ["Show Control Window"] = new DictionaryLocation { Menu = "UI", Section = "Windows" },
            ["Show Cooldown Window"] = new DictionaryLocation { Menu = "UI", Section = "Windows" },
            ["Show Next Action Window"] = new DictionaryLocation { Menu = "UI", Section = "Windows" },
            ["Show plugin status in DTR bar."] = new DictionaryLocation { Menu = "UI", Section = "Information" },
            ["Simply right clicking any action, setting, or toggle will pop up the macro associated with it."] = new DictionaryLocation { Menu = "Main", Section = "Action and Setting Macros" },
            ["Simulate the effect of pressing abilities"] = new DictionaryLocation { Menu = "UI", Section = "Information" },
            ["Skill targetting conflicts"] = new DictionaryLocation { Menu = "Main", Section = "Compatibility" },
            ["Skill Usage"] = new DictionaryLocation { Menu = "Main", Section = "Compatibility" },
            ["Solo Deep Dungeons: if solo, out of combat pull the nearest single enemy; in combat only previously engaged."] = new DictionaryLocation { Menu = "Target", Section = "Hostile" },
            ["Start the addon in Auto mode. When out of combat or when combat starts, switches the target according to the set condition. \n Optionally: You can add the target type to the end of the command you want RSR to do. For example: /rotation Auto Big"] = new DictionaryLocation { Menu = "Main", Section = "State Macros" },
            ["Stop casting if the target dies."] = new DictionaryLocation { Menu = "Extra", Section = "Others" },
            ["Targets with attack markers will be prioritized for actions."] = new DictionaryLocation { Menu = "Target", Section = "Configuration" },
            ["Targets with only 1 HP will be treated as invincible and ignored; for rare cases where target is invincible but is not given a status for it."] = new DictionaryLocation { Menu = "Target", Section = "Configuration" },
            ["Targets with stop markers will not be attacked."] = new DictionaryLocation { Menu = "Target", Section = "Configuration" },
            ["Teaching mode"] = new DictionaryLocation { Menu = "UI", Section = "Information" },
            ["The Epic of Alexander (Ultimate) - Jagd Doll logic."] = new DictionaryLocation { Menu = "Duty", Section = "Ultimate" },
            ["The Epic of Alexander (Ultimate) - True Heart logic."] = new DictionaryLocation { Menu = "Duty", Section = "Ultimate" },
            ["The Ghimlyt Dark - Colossus Rubricatus ad."] = new DictionaryLocation { Menu = "Duty", Section = "Dungeon" },
            ["The Labyrinth of the Ancients - Thanatos logic."] = new DictionaryLocation { Menu = "Duty", Section = "Alliance Raid" },
            ["The Meso Terminal - Thanatos logic."] = new DictionaryLocation { Menu = "Duty", Section = "Dungeon" },
            ["The Omega Protocol (Ultimate) - Packet Filter logic."] = new DictionaryLocation { Menu = "Duty", Section = "Ultimate" },
            ["The Puppets' Bunker - Superior Flight Unit logic."] = new DictionaryLocation { Menu = "Duty", Section = "Alliance Raid" },
            ["The Sil'dihn Subterrane - Drakefamily ads logic."] = new DictionaryLocation { Menu = "Duty", Section = "Variant Dungeon" },
            ["The Tower at Paradigm's Breach - Hansel and Gretel logic."] = new DictionaryLocation { Menu = "Duty", Section = "Alliance Raid" },
            ["The Unmaking Extreme - Shadow logic."] = new DictionaryLocation { Menu = "Duty", Section = "Extreme" },
            ["The Void Ark - Irminsul and Sawtooth logic."] = new DictionaryLocation { Menu = "Duty", Section = "Alliance Raid" },
            ["These commands can be used to open or change plugin settings directly from chat or macros."] = new DictionaryLocation { Menu = "Main", Section = "Action and Setting Macros" },
            ["This option is unavailable with your current job\n \nRoles or jobs needed:\n白魔道士\n学者\n占星術師\n賢者\n幻術士\n吟遊詩人"] = new DictionaryLocation { Menu = "Auto", Section = "Action Usage and Control" },
            ["This setting controls how many oGCDs RSR will try to fit in a single GCD window\nLower numbers mean more oGCDs, but potentially more GCD clipping\nPercent"] = new DictionaryLocation { Menu = "Basic", Section = "Timer" },
            ["This setting is only used if the rotation specifically supports automatic tincture usage."] = new DictionaryLocation { Menu = "Auto", Section = "Action Usage and Control" },
            ["This should clear up any targeting issues when a nail, the boss, and a flail are near each other at the same time."] = new DictionaryLocation { Menu = "Duty", Section = "Savage" },
            ["Treat Alexander and/or Gordius Systems as immune if they have Perfect Defense."] = new DictionaryLocation { Menu = "Duty", Section = "Alliance Raid" },
            ["Treat Bismark Shell/Bismark Corona as immune if you don't have the Whaleback status"] = new DictionaryLocation { Menu = "Duty", Section = "Extreme" },
            ["Treat bosses as immune if you don't have the corresponding status for it."] = new DictionaryLocation { Menu = "Duty", Section = "Ultimate" },
            ["Treat Cells as immune if you don't have the corresponding status for it."] = new DictionaryLocation { Menu = "Duty", Section = "Savage" },
            ["Treat Cloud of Darkness/Stygian as immune if you don't have corresponding buff."] = new DictionaryLocation { Menu = "Duty", Section = "Chaotic Alliance Raid" },
            ["Treat Colossus Rubricatus as immune while its casting scripted action which leads to its death."] = new DictionaryLocation { Menu = "Duty", Section = "Dungeon" },
            ["Treat Crystal Of Darkness ad as immune."] = new DictionaryLocation { Menu = "Duty", Section = "Ultimate" },
            ["Treat each Ark Angel as immune if you don't have corresponding buff."] = new DictionaryLocation { Menu = "Duty", Section = "Alliance Raid" },
            ["Treat each Hansel/Gretel as immune if you are at an angle that would cause you to take rebound damage from the shield mechanic."] = new DictionaryLocation { Menu = "Duty", Section = "Alliance Raid" },
            ["Treat each Superior Flight Unit as immune if you don't have corresponding buff."] = new DictionaryLocation { Menu = "Duty", Section = "Alliance Raid" },
            ["Treat Eminent Grief as immune if you don't have Light Vengeance buff, and treat Devoured Eater as immune if you don't have Dark Vengeance buff."] = new DictionaryLocation { Menu = "Duty", Section = "Deep Dungeon" },
            ["Treat Irminsul and Sawtooth as immune if you don't have corresponding buff."] = new DictionaryLocation { Menu = "Duty", Section = "Alliance Raid" },
            ["Treat Jagd Doll ads as immune when HP is less than 25%."] = new DictionaryLocation { Menu = "Duty", Section = "Ultimate" },
            ["Treat Jailers in second boss fight as immune if you don't have corresponding buff."] = new DictionaryLocation { Menu = "Duty", Section = "Dungeon" },
            ["Treat Liars Lyre as immune if you don't have the Unfooled status."] = new DictionaryLocation { Menu = "Duty", Section = "Dungeon" },
            ["Treat OmegaM/OmegaF as immune if you have their corresponding Packet Filter status (also applies to Normal)."] = new DictionaryLocation { Menu = "Duty", Section = "Savage" },
            ["Treat OmegaM/OmegaF as immune if you have their corresponding Packet Filter status."] = new DictionaryLocation { Menu = "Duty", Section = "Ultimate" },
            ["Treat Pall of Rage/Pall of Grief as immune if you don't have the corresponding status for it."] = new DictionaryLocation { Menu = "Duty", Section = "Extreme" },
            ["Treat Shadow as immune if you don't have the corresponding status"] = new DictionaryLocation { Menu = "Duty", Section = "Extreme" },
            ["Treat Thanatos as immune if you don't have Astral Realignment buff."] = new DictionaryLocation { Menu = "Duty", Section = "Alliance Raid" },
            ["Treat the first boss of Forked Tower as immune if you don't have the corresponding status for it."] = new DictionaryLocation { Menu = "Duty", Section = "Field Ops" },
            ["Treat Tiny Mage CE mobs as immune if you're more than 5 yalms away and they are not the farthest along their cast."] = new DictionaryLocation { Menu = "Duty", Section = "Field Ops" },
            ["Treat Triton/Nereid/Phobos as immune if you don't have the corresponding status for it."] = new DictionaryLocation { Menu = "Duty", Section = "Field Ops" },
            ["Treat True Heart ad as immune."] = new DictionaryLocation { Menu = "Duty", Section = "Ultimate" },
            ["Treat Wolf of Wind/Wolf of Stone as immune if you don't have the corresponding status for it."] = new DictionaryLocation { Menu = "Duty", Section = "Savage" },
            ["Use beneficial ground-targeted actions"] = new DictionaryLocation { Menu = "Auto", Section = "Healing Usage and Control" },
            ["Use beneficial ground-targeted actions on party Tank if present, skipping other logic."] = new DictionaryLocation { Menu = "Auto", Section = "Healing Usage and Control" },
            ["Use beneficial ground-targeted actions only on self, skipping other logic."] = new DictionaryLocation { Menu = "Auto", Section = "Healing Usage and Control" },
            ["Use beneficial ground-targeted actions when moving."] = new DictionaryLocation { Menu = "Auto", Section = "Healing Usage and Control" },
            ["Use defensive actions"] = new DictionaryLocation { Menu = "Auto", Section = "Action Usage and Control" },
            ["Use healing abilities when playing a non-healer role."] = new DictionaryLocation { Menu = "Auto", Section = "Healing Usage and Control" },
            ["UseClosedPosition Logic: False"] = new DictionaryLocation { Menu = "DNC", Section = "" },
            ["UseStandardStep Logic: False"] = new DictionaryLocation { Menu = "DNC", Section = "" },
            ["When enabled, actions you have disabled in RSR will be tinted on in-game hotbars."] = new DictionaryLocation { Menu = "UI", Section = "Information" },
            ["When enabled, non-healer jobs (such as DPS or tanks) will only use healing abilities if there are no healers in the party, or if all healers are incapacitated (at 0 HP). \nIf at least one healer is alive, non-healers will not use healing abilities."] = new DictionaryLocation { Menu = "Auto", Section = "Healing Usage and Control" },
            ["When in a Fate, only Fate targets are considered. When not in a Fate, Fate targets are ignored."] = new DictionaryLocation { Menu = "Target", Section = "Configuration" },
            ["Windurst: The Third Walk - Alexander Resurrected logic."] = new DictionaryLocation { Menu = "Duty", Section = "Alliance Raid" },
            ["Yalms"] = new DictionaryLocation { Menu = "Auto", Section = "Action Usage and Control" },
            ["Yes"] = new DictionaryLocation { Menu = "Main", Section = "Compatibility" },
            ["##-##Rotation Solver Remove Territory Target Name 0"] = new DictionaryLocation { Menu = "List", Section = "Map-specific settings" },
        };

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

    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commandManager, IGameInteropProvider interop, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.interop = interop;
        this.log = log;
        config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        config.EnsurePlugins();
        EnsureCaptureDictionaries();
        // v0.0.23: 完全リセット版。更新時に一度だけ旧データを全消去する。
        // バンドル済み旧標準辞書も CleanSlateMode で無効化し、今回の再取得結果だけを新基準にする。
        if (config.DataResetVersion < 23)
        {
            foreach (var state in config.Plugins.Values)
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
            foreach (var kv in RsrBundledLocations)
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
            foreach (var kv in RsrBundledLocations)
                rsr.Locations[kv.Key] = new DictionaryLocation { Menu = kv.Value.Menu, Section = kv.Value.Section };
            foreach (var kv in RsrCapturedLocationsV42)
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

        commandManager.AddHandler(Command, new CommandInfo(OnCommand) { HelpMessage = "Plugin JP Helper を開きます。" });
        pluginInterface.UiBuilder.Draw += Draw;
        pluginInterface.UiBuilder.OpenConfigUi += OpenUi;
        pluginInterface.UiBuilder.OpenMainUi += OpenUi;
        InstallHooks();
    }

    private void InstallHooks()
    {
        var installed = new List<string>();
        var failed = new List<string>();
        try { textHook = interop.HookFromSymbol<TextUnformattedDelegate>("cimgui.dll", "igTextUnformatted", TextUnformattedDetour); textHook.Enable(); installed.Add("Text"); }
        catch (Exception ex) { failed.Add("Text"); log.Warning(ex, "[PluginJPHelper] igTextUnformatted hook failed"); }
        try { textWrappedHook = interop.HookFromSymbol<TextWrappedDelegate>("cimgui.dll", "igTextWrapped", TextWrappedDetour); textWrappedHook.Enable(); installed.Add("TextWrapped"); }
        catch (Exception ex) { failed.Add("TextWrapped"); log.Warning(ex, "[PluginJPHelper] igTextWrapped hook failed"); }
        try { checkboxHook = interop.HookFromSymbol<CheckboxDelegate>("cimgui.dll", "igCheckbox", CheckboxDetour); checkboxHook.Enable(); installed.Add("Checkbox"); }
        catch (Exception ex) { failed.Add("Checkbox"); log.Warning(ex, "[PluginJPHelper] igCheckbox hook failed"); }
        try { buttonHook = interop.HookFromSymbol<ButtonDelegate>("cimgui.dll", "igButton", ButtonDetour); buttonHook.Enable(); installed.Add("Button"); }
        catch (Exception ex) { failed.Add("Button"); log.Warning(ex, "[PluginJPHelper] igButton hook failed"); }
        try { selectableHook = interop.HookFromSymbol<SelectableDelegate>("cimgui.dll", "igSelectable_Bool", SelectableDetour); selectableHook.Enable(); installed.Add("Selectable"); }
        catch (Exception ex) { failed.Add("Selectable"); log.Warning(ex, "[PluginJPHelper] igSelectable_Bool hook failed"); }
        try { selectablePtrHook = interop.HookFromSymbol<SelectablePtrDelegate>("cimgui.dll", "igSelectable_BoolPtr", SelectablePtrDetour); selectablePtrHook.Enable(); installed.Add("SelectablePtr"); }
        catch (Exception ex) { failed.Add("SelectablePtr"); log.Warning(ex, "[PluginJPHelper] igSelectable_BoolPtr hook failed"); }
        try { comboStrArrHook = interop.HookFromSymbol<ComboStrArrDelegate>("cimgui.dll", "igCombo_Str_arr", ComboStrArrDetour); comboStrArrHook.Enable(); installed.Add("ComboStrArr"); }
        catch (Exception ex) { failed.Add("ComboStrArr"); log.Warning(ex, "[PluginJPHelper] igCombo_Str_arr hook failed"); }
        try { comboStrHook = interop.HookFromSymbol<ComboStrDelegate>("cimgui.dll", "igCombo_Str", ComboStrDetour); comboStrHook.Enable(); installed.Add("ComboStr"); }
        catch (Exception ex) { failed.Add("ComboStr"); log.Warning(ex, "[PluginJPHelper] igCombo_Str hook failed"); }
        try { comboFnStrPtrHook = interop.HookFromSymbol<ComboFnStrPtrDelegate>("cimgui.dll", "igCombo_FnStrPtr", ComboFnStrPtrDetour); comboFnStrPtrHook.Enable(); installed.Add("ComboFn"); }
        catch (Exception ex) { failed.Add("ComboFn"); log.Warning(ex, "[PluginJPHelper] igCombo_FnStrPtr hook failed"); }
        try { beginComboHook = interop.HookFromSymbol<BeginComboDelegate>("cimgui.dll", "igBeginCombo", BeginComboDetour); beginComboHook.Enable(); installed.Add("BeginCombo"); }
        catch (Exception ex) { failed.Add("BeginCombo"); log.Warning(ex, "[PluginJPHelper] igBeginCombo hook failed"); }
        try { endComboHook = interop.HookFromSymbol<EndComboDelegate>("cimgui.dll", "igEndCombo", EndComboDetour); endComboHook.Enable(); installed.Add("EndCombo"); }
        catch (Exception ex) { failed.Add("EndCombo"); log.Warning(ex, "[PluginJPHelper] igEndCombo hook failed"); }
        try { beginTabItemHook = interop.HookFromSymbol<BeginTabItemDelegate>("cimgui.dll", "igBeginTabItem", BeginTabItemDetour); beginTabItemHook.Enable(); installed.Add("BeginTabItem"); }
        catch (Exception ex) { failed.Add("BeginTabItem"); log.Warning(ex, "[PluginJPHelper] igBeginTabItem hook failed"); }
        try { menuItemBoolHook = interop.HookFromSymbol<MenuItemBoolDelegate>("cimgui.dll", "igMenuItem_Bool", MenuItemBoolDetour); menuItemBoolHook.Enable(); installed.Add("MenuItem"); }
        catch (Exception ex) { failed.Add("MenuItem"); log.Warning(ex, "[PluginJPHelper] igMenuItem_Bool hook failed"); }
        try { menuItemBoolPtrHook = interop.HookFromSymbol<MenuItemBoolPtrDelegate>("cimgui.dll", "igMenuItem_BoolPtr", MenuItemBoolPtrDetour); menuItemBoolPtrHook.Enable(); installed.Add("MenuItemPtr"); }
        catch (Exception ex) { failed.Add("MenuItemPtr"); log.Warning(ex, "[PluginJPHelper] igMenuItem_BoolPtr hook failed"); }
        try { beginMenuHook = interop.HookFromSymbol<BeginMenuDelegate>("cimgui.dll", "igBeginMenu", BeginMenuDetour); beginMenuHook.Enable(); installed.Add("BeginMenu"); }
        catch (Exception ex) { failed.Add("BeginMenu"); log.Warning(ex, "[PluginJPHelper] igBeginMenu hook failed"); }
        try { separatorTextHook = interop.HookFromSymbol<SeparatorTextDelegate>("cimgui.dll", "igSeparatorText", SeparatorTextDetour); separatorTextHook.Enable(); installed.Add("SeparatorText"); }
        catch (Exception ex) { failed.Add("SeparatorText"); log.Warning(ex, "[PluginJPHelper] igSeparatorText hook failed"); }
        try { beginHook = interop.HookFromSymbol<BeginDelegate>("cimgui.dll", "igBegin", BeginDetour); beginHook.Enable(); installed.Add("Begin"); }
        catch (Exception ex) { failed.Add("Begin"); log.Warning(ex, "[PluginJPHelper] igBegin hook failed"); }
        try { endHook = interop.HookFromSymbol<EndDelegate>("cimgui.dll", "igEnd", EndDetour); endHook.Enable(); installed.Add("End"); }
        catch (Exception ex) { failed.Add("End"); log.Warning(ex, "[PluginJPHelper] igEnd hook failed"); }
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
        hookStatus = installed.Count == 0 ? "フック失敗（Dalamudログを確認）" : $"有効: {string.Join(", ", installed)}" + (failed.Count > 0 ? $" / 失敗: {string.Join(", ", failed)}" : string.Empty);
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
            var safe = translated.Replace("%", "%%", StringComparison.Ordinal);
            var bytes = Encoding.UTF8.GetBytes(safe + "\0");
            fixed (byte* p = bytes) { textWrappedHook!.Original(p); return; }
        }
        textWrappedHook!.Original(text);
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

    private byte ComboFnStrPtrDetour(byte* label, int* currentItem, nint getter, void* userData, int itemsCount, int popupMaxHeightInItems)
    {
        // まず実機でこの経路を使っているかだけ確認する。getter差し替えは誤動作リスクがあるためまだ行わない。
        Interlocked.Increment(ref comboFnCalls);
        return comboFnStrPtrHook!.Original(label, currentItem, getter, userData, itemsCount, popupMaxHeightInItems);
    }

    private byte BeginComboDetour(byte* label, byte* previewValue, int flags)
    {
        Interlocked.Increment(ref beginComboCalls);
        byte result;
        if (drawingOwnUi || previewValue == null)
        {
            result = beginComboHook!.Original(label, previewValue, flags);
        }
        else
        {
            try
            {
                var src = Marshal.PtrToStringUTF8((nint)previewValue);
                if (!string.IsNullOrEmpty(src) && TryTranslate(src, out var ja))
                {
                    var bytes = Encoding.UTF8.GetBytes(ja + "\0");
                    fixed (byte* p = bytes) result = beginComboHook!.Original(label, p, flags);
                }
                else result = beginComboHook!.Original(label, previewValue, flags);
            }
            catch { result = beginComboHook!.Original(label, previewValue, flags); }
        }

        if (!drawingOwnUi && result != 0) comboOpenDepth++;
        return result;
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

    private bool TryGetTranslationForPlugin(string pluginName, string source, out string translated)
    {
        translated = string.Empty;
        if (!config.Plugins.TryGetValue(pluginName, out var state) || !state.Enabled) return false;
        if (state.UserOverrides.TryGetValue(source, out translated!) && !string.IsNullOrWhiteSpace(translated)) return true;
        return GetActiveStandardDictionary(pluginName).TryGetValue(source, out translated!);
    }

    private static bool AllowsPartialTranslation(string pluginName)
        => string.Equals(pluginName, "DalamudACT", StringComparison.OrdinalIgnoreCase);


    // v0.0.66: RenderText系は「現在のウィンドウがどの対象プラグインか」を確定してから、
    // そのプラグインの辞書だけを参照する。設定値・ImGui ID・入力状態には触れない。
    private bool TryTranslateRenderPointerForCurrentWindow(byte* begin, byte* end, out string translated, out string pluginName)
    {
        translated = string.Empty;
        pluginName = string.Empty;
        if (begin == null) return false;

        var currentWindow = CurrentWindowName;
        if (string.IsNullOrWhiteSpace(currentWindow)) return false;

        foreach (var (name, state) in config.Plugins)
        {
            if (!state.Enabled || !(string.Equals(CurrentWindowOwner, name, StringComparison.Ordinal) || IsTargetWindow(name, currentWindow))) continue;
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

        if (TryTranslateInventoryToolsDynamic(pluginName, source, out translated)) return true;

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
            .Select(kv => (Source: kv.Key, Ja: pluginState.UserOverrides.TryGetValue(kv.Key, out var userJa) && !string.IsNullOrWhiteSpace(userJa) ? userJa : kv.Value))
            .Where(x => !string.IsNullOrWhiteSpace(x.Source) && !string.IsNullOrWhiteSpace(x.Ja))
            .Where(x => !x.Source.Contains("##", StringComparison.Ordinal) && !x.Ja.Contains("##", StringComparison.Ordinal))
            .Where(x => visible.Contains(x.Source, StringComparison.Ordinal))
            .OrderByDescending(x => x.Source.Length)
            .ThenBy(x => x.Source, StringComparer.Ordinal)
            .ToArray();

        if (candidates.Length == 0) return false;
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
        // v0.0.66:
        // DalamudACT の PartyMonitorWindow は ImGui.Text 系ではなく
        // ImDrawList.AddText() で短縮ジョブ名を直接描画する。
        // この経路は通常の Text/RenderText フックを通らないため、DalamudACT の
        // ウィンドウ内だけを対象に最小限の完全一致置換を行う。
        if (drawingOwnUi || textBegin == null || !IsCurrentWindowOwnedBy("DalamudACT"))
        {
            drawListAddTextVec2Hook!.Original(drawList, pos, col, textBegin, textEnd);
            return;
        }

        string? source = null;
        try
        {
            if (textEnd != null && textEnd >= textBegin)
            {
                var len = textEnd - textBegin;
                if (len > 0 && len <= 4096)
                    source = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(textBegin, (int)len));
            }
            else
            {
                source = Marshal.PtrToStringUTF8((nint)textBegin);
            }

            if (!string.IsNullOrEmpty(source))
            {
                // 取得中もこの描画経路を対象側へ追加する。
                if (captureEnabled || baselineCaptureEnabled)
                    CapturePointer(textBegin, textEnd, "DrawListAddText");

                if (TryGetTranslationForPlugin("DalamudACT", source, out var translated) &&
                    !string.IsNullOrWhiteSpace(translated) &&
                    !string.Equals(source, translated, StringComparison.Ordinal))
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
        }
        catch
        {
            // 失敗時は元の描画をそのまま通す。
        }

        drawListAddTextVec2Hook!.Original(drawList, pos, col, textBegin, textEnd);
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
        foreach (var (pluginName, state) in config.Plugins)
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
        if (drawingOwnUi || label == null) return;
        if (!string.Equals(CurrentWindowName, "Configuration", StringComparison.Ordinal)) return;
        if (!config.Plugins.TryGetValue("InventoryTools", out var state) || !state.Enabled) return;

        string? source;
        try { source = Marshal.PtrToStringUTF8((nint)label); }
        catch { return; }
        if (!string.Equals(source, "Wizard", StringComparison.Ordinal)) return;

        if (windowOwnerStack is not { Count: > 0 }) return;
        windowOwnerStack.Pop();
        windowOwnerStack.Push("InventoryTools");
        lastExplicitWindowOwner = "InventoryTools";
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
        var result = beginHook!.Original(name, pOpen, flags);

        string windowName = string.Empty;
        try
        {
            if (name != null)
                windowName = Marshal.PtrToStringUTF8((nint)name) ?? string.Empty;
        }
        catch { }

        windowStack ??= new Stack<string>();
        windowOwnerStack ??= new Stack<string>();

        // v0.0.66: Combo/Tooltip/Popup などの内部ウィンドウは元のプラグイン名を持たない。
        // 直前の通常ウィンドウの所有プラグインを継承し、追加プラグインでもドロップダウン候補や
        // ツールチップを背景側ではなく正しい対象へ取得できるようにする。
        var owner = ResolveWindowOwner(windowName);

        // v0.0.69: Allagan Tools などは、設定画面の内部を Child Window に分割して描画する。
        // 親ウィンドウで対象プラグインを確定できていても、子ウィンドウ名（例: Menu）自体には
        // プラグイン固有名が含まれないため、従来は RenderText 系の所有判定が外れていた。
        // ImGui の Begin が親ウィンドウの描画中にネストしている場合だけ、直近の親所有者を継承する。
        // 辞書検索や部分一致は追加せず、Stack.Peek() 1回だけの軽量な所属継承。
        if (string.IsNullOrWhiteSpace(owner) && windowOwnerStack.Count > 0)
            owner = windowOwnerStack.Peek();

        if (IsTransientImGuiWindow(windowName))
        {

            // v0.0.66:
            // Tooltip/Combo/Popup は親ウィンドウとは別の ImGui Window として描画されるため、
            // 直前の対象プラグイン所有者を短時間だけ継承する。
            // 無関係なウィンドウで lastExplicitWindowOwner を空にしないことが重要。
            if (string.IsNullOrWhiteSpace(owner) &&
                !string.IsNullOrWhiteSpace(lastExplicitWindowOwner) &&
                Environment.TickCount64 - lastExplicitWindowOwnerTick <= 150)
            {
                owner = lastExplicitWindowOwner;
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(owner))
            {
                lastExplicitWindowOwner = owner;
                lastExplicitWindowOwnerTick = Environment.TickCount64;
            }
        }

        windowStack.Push(windowName);
        windowOwnerStack.Push(owner);

        // JP Helper自身の内部ウィンドウは検出一覧へ出さない。
        if (!drawingOwnUi && !string.IsNullOrWhiteSpace(windowName))
            seenWindows.AddOrUpdate(windowName, 1, (_, count) => count + 1);

        return result;
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

    private string ResolveWindowOwner(string? windowName)
    {
        if (string.IsNullOrWhiteSpace(windowName)) return string.Empty;
        foreach (var (pluginName, state) in config.Plugins)
        {
            if (!state.TranslationTarget && !state.Enabled) continue;
            if (IsTargetWindow(pluginName, windowName)) return pluginName;
        }
        return string.Empty;
    }

    private bool IsCurrentWindowOwnedBy(string pluginName)
        => string.Equals(CurrentWindowOwner, pluginName, StringComparison.Ordinal)
           || IsTargetWindow(pluginName, CurrentWindowName);

    private bool IsTargetWindow(string pluginName, string? windowName)
    {
        if (string.IsNullOrWhiteSpace(windowName)) return false;

        var w = windowName.Trim();
        return pluginName switch
        {
            "RSR" => w.Contains("Rotation Solver Reborn", StringComparison.OrdinalIgnoreCase)
                  || w.Contains("RotationSolverReborn", StringComparison.OrdinalIgnoreCase)
                  || w.Contains("Rotation Solver", StringComparison.OrdinalIgnoreCase),

            "BMR" => w.Contains("BossMod Reborn", StringComparison.OrdinalIgnoreCase)
                  || w.Contains("Boss Mod Reborn", StringComparison.OrdinalIgnoreCase)
                  || w.Contains("BossModReborn", StringComparison.OrdinalIgnoreCase),

            "BM" => (w.Contains("BossMod", StringComparison.OrdinalIgnoreCase)
                  || w.Contains("Boss Mod", StringComparison.OrdinalIgnoreCase))
                  && !w.Contains("Reborn", StringComparison.OrdinalIgnoreCase),

            "DalamudACT" => w.Contains("DalamudACT", StringComparison.OrdinalIgnoreCase)
                         || w.Contains("CombatTimelineWindow", StringComparison.OrdinalIgnoreCase)
                         || w.Contains("StatusObserverWindow", StringComparison.OrdinalIgnoreCase)
                         || w.Contains("PartyMonitorWindow", StringComparison.OrdinalIgnoreCase)
                         || w.Contains("StatsPanelWindow", StringComparison.OrdinalIgnoreCase)
                         || w.Contains("SkillMonitorWindow", StringComparison.OrdinalIgnoreCase)
                         || w.Contains("SettingsWindow", StringComparison.OrdinalIgnoreCase),

            _ => config.Plugins.TryGetValue(pluginName, out var custom)
                 && !string.IsNullOrWhiteSpace(custom.WindowKeyword)
                 && custom.WindowKeyword.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Any(keyword => w.Contains(keyword, StringComparison.OrdinalIgnoreCase)),
        };
    }

    private static readonly HashSet<string> RsrFixedMenus = new(StringComparer.Ordinal)
    {
        "Main", "Actions", "List", "Basic", "UI", "Auto", "Target", "Duty", "Extra", "Debug", "AutoDuty"
    };

    private static readonly HashSet<string> RsrJobMenus = new(StringComparer.Ordinal)
    {
        "PLD", "WAR", "DRK", "GNB", "WHM", "SCH", "AST", "SGE", "MNK", "DRG", "NIN", "SAM", "RPR", "VPR",
        "BRD", "MCH", "DNC", "BLM", "SMN", "RDM", "PCT", "BLU"
    };

    // RSRソースの CollapsingHeaderGroup と実機ログで確認できた画面内項目。
    // ここに無いSelectableを意味だけで「項目」と推測しない。
    private static readonly HashSet<string> RsrKnownSections = new(StringComparer.Ordinal)
    {
        "Timer", "Others", "Information", "Windows",
        "Auto Switch", "Action Usage and Control", "Healing Usage and Control",
        "Configuration", "Hostile",
        "Ultimate", "Savage", "Extreme", "Chaotic Alliance Raid", "Alliance Raid", "Dungeon", "Deep Dungeon",
        "Variant Dungeon", "Treasure Dungeon", "Field Ops", "PvP", "The Masked Carnivale", "Crucible of the Unbroken",
        "Event", "Internal",
        "Preset", "Statuses", "Map-specific settings", "Compatibility", "Links", "Many thanks to Ko-fi sponsors.",
        "Action and Setting Macros", "State Macros"
    };

    private void UpdateRsrNavigationContext(byte* label, bool selected)
    {
        if (!selected || label == null || !captureEnabled || capturePlugin != "RSR") return;
        var raw = Marshal.PtrToStringUTF8((nint)label);
        if (string.IsNullOrWhiteSpace(raw)) return;
        var visible = VisibleLabel(raw);

        // RSRの左メニューは別の子ウィンドウで描画されるため、
        // CurrentWindowNameだけで判定すると取りこぼす。
        // ただし誤分類を防ぐため、RSRソースで確認済みのメニュー名かジョブ名だけを候補にする。
        if (RsrFixedMenus.Contains(visible) || RsrJobMenus.Contains(visible) || visible.StartsWith("Duty - ", StringComparison.Ordinal))
        {
            rsrPendingMenuCandidate = visible;
            return;
        }

        // 画面内セクションはRSR本体ウィンドウ内でselectedになったものだけ採用する。
        if (IsTargetWindow("RSR", CurrentWindowName) && RsrKnownSections.Contains(visible))
            rsrCurrentSection = visible;
    }

    private void UpdateRsrNavigationContextAfterClick(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || !captureEnabled || capturePlugin != "RSR") return;
        var visible = VisibleLabel(raw);
        if (RsrFixedMenus.Contains(visible) || RsrJobMenus.Contains(visible) || visible.StartsWith("Duty - ", StringComparison.Ordinal))
        {
            if (!string.Equals(rsrCurrentMenu, visible, StringComparison.Ordinal)) rsrCurrentSection = string.Empty;
            rsrPendingMenuCandidate = visible;
            rsrCurrentMenu = visible;
        }
    }

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
    private bool TryTranslateInventoryToolsDynamic(string pluginName, string source, out string translated)
    {
        translated = string.Empty;
        if (!string.Equals(pluginName, "InventoryTools", StringComparison.OrdinalIgnoreCase)) return false;

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

        static string CategoryJa(string value) => value.Trim().ToLowerInvariant() switch
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

    private bool TryTranslatePointer(byte* begin, byte* end, bool preserveImGuiId, out string translated)
    {
        translated = string.Empty;
        if (begin == null) return false;
        string? source;
        if (end != null && end >= begin)
        {
            var len = end - begin; if (len <= 0 || len > 4096) return false;
            source = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(begin, (int)len));
        }
        else source = Marshal.PtrToStringUTF8((nint)begin);
        if (string.IsNullOrEmpty(source)) return false;

        // まず従来どおり完全一致。既存挙動を変えない。
        if (TryTranslate(source, out translated)) return true;

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
        foreach (var (name, state) in config.Plugins)
        {
            if (!state.Enabled) continue;
            if (string.Equals(CurrentWindowOwner, name, StringComparison.Ordinal) || IsTargetWindow(name, currentWindow))
            {
                pluginName = name;
                break;
            }
        }
        if (string.IsNullOrEmpty(pluginName)) return false;

        if (TryTranslateInventoryToolsDynamic(pluginName, source, out translated)) return true;

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
                    : kv.Value))
            .Where(x => !string.IsNullOrWhiteSpace(x.Source) && !string.IsNullOrWhiteSpace(x.Ja))
            .Where(x => !x.Source.Contains("##", StringComparison.Ordinal) && !x.Source.Contains("###", StringComparison.Ordinal))
            .Where(x => !x.Ja.Contains("##", StringComparison.Ordinal) && !x.Ja.Contains("###", StringComparison.Ordinal))
            .Where(x => visible.Contains(x.Source, StringComparison.Ordinal))
            .OrderByDescending(x => x.Source.Length)
            .ThenBy(x => x.Source, StringComparer.Ordinal)
            .ToArray();

        if (candidates.Length == 0) return false;

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
        const string counterPrefix = "RSR has helped you by clicking actions ";
        const string counterSuffix = " times.";
        if (source.StartsWith(counterPrefix, StringComparison.Ordinal) && source.EndsWith(counterSuffix, StringComparison.Ordinal))
        {
            var count = source[counterPrefix.Length..^counterSuffix.Length];
            translated = $"RSRがアクションを実行した回数: {count}回";
            return true;
        }

        foreach (var (pluginName, state) in config.Plugins)
        {
            if (!state.Enabled) continue;
            if (TryGetTranslationForPlugin(pluginName, source, out translated)) return true;
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
        if (capturePlugin == "RSR" && isTargetWindow && currentWindow.Contains("Rotation Solver Side bar", StringComparison.OrdinalIgnoreCase))
            return;

        if (captureEnabled && isTargetWindow)
        {
            // RSR左メニューは直前に描画されるため、最後に見つけたselectedな
            // 確認済みメニュー候補を、RSR本体の文字列を取得する瞬間に確定する。
            if (capturePlugin == "RSR" && !string.IsNullOrWhiteSpace(rsrPendingMenuCandidate))
            {
                if (!string.Equals(rsrCurrentMenu, rsrPendingMenuCandidate, StringComparison.Ordinal))
                    rsrCurrentSection = string.Empty;
                rsrCurrentMenu = rsrPendingMenuCandidate;
            }

            var menu = capturePlugin == "RSR" ? rsrCurrentMenu : string.Empty;
            var section = capturePlugin == "RSR" ? rsrCurrentSection : string.Empty;
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

    private void OnCommand(string command, string args) => windowOpen = true;
    private void OpenUi() => windowOpen = true;

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

    private void Draw()
    {
        if (!windowOpen) return;
        // Plugin JP Helper 自身のUIは翻訳・取得対象にしない。
        // これにより辞書左列の「英語原文」は必ず原文のまま表示される。
        drawingOwnUi = true;
        try
        {
            ImGui.SetNextWindowSize(new Vector2(920, 650), ImGuiCond.FirstUseEver);
            if (!ImGui.Begin("Plugin JP Helper v0.1.0", ref windowOpen)) { ImGui.End(); return; }

            ImGui.TextWrapped("プラグインの表示を日本語化する翻訳辞書を管理します。初めて使う場合は「未翻訳・取得」から対象プラグインを追加してください。");
            ImGui.Separator();

            ImGui.PushStyleColor(ImGuiCol.Tab, new Vector4(0.16f, 0.20f, 0.26f, 1.00f));
            ImGui.PushStyleColor(ImGuiCol.TabHovered, new Vector4(0.24f, 0.46f, 0.70f, 1.00f));
            ImGui.PushStyleColor(ImGuiCol.TabActive, new Vector4(0.18f, 0.38f, 0.62f, 1.00f));
            if (ImGui.BeginTabBar("mainTabs"))
            {
                if (ImGui.BeginTabItem("翻訳辞書")) { DrawDictionaryTab(); ImGui.EndTabItem(); }
                if (ImGui.BeginTabItem("未翻訳・取得")) { DrawCaptureTab(); ImGui.EndTabItem(); }
                ImGui.EndTabBar();
            }
            ImGui.PopStyleColor(3);
            ImGui.End();
        }
        finally
        {
            drawingOwnUi = false;
        }
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

        var next = config.Plugins.Where(x => x.Value.TranslationTarget).Select(x => x.Key)
            .OrderBy(x => PluginSortKey(x)).ThenBy(x => x, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(next)) selectedPlugin = capturePlugin = next;
        csvStatus = $"{pluginName} を翻訳対象から外しました。辞書データ自体は削除していません。";
    }

    private void DrawDictionaryTab()
    {
        var pluginNames = config.Plugins
            .Where(x => x.Value.TranslationTarget)
            .Select(x => x.Key)
            .OrderBy(x => PluginSortKey(x)).ThenBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        if (pluginNames.Length == 0)
        {
            ImGui.TextDisabled("翻訳対象がありません。「未翻訳・取得」タブからインストール済みプラグインを追加してください。");
            return;
        }
        if (!pluginNames.Contains(selectedPlugin, StringComparer.Ordinal)) selectedPlugin = pluginNames[0];
        foreach (var name in pluginNames)
        {
            if (ImGui.RadioButton($"{name}##dictPlugin", selectedPlugin == name)) selectedPlugin = name;
            ImGui.SameLine();
        }
        ImGui.NewLine();
        var state = config.Plugins[selectedPlugin];
        var enabled = state.Enabled;
        ImGui.PushStyleColor(ImGuiCol.CheckMark, new Vector4(0.28f, 0.78f, 0.48f, 1.00f));
        if (ImGui.Checkbox($"{selectedPlugin} の日本語化を有効##enabled", ref enabled)) { state.Enabled = enabled; SaveConfig(); }
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
            csvStatus = string.IsNullOrWhiteSpace(state.LastCsvPath)
                ? "先にCSVを選択してください。"
                : ImportDictionaryCsv(selectedPlugin, state.LastCsvPath);
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
        if (ImGui.BeginTable($"dict_{selectedPlugin}_{category ?? "all"}", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable, new Vector2(0, -35)))
        {
            ImGui.TableSetupColumn("画面内項目", ImGuiTableColumnFlags.WidthFixed, 150);
            ImGui.TableSetupColumn("原文（英語/中国語）", ImGuiTableColumnFlags.WidthStretch, 0.38f);
            ImGui.TableSetupColumn("日本語訳（編集可）", ImGuiTableColumnFlags.WidthStretch, 0.46f);
            ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 90);

            // ヘッダー行を縦スクロール時も固定。
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableNextRow(ImGuiTableRowFlags.Headers);

            ImGui.TableSetColumnIndex(0);
            ImGui.TableHeader(GetSortableHeaderLabel("画面内項目", 1));
            if (ImGui.IsItemClicked()) SetDictionarySortFromHeader(1);

            ImGui.TableSetColumnIndex(1);
            ImGui.TableHeader(GetSortableHeaderLabel("原文（英語/中国語）", 0));
            if (ImGui.IsItemClicked()) SetDictionarySortFromHeader(0);

            ImGui.TableSetColumnIndex(2);
            ImGui.TableHeader(GetSortableHeaderLabel("日本語訳（編集可）", 2));
            if (ImGui.IsItemClicked()) SetDictionarySortFromHeader(2);

            ImGui.TableSetColumnIndex(3);
            ImGui.TableHeader("操作");

            var rows = GetDictionaryCatalog(selectedPlugin)
                .Select(kv => new
                {
                    Key = kv.Key,
                    DefaultJa = kv.Value,
                    CurrentJa = state.UserOverrides.TryGetValue(kv.Key, out var ov) && !string.IsNullOrWhiteSpace(ov) ? ov : kv.Value,
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
                        SaveConfig();
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

        ImGui.TextWrapped("新しい翻訳辞書を作るときに使います。まず「インストール済みプラグインから追加」で日本語化したいプラグインを追加してください。");
        ImGui.TextDisabled("追加後は「取得開始」→対象プラグインの画面やツールチップを一通り表示→「取得停止」の順で操作します。ツールチップ等も可能な限り対象プラグインへ自動分類します。");

        ImGui.Separator();
        ImGui.TextUnformatted("インストール済みプラグインから追加");
        ImGui.TextDisabled("日本語化したいプラグインを選択して追加します。");
        var installed = pluginInterface.InstalledPlugins
            .Where(p => !string.Equals(p.InternalName, pluginInterface.InternalName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.InternalName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var selectedInstalled = installed.FirstOrDefault(p => string.Equals(p.InternalName, installedPluginSelection, StringComparison.Ordinal));
        var installedPreview = selectedInstalled == null ? "選択してください" : $"{selectedInstalled.Name} ({selectedInstalled.InternalName})";
        ImGui.SetNextItemWidth(360);
        if (ImGui.BeginCombo("##installedPluginSelect", installedPreview))
        {
            foreach (var p in installed)
            {
                var already = config.Plugins.TryGetValue(p.InternalName, out var existing) && existing.TranslationTarget;
                var label = $"{p.Name} ({p.InternalName}){(already ? "  [追加済み]" : string.Empty)}{(p.IsLoaded ? string.Empty : "  [停止中]")}";
                var selected = string.Equals(installedPluginSelection, p.InternalName, StringComparison.Ordinal);
                if (ImGui.Selectable($"{label}##installed_{p.InternalName}", selected)) installedPluginSelection = p.InternalName;
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (ActionButton("翻訳対象に追加", ButtonRole.Primary) && !captureEnabled) AddInstalledPluginTarget();

        if (ImGui.TreeNode("手入力で追加（自動判定できない場合のみ）"))
        {
            ImGui.TextDisabled("通常は使いません。自動判定できないプラグインだけ手入力します。");
            ImGui.SetNextItemWidth(180); ImGui.InputTextWithHint("##customPlugin", "プラグイン名", ref customPluginName, 64);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(260); ImGui.InputTextWithHint("##customWindow", "ウィンドウ名キーワード", ref customWindowKeyword, 128);
            ImGui.SameLine();
            if (ActionButton("手入力で追加", ButtonRole.Primary) && !captureEnabled) AddCustomPlugin();
            ImGui.TreePop();
        }


        ImGui.Spacing();
        ImGui.TextUnformatted("翻訳対象プラグイン");
        var captureNames = config.Plugins.Where(x => x.Value.TranslationTarget).Select(x => x.Key)
            .OrderBy(x => PluginSortKey(x)).ThenBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        if (captureNames.Length > 0 && !captureNames.Contains(capturePlugin, StringComparer.Ordinal)) capturePlugin = captureNames[0];
        foreach (var name in captureNames)
        {
            if (ImGui.RadioButton($"{name}##capturePlugin", capturePlugin == name) && !captureEnabled) capturePlugin = name;
            ImGui.SameLine();
        }
        ImGui.NewLine();

        if (!string.IsNullOrWhiteSpace(capturePlugin) && !captureEnabled)
        {
            if (ActionButton("選択中を翻訳対象から外す", ButtonRole.Danger))
                RemoveTranslationTarget(capturePlugin);
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

        ImGui.Separator();
        ImGui.TextUnformatted("未翻訳の取得");
        if (captureEnabled || baselineCaptureEnabled)
        {
            if (ActionButton("取得停止", ButtonRole.Warning))
            {
                captureEnabled = false;
                baselineCaptureEnabled = false;
                ApplyCapturedLocations(capturePlugin);
            }
            ImGui.SameLine();
            ImGui.TextUnformatted($"{capturePlugin} を取得中");
        }
        else
        {
            if (ActionButton("取得開始", ButtonRole.Success))
            {
                current.Clear();
                backgroundCaptured.Clear();
                rsrCurrentMenu = string.Empty;
                rsrCurrentSection = string.Empty;
                rsrPendingMenuCandidate = string.Empty;
                baselineCaptureEnabled = true;
                captureEnabled = true;
                SaveConfig();
            }
            ImGui.SameLine();
            ImGui.TextDisabled("対象プラグインの画面を一通り開いて操作してください。");
        }

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

        var untranslated = current.Values.Where(x => !IsKnown(x.Text)).OrderBy(x => x.Text, StringComparer.OrdinalIgnoreCase).ToArray();
        var bgUntranslated = backgroundCaptured.Values.Where(x => !IsKnown(x.Text)).OrderBy(x => x.Text, StringComparer.OrdinalIgnoreCase).ToArray();
        ImGui.TextUnformatted($"対象: 取得 {current.Count}件 / 未翻訳 {untranslated.Length}件    その他: 取得 {backgroundCaptured.Count}件 / 未翻訳 {bgUntranslated.Length}件");

        ImGui.PushStyleColor(ImGuiCol.Tab, new Vector4(0.16f, 0.20f, 0.26f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.TabHovered, new Vector4(0.24f, 0.46f, 0.70f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.TabActive, new Vector4(0.18f, 0.38f, 0.62f, 1.00f));
        if (ImGui.BeginTabBar("captureResultTabs"))
        {
            if (ImGui.BeginTabItem($"対象プラグイン ({untranslated.Length})"))
            {
                DrawCapturedTable($"plugin_{capturePlugin}", untranslated);
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem($"その他取得分 ({bgUntranslated.Length})"))
            {
                DrawCapturedTable("other", bgUntranslated);
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
        ImGui.PopStyleColor(3);
    }

    private static void DrawCapturedTable(string id, CapturedItem[] items)
    {
        if (ImGui.BeginTable($"untranslated_{id}", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable, new Vector2(0, -25)))
        {
            ImGui.TableSetupColumn("メニュー", ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("画面内項目", ImGuiTableColumnFlags.WidthFixed, 150);
            ImGui.TableSetupColumn("種類", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("回数", ImGuiTableColumnFlags.WidthFixed, 55);
            ImGui.TableSetupColumn("未翻訳の英語", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();
            foreach (var item in items)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(item.Menu);
                ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted(item.Section);
                ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(item.Kind);
                ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(item.Count.ToString());
                ImGui.TableSetColumnIndex(4); ImGui.TextWrapped(item.Text);
            }
            ImGui.EndTable();
        }
    }

    private static readonly HashSet<string> KeepAsIs = new(StringComparer.Ordinal)
    {
        "DNC", "BRD", "MCH", "BLM", "SMN", "RDM", "PCT", "MNK", "DRG", "NIN", "SAM", "RPR", "VPR",
        "AutoDuty", "BossMod", "BossMod Reborn", "Wrath Combo", "XIV Combo", "XIV Combo Expanded", "XIVSlothCombo",
        "ReAction", "ReActionEX", "Redirect", "Olympus", "Reborn"
    };

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
        if (KeepAsIs.Contains(visible)) return true;
        if (ContainsJapanese(visible)) return true;
        if (source.Contains("##Up", StringComparison.Ordinal) || source.Contains("##Down", StringComparison.Ordinal) ||
            source.Contains("#####up", StringComparison.OrdinalIgnoreCase) || source.Contains("#####down", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("Rotation Solver Reborn Remove Territory", StringComparison.Ordinal)) return true;
        if (visible.StartsWith("RSR has helped you by clicking actions ", StringComparison.Ordinal) && visible.EndsWith(" times.", StringComparison.Ordinal)) return true;
        foreach (var (name, state) in config.Plugins) if (GetActiveStandardDictionary(name).ContainsKey(visible) || state.UserOverrides.ContainsKey(visible)) return true;
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
        => config.CleanSlateMode || !StandardDictionaries.TryGetValue(pluginName, out var dict)
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
        foreach (var state in config.Plugins.Values)
        {
            state.UserOverrides.Clear();
            state.Locations.Clear();
            state.Enabled = false;
        }
        config.Plugins["RSR"].Enabled = true;
        config.CleanSlateMode = true;
        config.CaptureSchemaVersion = 3;
        config.DataResetVersion = 24;
        rsrCurrentMenu = string.Empty;
        rsrCurrentSection = string.Empty;
        rsrPendingMenuCandidate = string.Empty;
        comboOpenDepth = 0;
        SaveConfig();
    }


    private void EnsureCaptureDictionaries()
    {
        foreach (var name in config.Plugins.Keys) EnsureCaptureDictionary(name);
    }

    private void EnsureCaptureDictionary(string name)
    {
        if (!pluginCaptured.ContainsKey(name)) pluginCaptured[name] = new ConcurrentDictionary<string, CapturedItem>(StringComparer.Ordinal);
    }

    private static int PluginSortKey(string name) => name switch { "RSR" => 0, "BMR" => 1, "BM" => 2, _ => 10 };

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
        foreach (var key in standard.Keys) keys.Add(key);

        var rows = new List<KeyValuePair<string, string>>(keys.Count);
        foreach (var key in keys)
        {
            if (IsTranslatedShadowKey(pluginName, key)) continue;
            var fallback = standard.TryGetValue(key, out var ja) ? ja : string.Empty;
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
            config.Plugins[key] = state;
        }

        state.TranslationTarget = true;
        state.Enabled = true;
        var keywords = new[] { plugin.Name?.Trim(), plugin.InternalName?.Trim() }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase);
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
            const string pluginName = "InventoryTools";

            if (!config.Plugins.TryGetValue(pluginName, out var state) || state == null)
            {
                state = new PluginDictionaryState
                {
                    Enabled = true,
                    TranslationTarget = true,
                    WindowKeyword = "Configuration",
                };
                config.Plugins[pluginName] = state;
                EnsureCaptureDictionary(pluginName);
            }
            else
            {
                state.Enabled = true;
                state.TranslationTarget = true;
            }

            var assemblyDir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? string.Empty;
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
            foreach (var kv in GetDictionaryCatalog(pluginName).OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                state.Locations.TryGetValue(kv.Key, out var loc);
                var ja = state.UserOverrides.TryGetValue(kv.Key, out var ov) ? ov : kv.Value;
                sw.WriteLine(string.Join(',', new[] { pluginName, loc?.Menu ?? string.Empty, loc?.Section ?? string.Empty, string.Empty, kv.Key, ja }.Select(CsvEscape)));
            }
            state.LastCsvPath = fullPath;
            SaveConfig();
            return $"CSVを書き出しました: {fullPath}";
        }
        catch (Exception ex) { return $"CSV書き出し失敗: {ex.Message}"; }
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
            var imported = 0;
            foreach (var cols in rows.Skip(1))
            {
                if (cols.Count < 6) continue;
                var english = cols[4];
                var japanese = cols[5];
                if (string.IsNullOrWhiteSpace(english)) continue;
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

    private void SaveConfig()
    {
        InvalidateDictionaryUiCache();
        pluginInterface.SavePluginConfig(config);
    }

    public void Dispose()
    {
        captureEnabled = false; baselineCaptureEnabled = false;
        pluginInterface.UiBuilder.Draw -= Draw; pluginInterface.UiBuilder.OpenConfigUi -= OpenUi; pluginInterface.UiBuilder.OpenMainUi -= OpenUi; commandManager.RemoveHandler(Command);
        beginMenuHook?.Dispose(); menuItemBoolPtrHook?.Dispose(); menuItemBoolHook?.Dispose(); beginTabItemHook?.Dispose(); drawListAddTextVec2Hook?.Dispose(); collapsingHeaderBoolPtrHook?.Dispose(); collapsingHeaderTreeNodeFlagsHook?.Dispose(); renderTextClippedHook?.Dispose(); renderTextWrappedHook?.Dispose(); renderTextHook?.Dispose(); bulletTextHook?.Dispose(); textWrappedHook?.Dispose(); treeNodeExStrHook?.Dispose(); treeNodeStrHook?.Dispose(); radioButtonIntPtrHook?.Dispose(); radioButtonBoolHook?.Dispose(); endHook?.Dispose(); beginHook?.Dispose(); separatorTextHook?.Dispose(); endComboHook?.Dispose(); beginComboHook?.Dispose(); comboFnStrPtrHook?.Dispose(); comboStrHook?.Dispose(); comboStrArrHook?.Dispose(); selectablePtrHook?.Dispose(); selectableHook?.Dispose(); buttonHook?.Dispose(); checkboxHook?.Dispose(); textHook?.Dispose();
    }

    private sealed record CapturedItem(string Text, string Kind, int Count, string Menu, string Section, string Window);
}

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
