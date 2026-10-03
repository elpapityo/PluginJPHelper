from pathlib import Path
import json
import re

root = Path('.')
p = root / 'PluginJPHelper' / 'Plugin.cs'
s = p.read_text(encoding='utf-8')


def once(old: str, new: str, label: str) -> None:
    global s
    if old not in s:
        raise SystemExit(f'missing source block: {label}')
    s = s.replace(old, new, 1)


once(
    '    private long communityLastAttemptTick;\n    private const long UpdateCheckIntervalMs = 3_600_000; // 起動時 + 1時間ごとに確認\n',
    '    private long communityLastAttemptTick;\n'
    '    // GitHub REST API のレート制限を公式／コミュニティ辞書で共有して抑止する。\n'
    '    // githubApiRateLimitResetUnixSeconds は「制限中の再送禁止時刻」。\n'
    '    // 表示用の残数／上限／通常リセット時刻は別フィールドで保持する。\n'
    '    private long githubApiRateLimitResetUnixSeconds;\n'
    '    private long githubApiRateLimitRemaining = -1;\n'
    '    private long githubApiRateLimitLimit = -1;\n'
    '    private long githubApiRateLimitDisplayResetUnixSeconds;\n'
    '    private const long GithubApiRateLimitFallbackSeconds = 300;\n'
    '    private const long UpdateCheckIntervalMs = 3_600_000; // 起動時 + 1時間ごとに確認\n',
    'rate fields')

s = s.replace('文字化けを検知 v0.5.0###PluginJPHelperMojibakeAlert', '文字化けを検知 v0.5.4###PluginJPHelperMojibakeAlert')
s = s.replace('const string baseTitle = "Plugin JP Helper v0.5.0";', 'const string baseTitle = "Plugin JP Helper v0.5.4";')
s = s.replace('PluginJPHelper/0.4.4', 'PluginJPHelper/0.5.4')

once(
    '    private string GetCommunityDictionaryState(CommunityDictionaryEntry item)\n'
    '    {\n'
    '        var path = CommunityCsvPath(item.FileName);\n'
    '        if (!File.Exists(path)) return "未取得";\n'
    '        var shaPath = CommunityShaPath(item.FileName);\n',
    '    private string GetCommunityDictionaryState(CommunityDictionaryEntry item)\n'
    '    {\n'
    '        var path = CommunityCsvPath(item.FileName);\n'
    '        if (!File.Exists(path)) return "未取得";\n'
    '        if (string.IsNullOrWhiteSpace(item.Sha)) return "導入済み";\n'
    '        var shaPath = CommunityShaPath(item.FileName);\n',
    'community state')

