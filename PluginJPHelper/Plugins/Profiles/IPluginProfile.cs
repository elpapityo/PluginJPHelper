namespace PluginJPHelper.Plugins.Profiles;

/// <summary>
/// ウィンドウの所有判定に参加するプラグインの契約。
/// ウィンドウ名にプラグイン名が現れない、別名を使うなど、
/// 設定の WindowKeyword だけでは表せないものをここに置く。
/// </summary>
internal interface IPluginProfile
{
    /// <summary>設定 (config.Plugins) のキーとして使う名前。</summary>
    string Name { get; }

    /// <summary>同じプロファイルを指す別のキー。無ければ空。</summary>
    IReadOnlyList<string> Aliases { get; }

    /// <summary>翻訳対象一覧の並び順。小さいほど先頭。</summary>
    int SortKey { get; }

    /// <summary>設定を新規作成するときの既定 WindowKeyword。</summary>
    string DefaultWindowKeyword { get; }

    /// <summary>このウィンドウがこのプラグインのものか。windowName は Trim 済みの原文。</summary>
    bool MatchesWindow(string windowName);
}
