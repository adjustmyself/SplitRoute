namespace SplitRoute;

/// <summary>守候執行緒發現的狀況。</summary>
internal enum GuardChange
{
    /// <summary>外網那張網卡不見了（USB 線拔掉、手機熱點關掉）。什麼都不用做，等它回來。</summary>
    ExternalOffline,

    /// <summary>閘道換了，或離線之後又回來了 —— 目前的路由已經不對，要重套。</summary>
    NeedsReapply,
}

/// <summary>
/// 套用之後留在背景看著網路。
///
/// USB 網路共用中途拔插、DHCP 過夜換一個閘道，是這個工具最常見的失效方式：
/// 路由還指著一個已經不存在的閘道，畫面上完全看不出來，使用者只會覺得
/// 「內網名稱查得到卻連不上」。反正程式本來就常駐系統匣，那就順便看著。
///
/// 只在<see cref="Settings.Applied"/>時動作 —— 使用者自己按過「清除路由」之後，
/// 我們記憶中的狀態不代表他要的狀態，這時候偷偷把路由加回去是最惹人厭的行為。
/// </summary>
internal sealed class RouteGuard : IDisposable
{
    /// <summary>
    /// 保險輪詢的間隔。真正的反應速度靠 <see cref="NetworkWatcher"/> 的事件（約兩秒），
    /// 這個值只是防止事件沒發出來，所以放得很寬，30 秒醒一次的成本可以忽略。
    /// </summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly Settings _cfg;

    /// <summary>從背景執行緒呼叫，回到 UI 執行緒是呼叫端的責任。</summary>
    private readonly Action<GuardChange, string> _report;

    private CancellationTokenSource? _cts;

    public RouteGuard(Settings cfg, Action<GuardChange, string> report)
    {
        _cfg = cfg;
        _report = report;
    }

    public bool Running => _cts != null;

    public void Start()
    {
        if (_cts != null) return;   // 已經在看了

        var cts = new CancellationTokenSource();
        _cts = cts;

        // 例外一律吃掉：守候是「額外的保險」，它自己壞掉不該把主程式一起帶走，
        // 更不該變成沒人接的 unobserved task exception。
        _ = Task.Run(async () =>
        {
            try { await LoopAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* 正常結束 */ }
            catch { /* 監看失效就當作沒開，使用者還是可以自己按套用 */ }
        }, cts.Token);
    }

    public void Stop()
    {
        CancellationTokenSource? cts = _cts;
        _cts = null;
        if (cts == null) return;

        try { cts.Cancel(); } catch (ObjectDisposedException) { /* 已經收掉了 */ }
        cts.Dispose();
    }

    public void Dispose() => Stop();

    // ------------------------------------------------------------------

    private async Task LoopAsync(CancellationToken ct)
    {
        using var watcher = new NetworkWatcher();

        // 起點是「剛套用完」的那組閘道，之後每次醒來都跟它比。
        string lastExternal = _cfg.ExternalInterface?.Gateway ?? "";
        string lastInternal = _cfg.InternalInterface?.Gateway ?? "";
        bool offline = false;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await watcher.WaitForChangeAsync(PollInterval, NetworkWatcher.DefaultDebounce, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }

            if (!_cfg.Applied) continue;

            RefreshOutcome found;
            try { found = RouteEngine.RefreshInterfaces(_cfg); }
            catch { continue; }   // 網卡正在換手時偶爾會讀不到，下一輪再說

            string external = _cfg.ExternalInterface?.Gateway ?? "";
            string internalGw = _cfg.InternalInterface?.Gateway ?? "";

            if (!found.ExternalFound || external.Length == 0)
            {
                // 只在剛離線那一刻講一次，不然拔著線過一夜會有一整晚的重複訊息
                if (!offline)
                {
                    offline = true;
                    Report(GuardChange.ExternalOffline,
                        $"外網介面「{_cfg.ExternalInterface?.Name}」離線了，等它回來會自動重套路由。");
                }
                continue;
            }

            // 內網線也拔掉的話一樣等，但這條是實體網路線，通常不是常態，不必通報
            if (!found.InternalFound || internalGw.Length == 0) continue;

            if (offline)
            {
                offline = false;
                lastExternal = external;
                lastInternal = internalGw;

                // 網卡斷線時它身上的路由會一起被 Windows 移掉，所以就算閘道跟斷線前
                // 一模一樣也還是要重套。
                Report(GuardChange.NeedsReapply, "外網介面回來了，重新套用路由。");
                continue;
            }

            if (external != lastExternal || internalGw != lastInternal)
            {
                string what = external != lastExternal
                    ? $"外網閘道從 {Show(lastExternal)} 變成 {external}"
                    : $"內網閘道從 {Show(lastInternal)} 變成 {internalGw}";
                lastExternal = external;
                lastInternal = internalGw;

                Report(GuardChange.NeedsReapply, what + "，重新套用路由。");
            }
        }
    }

    private static string Show(string gw) => gw.Length == 0 ? "（無）" : gw;

    private void Report(GuardChange change, string message)
    {
        try { _report(change, message); }
        catch { /* 視窗正在關，通知不到就算了 */ }
    }
}