helpers = r'''    private void CaptureGitHubApiRateLimitHeaders(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remainingValues)
            && long.TryParse(remainingValues.FirstOrDefault(), out var remaining))
        {
            Interlocked.Exchange(ref githubApiRateLimitRemaining, remaining);
            if (remaining > 0)
                Interlocked.Exchange(ref githubApiRateLimitResetUnixSeconds, 0);
        }

        if (response.Headers.TryGetValues("X-RateLimit-Limit", out var limitValues)
            && long.TryParse(limitValues.FirstOrDefault(), out var limit))
            Interlocked.Exchange(ref githubApiRateLimitLimit, limit);

        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues)
            && long.TryParse(resetValues.FirstOrDefault(), out var resetUnix))
            Interlocked.Exchange(ref githubApiRateLimitDisplayResetUnixSeconds, resetUnix);
    }

    private string GetGitHubApiRateLimitStatusText()
    {
        var remaining = Interlocked.Read(ref githubApiRateLimitRemaining);
        var limit = Interlocked.Read(ref githubApiRateLimitLimit);
        var displayResetUnix = Interlocked.Read(ref githubApiRateLimitDisplayResetUnixSeconds);

        var countText = remaining >= 0 && limit > 0
            ? $"残り {remaining}/{limit}"
            : "残数 未取得";

        var resetText = string.Empty;
        if (displayResetUnix > DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            try
            {
                var resetLocal = DateTimeOffset.FromUnixTimeSeconds(displayResetUnix).ToLocalTime();
                resetText = $"・リセット {resetLocal:HH:mm}ごろ";
            }
            catch { }
        }

        if (TryGetGitHubApiRateLimitMessage(out _))
            return $"GitHub API（公式・コミュニティ共通）: {countText}{resetText}・レート制限中";

        return $"GitHub API（公式・コミュニティ共通）: {countText}{resetText}";
    }

    private void DrawGitHubApiRateLimitStatus()
    {
        var text = GetGitHubApiRateLimitStatusText();
        if (TryGetGitHubApiRateLimitMessage(out _))
            ImGui.TextColored(new Vector4(1.00f, 0.48f, 0.38f, 1.00f), text);
        else
            ImGui.TextDisabled(text);
    }

    private bool TryGetGitHubApiRateLimitMessage(out string message)
    {
        var resetUnix = Interlocked.Read(ref githubApiRateLimitResetUnixSeconds);
        if (resetUnix <= 0)
        {
            message = string.Empty;
            return false;
        }

        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (resetUnix <= nowUnix)
        {
            Interlocked.CompareExchange(ref githubApiRateLimitResetUnixSeconds, 0, resetUnix);
            message = string.Empty;
            return false;
        }

        var resetLocal = DateTimeOffset.FromUnixTimeSeconds(resetUnix).ToLocalTime();
        message = $"GitHub APIのレート制限中です。{resetLocal:HH:mm}ごろ再試行できます。";
        return true;
    }

    private bool TryRegisterGitHubApiRateLimit(HttpResponseMessage response, string responseBody, out string message)
    {
        CaptureGitHubApiRateLimitHeaders(response);
        message = string.Empty;
        var status = (int)response.StatusCode;
        if (status != 403 && status != 429) return false;

        long? remaining = null;
        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remainingValues)
            && long.TryParse(remainingValues.FirstOrDefault(), out var parsedRemaining))
            remaining = parsedRemaining;

        var rateLimitText = responseBody.Contains("rate limit", StringComparison.OrdinalIgnoreCase);
        if (status != 429 && remaining != 0 && !rateLimitText) return false;

        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var resetUnix = 0L;
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues))
            long.TryParse(resetValues.FirstOrDefault(), out resetUnix);
        if (resetUnix <= nowUnix)
            resetUnix = nowUnix + GithubApiRateLimitFallbackSeconds;

        Interlocked.Exchange(ref githubApiRateLimitResetUnixSeconds, resetUnix);
        Interlocked.Exchange(ref githubApiRateLimitDisplayResetUnixSeconds, resetUnix);
        Interlocked.Exchange(ref githubApiRateLimitRemaining, 0);
        Interlocked.Exchange(ref officialNoticeLastAttemptTick, 0);
        Interlocked.Exchange(ref communityLastAttemptTick, 0);

        var resetLocal = DateTimeOffset.FromUnixTimeSeconds(resetUnix).ToLocalTime();
        message = $"GitHub APIのレート制限に達しました。{resetLocal:HH:mm}ごろ再試行できます。解除時刻までは再取得を停止します。";
        return true;
    }

'''
marker = '    private void EnsureCommunityUpdateRefresh()\n'
if marker not in s:
    raise SystemExit('missing community helper marker')
s = s.replace(marker, helpers + marker, 1)

once(
    '    private void EnsureCommunityUpdateRefresh()\n'
    '    {\n'
    '        // 自動確認はDalamudのrepo読込と競合させない。\n'
    '        if (!pluginInstallerModule.IsSafeForBackgroundNetworkWork()) return;\n'
    '        var now = Environment.TickCount64;\n',
    '    private void EnsureCommunityUpdateRefresh()\n'
    '    {\n'
    '        // 自動確認はDalamudのrepo読込と競合させない。\n'
    '        if (!pluginInstallerModule.IsSafeForBackgroundNetworkWork()) return;\n'
    '        if (TryGetGitHubApiRateLimitMessage(out _)) return;\n'
    '        var now = Environment.TickCount64;\n',
    'community guard')

once(
    '    private Task RefreshCommunityDictionariesAsync(bool acknowledge)\n'
    '    {\n'
    '        if (communityDictionaryBusy) return Task.CompletedTask;\n',
    '    private Task RefreshCommunityDictionariesAsync(bool acknowledge)\n'
    '    {\n'
    '        if (TryGetGitHubApiRateLimitMessage(out var rateLimitMessage))\n'
    '        {\n'
    '            if (acknowledge) communityStatus = rateLimitMessage;\n'
    '            return Task.CompletedTask;\n'
    '        }\n'
    '        if (communityDictionaryBusy) return Task.CompletedTask;\n',
    'community precheck')

