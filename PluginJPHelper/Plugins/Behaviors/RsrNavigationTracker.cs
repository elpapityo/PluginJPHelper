namespace PluginJPHelper.Plugins.Behaviors;

using System.Runtime.InteropServices;
using Profiles;

/// <summary>
/// 未翻訳取得時に、RSR のどのメニュー・どの画面内項目を表示しているかを追う。
/// 状態は描画スレッド固有のため [ThreadStatic] を維持する。
/// </summary>
internal static unsafe class RsrNavigationTracker
{
    [ThreadStatic] private static string? currentMenu;
    [ThreadStatic] private static string? currentSection;
    [ThreadStatic] private static string? pendingMenuCandidate;

    public static string CurrentMenu => currentMenu ?? string.Empty;

    public static string CurrentSection => currentSection ?? string.Empty;

    public static void Reset()
    {
        currentMenu = string.Empty;
        currentSection = string.Empty;
        pendingMenuCandidate = string.Empty;
    }

    /// <summary>
    /// Selectable が selected で描画された時点の観測。
    /// currentWindowName は Plugin 側の CurrentWindowName をそのまま渡す。
    /// 本体ウィンドウ判定は元コードと同じく、ナビメニューでなかった場合にだけ行う。
    /// </summary>
    public static void Observe(byte* label, bool selected, bool captureEnabled, string capturePlugin, string? currentWindowName)
    {
        if (!selected || label == null || !captureEnabled) return;
        if (!RsrProfile.MatchesPluginName(capturePlugin)) return;

        string? raw;
        try { raw = Marshal.PtrToStringUTF8((nint)label); }
        catch { return; }
        if (string.IsNullOrWhiteSpace(raw)) return;

        var visible = VisibleLabel(raw);

        // RSRの左メニューは別の子ウィンドウで描画されるため、
        // CurrentWindowNameだけで判定すると取りこぼす。
        // ただし誤分類を防ぐため、RSRソースで確認済みのメニュー名かジョブ名だけを候補にする。
        if (RsrProfile.IsNavigationMenu(visible))
        {
            pendingMenuCandidate = visible;
            return;
        }

        // 画面内セクションはRSR本体ウィンドウ内でselectedになったものだけ採用する。
        if (IsRsrMainWindow(currentWindowName) && RsrProfile.IsKnownSection(visible))
            currentSection = visible;
    }

    // Plugin.IsTargetWindow("RSR", w) と同じ条件。
    //   空白なら false / それ以外は Trim してキーワード照合。
    // RSR は登録済みプロファイルなので設定の WindowKeyword は参照されない。
    private static bool IsRsrMainWindow(string? windowName)
        => !string.IsNullOrWhiteSpace(windowName) && RsrProfile.MatchesWindow(windowName.Trim());

    /// <summary>Selectable がクリックされた直後の観測。</summary>
    public static void ObserveAfterClick(string raw, bool captureEnabled, string capturePlugin)
    {
        if (string.IsNullOrWhiteSpace(raw) || !captureEnabled) return;
        if (!RsrProfile.MatchesPluginName(capturePlugin)) return;

        var visible = VisibleLabel(raw);
        if (!RsrProfile.IsNavigationMenu(visible)) return;

        if (!string.Equals(currentMenu, visible, StringComparison.Ordinal)) currentSection = string.Empty;
        pendingMenuCandidate = visible;
        currentMenu = visible;
    }

    /// <summary>
    /// RSR左メニューは直前に描画されるため、最後に見つけた selected なメニュー候補を、
    /// RSR本体の文字列を取得する瞬間に確定する。
    /// </summary>
    public static void CommitPendingMenu(string capturePlugin)
    {
        if (!RsrProfile.MatchesPluginName(capturePlugin)) return;
        if (string.IsNullOrWhiteSpace(pendingMenuCandidate)) return;

        if (!string.Equals(currentMenu, pendingMenuCandidate, StringComparison.Ordinal)) currentSection = string.Empty;
        currentMenu = pendingMenuCandidate;
    }

    private static string VisibleLabel(string source)
    {
        var marker = source.IndexOf("##", StringComparison.Ordinal);
        return marker > 0 ? source[..marker] : source;
    }
}