once(
    '                using var apiRes = officialDictionaryHttp.SendAsync(apiReq).GetAwaiter().GetResult();\n'
    '                apiRes.EnsureSuccessStatusCode();\n'
    '                var apiJson = apiRes.Content.ReadAsStringAsync().GetAwaiter().GetResult();\n'
    '                using var apiDoc = JsonDocument.Parse(apiJson);\n',
    '                using var apiRes = officialDictionaryHttp.SendAsync(apiReq).GetAwaiter().GetResult();\n'
    '                CaptureGitHubApiRateLimitHeaders(apiRes);\n'
    '                var apiJson = apiRes.Content.ReadAsStringAsync().GetAwaiter().GetResult();\n'
    '                if (!apiRes.IsSuccessStatusCode)\n'
    '                {\n'
    '                    if (TryRegisterGitHubApiRateLimit(apiRes, apiJson, out var rateMessage))\n'
    '                    {\n'
    '                        if (acknowledge) communityStatus = rateMessage;\n'
    '                        return;\n'
    '                    }\n'
    '                    apiRes.EnsureSuccessStatusCode();\n'
    '                }\n'
    '                using var apiDoc = JsonDocument.Parse(apiJson);\n',
    'community response')

once(
    '    private void EnsureOfficialNoticeRefresh()\n'
    '    {\n'
    '        // 自動確認はDalamudのrepo読込と競合させない。\n'
    '        if (!pluginInstallerModule.IsSafeForBackgroundNetworkWork()) return;\n'
    '        var now = Environment.TickCount64;\n',
    '    private void EnsureOfficialNoticeRefresh()\n'
    '    {\n'
    '        // 自動確認はDalamudのrepo読込と競合させない。\n'
    '        if (!pluginInstallerModule.IsSafeForBackgroundNetworkWork()) return;\n'
    '        if (TryGetGitHubApiRateLimitMessage(out _)) return;\n'
    '        var now = Environment.TickCount64;\n',
    'official notice guard')

once(
    '    private Task RefreshOfficialNoticeAsync()\n'
    '    {\n'
    '        if (officialNoticeBusy) return Task.CompletedTask;\n',
    '    private Task RefreshOfficialNoticeAsync()\n'
    '    {\n'
    '        if (TryGetGitHubApiRateLimitMessage(out _)) return Task.CompletedTask;\n'
    '        if (officialNoticeBusy) return Task.CompletedTask;\n',
    'official notice precheck')

once(
    '                using var dirRes = officialDictionaryHttp.SendAsync(dirReq).GetAwaiter().GetResult();\n'
    '                dirRes.EnsureSuccessStatusCode();\n'
    '                var dirJson = dirRes.Content.ReadAsStringAsync().GetAwaiter().GetResult();\n'
    '                using var dirDoc = JsonDocument.Parse(dirJson);\n',
    '                using var dirRes = officialDictionaryHttp.SendAsync(dirReq).GetAwaiter().GetResult();\n'
    '                CaptureGitHubApiRateLimitHeaders(dirRes);\n'
    '                var dirJson = dirRes.Content.ReadAsStringAsync().GetAwaiter().GetResult();\n'
    '                if (!dirRes.IsSuccessStatusCode)\n'
    '                {\n'
    '                    if (TryRegisterGitHubApiRateLimit(dirRes, dirJson, out _)) return;\n'
    '                    dirRes.EnsureSuccessStatusCode();\n'
    '                }\n'
    '                using var dirDoc = JsonDocument.Parse(dirJson);\n',
    'official notice response')

once(
    '    private Task RefreshOfficialDictionariesAsync()\n'
    '    {\n'
    '        if (officialDictionaryBusy) return Task.CompletedTask;\n',
    '    private Task RefreshOfficialDictionariesAsync()\n'
    '    {\n'
    '        if (TryGetGitHubApiRateLimitMessage(out var rateLimitMessage))\n'
    '        {\n'
    '            officialDictionaryStatus = rateLimitMessage;\n'
    '            return Task.CompletedTask;\n'
    '        }\n'
    '        if (officialDictionaryBusy) return Task.CompletedTask;\n',
    'official list precheck')

once(
    '                using var res = officialDictionaryHttp.SendAsync(req).GetAwaiter().GetResult();\n'
    '                res.EnsureSuccessStatusCode();\n'
    '                var json = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();\n'
    '                using var doc = JsonDocument.Parse(json);\n',
    '                using var res = officialDictionaryHttp.SendAsync(req).GetAwaiter().GetResult();\n'
    '                CaptureGitHubApiRateLimitHeaders(res);\n'
    '                var json = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();\n'
    '                if (!res.IsSuccessStatusCode)\n'
    '                {\n'
    '                    if (TryRegisterGitHubApiRateLimit(res, json, out var rateMessage))\n'
    '                    {\n'
    '                        officialDictionaryStatus = rateMessage;\n'
    '                        return;\n'
    '                    }\n'
    '                    res.EnsureSuccessStatusCode();\n'
    '                }\n'
    '                using var doc = JsonDocument.Parse(json);\n',
    'official list response')

once(
    '                lock (officialDictionaryLock)\n'
    '                    officialDictionaryEntries = list.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();\n'
    '                officialDictionaryStatus = $"公式辞書 {list.Count}件を確認しました。";\n'
    '                var noticeToAcknowledge = officialNoticeText?.Trim() ?? string.Empty;\n'
    '                var folderSignatureToAcknowledge = officialNoticeSha?.Trim() ?? string.Empty;\n',
    '                lock (officialDictionaryLock)\n'
    '                    officialDictionaryEntries = list.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();\n\n'
    '                var signatureParts = list.Select(x => $"{x.Name}:{x.Sha}")\n'
    '                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);\n'
    '                var signatureSource = string.Join("|", signatureParts);\n'
    '                var latestSignature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signatureSource)));\n'
    '                officialNoticeSha = latestSignature;\n\n'
    '                var noticeToAcknowledge = string.Empty;\n'
    '                try\n'
    '                {\n'
    '                    using var noticeReq = new HttpRequestMessage(HttpMethod.Get, OfficialNoticeUrl);\n'
    '                    noticeReq.Headers.UserAgent.ParseAdd("PluginJPHelper/0.5.4");\n'
    '                    using var noticeRes = officialDictionaryHttp.SendAsync(noticeReq).GetAwaiter().GetResult();\n'
    '                    if (noticeRes.IsSuccessStatusCode)\n'
    '                    {\n'
    '                        var noticeText = noticeRes.Content.ReadAsStringAsync().GetAwaiter().GetResult();\n'
    '                        noticeToAcknowledge = noticeText.Replace("\\r\\n", "\\n", StringComparison.Ordinal)\n'
    '                            .Replace(\'\\r\', \'\\n\')\n'
    '                            .Split(\'\\n\', StringSplitOptions.RemoveEmptyEntries)\n'
    '                            .FirstOrDefault()?.Trim() ?? string.Empty;\n'
    '                    }\n'
    '                }\n'
    '                catch (Exception ex)\n'
    '                {\n'
    '                    log.Debug(ex, "[PluginJPHelper] official notice text fetch failed");\n'
    '                }\n\n'
    '                officialDictionaryStatus = $"公式辞書 {list.Count}件を確認しました。";\n'
    '                var folderSignatureToAcknowledge = latestSignature;\n',
    'official signature reuse')

once(
    '    private string GetOfficialDictionaryState(OfficialDictionaryEntry item)\n'
    '    {\n'
    '        var path = OfficialCsvPath(item.Name);\n'
    '        if (!File.Exists(path)) return "未導入";\n'
    '        var shaPath = OfficialShaPath(item.Name);\n',
    '    private string GetOfficialDictionaryState(OfficialDictionaryEntry item)\n'
    '    {\n'
    '        var path = OfficialCsvPath(item.Name);\n'
    '        if (!File.Exists(path)) return "未導入";\n'
    '        if (string.IsNullOrWhiteSpace(item.Sha)) return "導入済み";\n'
    '        var shaPath = OfficialShaPath(item.Name);\n',
    'official state')

once(
    '        ImGui.TextWrapped("GitHubの公式CSV辞書を確認し、必要なものだけダウンロード／更新できます。使用方法は「ヘルプ」タブを確認してください。");\n'
    '        ImGui.Separator();\n'
    '        if (ActionButton("公式辞書一覧を取得", ButtonRole.Primary) && !officialDictionaryBusy)\n'
    '        {\n'
    '            _ = RefreshOfficialDictionariesAsync();\n'
    '            _ = RefreshOfficialNoticeAsync();\n'
    '        }\n',
    '        ImGui.TextWrapped("GitHubの公式CSV辞書を確認し、必要なものだけダウンロード／更新できます。使用方法は「ヘルプ」タブを確認してください。");\n'
    '        ImGui.Separator();\n'
    '        DrawGitHubApiRateLimitStatus();\n'
    '        ImGui.Spacing();\n'
    '        if (ActionButton("公式辞書一覧を取得", ButtonRole.Primary) && !officialDictionaryBusy)\n'
    '        {\n'
    '            _ = RefreshOfficialDictionariesAsync();\n'
    '        }\n',
    'official UI')

once(
    '        if (!string.IsNullOrWhiteSpace(communityStatus)) ImGui.TextWrapped(communityStatus);\n'
    '        if (!communityListLoaded)\n',
    '        if (!string.IsNullOrWhiteSpace(communityStatus)) ImGui.TextWrapped(communityStatus);\n'
    '        DrawGitHubApiRateLimitStatus();\n'
    '        if (!communityListLoaded)\n',
    'community UI')

p.write_text(s, encoding='utf-8')

(root / 'VERSION.txt').write_text('0.5.4\n', encoding='utf-8')

cp = root / 'PluginJPHelper' / 'PluginJPHelper.csproj'
c = cp.read_text(encoding='utf-8')
c = re.sub(r'<Version>[^<]+</Version>', '<Version>0.5.4</Version>', c)
c = re.sub(r'<AssemblyVersion>[^<]+</AssemblyVersion>', '<AssemblyVersion>0.5.4.0</AssemblyVersion>', c)
c = re.sub(r'<FileVersion>[^<]+</FileVersion>', '<FileVersion>0.5.4.0</FileVersion>', c)
cp.write_text(c, encoding='utf-8')

mp = root / 'PluginJPHelper' / 'PluginJPHelper.json'
m = json.loads(mp.read_text(encoding='utf-8-sig'))
m['AssemblyVersion'] = '0.5.4.0'
if 'IconUrl' in m:
    m['IconUrl'] = re.sub(r'\?v=[^"\s]+$', '?v=0.5.4', m['IconUrl'])
mp.write_text(json.dumps(m, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')

bp = root / 'build.bat'
b = bp.read_text(encoding='utf-8-sig')
b = re.sub(r'set "VERSION=[^"]+"', 'set "VERSION=0.5.4"', b)
bp.write_text(b, encoding='utf-8')

ch = root / 'CHANGELOG.md'
old = ch.read_text(encoding='utf-8-sig')
section = '''# CHANGELOG

## v0.5.4

### 追加・改善

- 公式辞書・コミュニティ辞書でGitHub REST APIの残回数とリセット時刻を表示。
- GitHub APIのレート制限到達時は解除時刻まで再取得を停止し、解除後は1時間待たず再確認できるよう改善。
- 公式辞書一覧の手動取得時、同じOfficial一覧APIへの二重アクセスをやめ、1回の応答を一覧と更新判定で共用。
- 公式辞書の `notice.txt` 取得失敗が、公式辞書一覧の取得成功を巻き込んで失敗扱いにならないよう改善。

---

'''
if not old.startswith('# CHANGELOG\n\n## v0.5.4'):
    if old.startswith('# CHANGELOG'):
        old = old.split('\n', 1)[1].lstrip('\n')
    ch.write_text(section + old, encoding='utf-8')

pm = root / 'pluginmaster.json'
data = json.loads(pm.read_text(encoding='utf-8-sig'))
entries = data if isinstance(data, list) else [data]
e = entries[0]
e['AssemblyVersion'] = '0.5.4.0'
url = 'https://github.com/elpapityo/PluginJPHelper/releases/download/V0.5.4/PluginJPHelper_v0.5.4.zip'
e['DownloadLinkInstall'] = url
e['DownloadLinkUpdate'] = url
e['DownloadLinkTesting'] = url
if 'IconUrl' in e:
    e['IconUrl'] = re.sub(r'\?v=[^"\s]+$', '?v=0.5.4', e['IconUrl'])
e['Changelog'] = 'v0.5.4: GitHub APIの残回数・リセット時刻表示、レート制限時の再送抑止、公式辞書一覧取得のAPIアクセス削減。'
pm.write_text(json.dumps(data, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')

stale = root / 'source-patch' / 'PluginJPHelper_v0.5.1.patch.gz.b64.part00'
if stale.exists():
    stale.unlink()

out = p.read_text(encoding='utf-8')
for required in [
    'Plugin JP Helper v0.5.4',
    '文字化けを検知 v0.5.4',
    'GitHub API（公式・コミュニティ共通）',
    'TryRegisterGitHubApiRateLimit',
    'DrawGitHubApiRateLimitStatus',
]:
    if required not in out:
        raise SystemExit(f'missing after promotion: {required}')
