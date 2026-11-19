using System.Text.Json;

namespace SplitRoute;

/// <summary>
/// 套用／清除過程中的一個步驟，直接對應日誌裡的一行。
///
/// 這裡刻意沒有「從 ExecResult 直接轉過來」的建構捷徑：route.exe 不管成敗都回傳
/// exit code 0，照著 <see cref="ExecResult.Ok"/> 抄就會把「路由新增失敗: 物件已經存在。」
/// 記成綠色的 ✓。成敗要由 RouteEngine 看輸出內容判斷（見 Classify）。
/// </summary>
internal readonly record struct StepResult(string Label, bool Ok, string Output);

/// <summary>設定不完整（缺介面、缺閘道、沒有內網範圍）時丟這個，畫面直接顯示訊息即可。</summary>
internal sealed class RouteConfigException(string message) : Exception(message);

internal readonly record struct RefreshOutcome(bool ExternalFound, bool InternalFound);

/// <summary>一個 namespace 的「已設定」與「生效中」比對結果。</summary>
internal enum NrptState
{
    /// <summary>已設定，DNS Client 也載入了同一組 DNS 伺服器。</summary>
    Effective,
    /// <summary>登錄檔裡有，DNS Client 還沒載入（或被群組原則的 NRPT 整組蓋掉）。</summary>
    NotLoaded,
    /// <summary>生效中，但不是本機設定的，多半來自群組原則或 DirectAccess。</summary>
    NotLocal,
    /// <summary>兩邊都有，但 DNS 伺服器不一樣 —— 改過設定、服務還沒重新載入。</summary>
    Mismatch,
}

internal sealed record NrptEntry(string Namespace, List<string> Configured, List<string> Effective, NrptState State);

internal sealed record NrptStatus(List<NrptEntry> Entries, List<string> Errors);

internal static class RouteEngine
{
    // ------------------------------------------------------------------
    // 介面資訊更新
    // ------------------------------------------------------------------

    /// <summary>
    /// 用目前實際的網卡資料，更新設定檔裡記住的 index / IP / 閘道。
    ///
    /// USB 網路共用和 DHCP 每次重連給的閘道都不一樣，設定檔裡存的值一過夜就過期了，
    /// 所以每次套用之前都要重讀一次。
    ///
    /// 但偵測到的閘道只有在「on-link」時才採用：Windows 會把我們自己加的預設路由
    /// 算回某張網卡頭上，於是那張網卡會回報一個它根本走不到的閘道。照單全收的話，
    /// 等於拿一個不通的位址覆蓋掉原本好好的設定 —— 這種時候寧可留著舊值。
    /// </summary>
    public static RefreshOutcome RefreshInterfaces(Settings cfg)
    {
        List<NetAdapter> live;
        try { live = NetInfo.List(); }
        catch { return new RefreshOutcome(false, false); }

        return new RefreshOutcome(
            RefreshOne(cfg.ExternalInterface, live),
            RefreshOne(cfg.InternalInterface, live));
    }

    private static bool RefreshOne(NetEndpointRef? saved, List<NetAdapter> live)
    {
        if (saved == null) return false;

        NetAdapter? found = live.FirstOrDefault(a => a.Index == saved.Index)
                            ?? live.FirstOrDefault(a => a.Name == saved.Name);
        if (found == null) return false;

        saved.Index = found.Index;
        saved.Name = found.Name;
        saved.Ip = found.Ip;
        saved.PrefixLength = found.PrefixLength;
        if (found.GatewayOnLink) saved.Gateway = found.Gateway;

        return true;
    }

    // ------------------------------------------------------------------
    // 套用
    // ------------------------------------------------------------------

    /// <summary>
    /// 套用全部設定：內網範圍走內網閘道、預設路由走外網閘道，再設 DNS 與 NRPT。
    /// 每完成一步就丟給 <paramref name="log"/>，畫面才不會整段等到最後才動。
    /// </summary>
    public static async Task<List<StepResult>> ApplyAsync(
        Settings cfg, IProgress<StepResult>? log = null, CancellationToken ct = default)
    {
        Validate(cfg);

        string intGw = cfg.InternalInterface!.Gateway;
        string extGw = cfg.ExternalInterface!.Gateway;
        int intIf = cfg.InternalInterface.Index;
        int extIf = cfg.ExternalInterface.Index;
        var results = new List<StepResult>();

        void Add(StepResult r) { results.Add(r); log?.Report(r); }

        // 內網範圍 → 內網閘道。metric 1 讓它穩穩壓過預設路由。
        foreach (string range in cfg.InternalRanges)
        {
            ct.ThrowIfCancellationRequested();

            if (!NetInfo.TryParseCidr(range, out string dest, out string mask))
            {
                Add(new StepResult($"[路由] 略過格式錯誤的範圍 {range}", false, "不是有效的 CIDR"));
                continue;
            }

            Add(await AddRouteAsync(dest, mask, intGw, "1", intIf).ConfigureAwait(false));
        }

        // 其餘流量 → 外網閘道
        ct.ThrowIfCancellationRequested();
        Add(await AddRouteAsync("0.0.0.0", "0.0.0.0", extGw, "5", extIf).ConfigureAwait(false));

        // 擋掉內網預設路由、DNS、NRPT 一次做完
        // （powershell.exe 啟動要花將近一秒，不要一件事開一次）
        ct.ThrowIfCancellationRequested();
        foreach (StepResult r in await ApplySystemAsync(cfg, ValidDomains(cfg), ct).ConfigureAwait(false))
            Add(r);

        return results;
    }

    /// <summary>
    /// 路由以外的部分：讓內網網卡不接受預設路由，再設 DNS 與 NRPT。
    ///
    /// <b>為什麼要擋內網的預設路由</b>：光是加一條 metric 比較小的外網預設路由，只是「優先」
    /// 走外網，內網 DHCP 發的那條預設路由還在。USB 一拔，我們那條跟著網卡消失，
    /// 外網流量就悄悄改從內網出去了 —— 外網應該只能走外網網卡。
    ///
    /// 只用 route delete 刪掉不夠：DHCP 續約會再加回來。<c>IgnoreDefaultRoutes</c>
    /// 是介面設定，叫它忽略 DHCP／RA 發的預設路由，IPv4、IPv6 都要設（內網有 IPv6 RA 的話
    /// 會從 IPv6 漏出去）。這個設定刻意是<b>持久</b>的：開機到登入還原之間還有一段空窗
    /// （排程延遲 20 秒、還要等手機插上），不持久的話這段時間就漏了。代價是一定要由
    /// 「清除路由」改回來（<see cref="ClearSystemAsync"/>）。
    ///
    /// 只刪 ActiveStore 的路由：PersistentStore 裡的是手動設的固定閘道，刪了等於改掉使用者的
    /// 網卡設定，清除時也沒辦法還原。
    /// </summary>
    private static async Task<List<StepResult>> ApplySystemAsync(
        Settings cfg, List<DomainRule> domains, CancellationToken ct)
    {
        string plan = JsonSerializer.Serialize(new
        {
            interfaceIndex = cfg.InternalInterface!.Index,
            dnsServers = cfg.InternalDnsServers,
            domains = domains.Select(d => new
            {
                ns = NetInfo.NormalizeNamespace(d.Namespace),
                servers = d.DnsServers,
            }).ToList(),
        });

        const string script = """
            $ErrorActionPreference = 'Stop'
            [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
            $plan = $env:SR_PLAN | ConvertFrom-Json
            $idx = [int]$plan.interfaceIndex

            foreach ($af in 'IPv4', 'IPv6') {
              $prefix = if ($af -eq 'IPv4') { '0.0.0.0/0' } else { '::/0' }
              try {
                $ipif = Get-NetIPInterface -InterfaceIndex $idx -AddressFamily $af -ErrorAction SilentlyContinue
                if (-not $ipif) { continue }   # 這張網卡沒開這個協定，沒有東西可以漏
                if ($ipif.IgnoreDefaultRoutes -ne 'Enabled') {
                  Set-NetIPInterface -InterfaceIndex $idx -AddressFamily $af -IgnoreDefaultRoutes Enabled
                }
                $routes = @(Get-NetRoute -InterfaceIndex $idx -AddressFamily $af -DestinationPrefix $prefix `
                              -PolicyStore ActiveStore -ErrorAction SilentlyContinue)
                if ($routes.Count -gt 0) { $routes | Remove-NetRoute -Confirm:$false }
                $note = if ($routes.Count -gt 0) { "，移除 $($routes.Count) 條" } else { '' }
                Write-Output "OK|[外網] 內網介面 $af 不接受預設路由，外網只走外網介面$note"
              } catch {
                Write-Output "ERR|[外網] 擋掉內網介面 $af 預設路由失敗: $($_.Exception.Message)"
              }
            }

            if ($plan.dnsServers -and @($plan.dnsServers).Count -gt 0) {
              $servers = [string[]]@($plan.dnsServers)
              try {
                Set-DnsClientServerAddress -InterfaceIndex $idx -ServerAddresses $servers
                Write-Output "OK|[DNS] 內網介面 DNS -> $($servers -join ', ')"
              } catch {
                Write-Output "ERR|[DNS] 設定內網 DNS 失敗: $($_.Exception.Message)"
              }
            }

            foreach ($d in @($plan.domains)) {
              $servers = [string[]]@($d.servers)
              try {
                # 先清掉同 namespace 的舊規則，否則同一個 domain 會累積出一堆重複規則
                Get-DnsClientNrptRule |
                  Where-Object { $_.Namespace -eq $d.ns } |
                  Remove-DnsClientNrptRule -Force -ErrorAction SilentlyContinue
                Add-DnsClientNrptRule -Namespace $d.ns -NameServers $servers
                Write-Output "OK|[NRPT] $($d.ns) -> $($servers -join ', ')"
              } catch {
                Write-Output "ERR|[NRPT] $($d.ns) 失敗: $($_.Exception.Message)"
              }
            }
            """;

        ExecResult r = await ProcessRunner.PowerShellAsync(
            script, new Dictionary<string, string> { ["SR_PLAN"] = plan }, ct).ConfigureAwait(false);

        return ParseScriptOutput(r, "[DNS/NRPT] 設定失敗");
    }

    // ------------------------------------------------------------------
    // 清除
    // ------------------------------------------------------------------

    public static async Task<List<StepResult>> ClearAsync(
        Settings cfg, IProgress<StepResult>? log = null, CancellationToken ct = default)
    {
        var results = new List<StepResult>();
        void Add(StepResult r) { results.Add(r); log?.Report(r); }

        string? intGw = cfg.InternalInterface?.Gateway;

        foreach (string range in cfg.InternalRanges)
        {
            ct.ThrowIfCancellationRequested();
            if (!NetInfo.TryParseCidr(range, out string dest, out string mask)) continue;

            // 先連閘道一起指定，只刪掉我們加的那一條；閘道自從套用之後換過的話
            // 會刪不到，這時才退回「不指定閘道」把該網段的路由整個清掉。
            ExecResult r = string.IsNullOrEmpty(intGw)
                ? await ProcessRunner.RouteAsync("delete", dest, "mask", mask).ConfigureAwait(false)
                : await ProcessRunner.RouteAsync("delete", dest, "mask", mask, intGw).ConfigureAwait(false);

            if (Classify(r) != RouteOutcome.Ok && !string.IsNullOrEmpty(intGw))
                r = await ProcessRunner.RouteAsync("delete", dest, "mask", mask).ConfigureAwait(false);

            Add(DeleteResult($"route delete {dest} mask {mask}", r));
        }

        string? extGw = cfg.ExternalInterface?.Gateway;
        if (!string.IsNullOrEmpty(extGw))
        {
            ct.ThrowIfCancellationRequested();
            ExecResult r = await ProcessRunner.RouteAsync(
                "delete", "0.0.0.0", "mask", "0.0.0.0", extGw).ConfigureAwait(false);
            Add(DeleteResult($"route delete 0.0.0.0 mask 0.0.0.0 {extGw}", r));
        }

        // 內網預設路由、DNS、NRPT 一起復原
        bool resetDns = cfg.InternalInterface != null && cfg.InternalDnsServers.Count > 0;
        List<string> namespaces = cfg.InternalDomains
            .Select(d => NetInfo.NormalizeNamespace(d.Namespace))
            .Where(s => s.Length > 0)
            .Distinct()
            .ToList();

        if (cfg.InternalInterface != null || namespaces.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            foreach (StepResult r in await ClearSystemAsync(cfg, resetDns, namespaces, ct).ConfigureAwait(false))
                Add(r);
        }

        // 自我檢查：`route delete 0.0.0.0 mask 0.0.0.0 <gw>` 會把「所有」指向該閘道的
        // 預設路由刪掉，包含 DHCP 原本發下來的那一條。刪完整台機器可能就沒有對外路由了，
        // 而且從畫面上完全看不出來，所以這裡明講並指路。
        if (!NetInfo.HasDefaultRoute())
        {
            Add(new StepResult(
                "[檢查] 目前沒有任何預設路由，對外連線會不通",
                false,
                "請用「工具 → 重新取得外網 IP」向 DHCP 要回預設路由，或重新插拔外網那張網卡。"));
        }

        return results;
    }

    /// <summary>
    /// <see cref="ApplySystemAsync"/> 的反向：內網網卡恢復接受預設路由，DNS 改回自動，移除 NRPT。
    ///
    /// 把 IgnoreDefaultRoutes 關掉，被擋掉的預設路由不會自己回來，要等 DHCP 下次續約，
    /// 可能是好幾個小時之後。所以 IPv4 沒有預設路由時要主動補：DHCP 的網卡跑
    /// <c>ipconfig /renew</c>；固定 IP 的網卡，閘道還在 PersistentStore 裡，照著抄回 ActiveStore。
    /// IPv6 的 RA 幾分鐘內就會再來，不用管。
    /// </summary>
    private static async Task<List<StepResult>> ClearSystemAsync(
        Settings cfg, bool resetDns, List<string> namespaces, CancellationToken ct)
    {
        string plan = JsonSerializer.Serialize(new
        {
            interfaceIndex = cfg.InternalInterface?.Index ?? 0,
            restoreDefaultRoute = cfg.InternalInterface != null,
            resetDns,
            namespaces,
        });

        const string script = """
            $ErrorActionPreference = 'Stop'
            [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
            $plan = $env:SR_PLAN | ConvertFrom-Json
            $idx = [int]$plan.interfaceIndex

            if ($plan.restoreDefaultRoute) {
              foreach ($af in 'IPv4', 'IPv6') {
                try {
                  $ipif = Get-NetIPInterface -InterfaceIndex $idx -AddressFamily $af -ErrorAction SilentlyContinue
                  if (-not $ipif -or $ipif.IgnoreDefaultRoutes -ne 'Enabled') { continue }
                  Set-NetIPInterface -InterfaceIndex $idx -AddressFamily $af -IgnoreDefaultRoutes Disabled
                  Write-Output "OK|[外網] 內網介面 $af 恢復接受預設路由"
                } catch {
                  Write-Output "ERR|[外網] 內網介面 $af 恢復接受預設路由失敗: $($_.Exception.Message)"
                }
              }

              try {
                $ipif = Get-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -ErrorAction SilentlyContinue
                $hasDefault = {
                  @(Get-NetRoute -InterfaceIndex $idx -DestinationPrefix '0.0.0.0/0' -PolicyStore ActiveStore `
                      -ErrorAction SilentlyContinue).Count -gt 0
                }
                if ($ipif -and -not (& $hasDefault)) {
                  if ($ipif.Dhcp -eq 'Enabled') {
                    # ipconfig 往 stderr 寫字時，Stop 模式會把它當成例外；成敗反正看下面有沒有預設路由
                    try { $null = & (Join-Path $env:SystemRoot 'System32\ipconfig.exe') /renew $ipif.InterfaceAlias 2>&1 } catch { }
                  } else {
                    Get-NetRoute -InterfaceIndex $idx -DestinationPrefix '0.0.0.0/0' -PolicyStore PersistentStore `
                        -ErrorAction SilentlyContinue |
                      ForEach-Object {
                        New-NetRoute -InterfaceIndex $idx -DestinationPrefix '0.0.0.0/0' -NextHop $_.NextHop `
                          -RouteMetric $_.RouteMetric -PolicyStore ActiveStore | Out-Null
                      }
                  }
                  if (& $hasDefault) {
                    Write-Output "OK|[外網] 內網介面拿回預設路由了"
                  } else {
                    Write-Output "ERR|[外網] 內網介面沒有拿回預設路由，要從內網上外網的話請把網路線重新插拔一次"
                  }
                }
              } catch {
                Write-Output "ERR|[外網] 補回內網預設路由失敗: $($_.Exception.Message)"
              }
            }

            if ($plan.resetDns) {
              try {
                Set-DnsClientServerAddress -InterfaceIndex $idx -ResetServerAddresses
                Write-Output "OK|[DNS] 內網介面恢復自動取得 DNS"
              } catch {
                Write-Output "ERR|[DNS] 恢復自動取得 DNS 失敗: $($_.Exception.Message)"
              }
            }

            foreach ($ns in @($plan.namespaces)) {
              try {
                $hit = @(Get-DnsClientNrptRule | Where-Object { $_.Namespace -eq $ns })
                if ($hit.Count -gt 0) {
                  $hit | Remove-DnsClientNrptRule -Force
                  Write-Output "OK|[NRPT] 移除 $ns（$($hit.Count) 條）"
                } else {
                  Write-Output "OK|[NRPT] $ns 本來就沒有規則"
                }
              } catch {
                Write-Output "ERR|[NRPT] 移除 $ns 失敗: $($_.Exception.Message)"
              }
            }
            """;

        ExecResult r = await ProcessRunner.PowerShellAsync(
            script, new Dictionary<string, string> { ["SR_PLAN"] = plan }, ct).ConfigureAwait(false);

        return ParseScriptOutput(r, "[DNS/NRPT] 復原失敗");
    }

    // ------------------------------------------------------------------
    // 查詢
    // ------------------------------------------------------------------

    public static Task<string> RouteTableAsync() => ProcessRunner.RoutePrintAsync();

    /// <summary>
    /// 把「已設定」和「生效中」的 NRPT 規則並排比對，用來核對設定有沒有真的套上去。
    ///
    /// 這是兩層不同的東西：<c>Get-DnsClientNrptRule</c> 讀的是登錄檔裡寫了什麼，
    /// <c>Get-DnsClientNrptPolicy</c> 才是 DNS Client 服務（Dnscache）已經載入、真的在用的。
    /// 規則一加就進登錄檔，但服務是自己找時機重新載入的，中間會有落差；
    /// 另外只要群組原則下發了任何 NRPT，本機規則會整組被忽略。只看前者的話，
    /// 「設了卻沒生效」這件事在畫面上完全看不出來 —— 而那正是使用者打開這個畫面要查的。
    /// </summary>
    public static async Task<NrptStatus> NrptStatusAsync(CancellationToken ct = default)
    {
        const string script = """
            [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
            try {
              Get-DnsClientNrptRule -ErrorAction Stop | ForEach-Object {
                $servers = @($_.NameServers) -join ','
                foreach ($ns in @($_.Namespace)) { Write-Output "RULE|$ns|$servers" }
              }
            } catch {
              Write-Output "ERR|讀取已設定的 NRPT 規則失敗: $($_.Exception.Message)"
            }
            try {
              Get-DnsClientNrptPolicy -ErrorAction Stop | ForEach-Object {
                $servers = @($_.NameServers) -join ','
                foreach ($ns in @($_.Namespace)) { Write-Output "POLICY|$ns|$servers" }
              }
            } catch {
              Write-Output "ERR|讀取生效中的 NRPT 原則失敗: $($_.Exception.Message)"
            }
            """;

        ExecResult r = await ProcessRunner.PowerShellAsync(script, null, ct).ConfigureAwait(false);

        var configured = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        var effective = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        foreach (string line in SplitLines(r.Output))
        {
            string[] parts = line.Split('|', 3);
            Dictionary<string, SortedSet<string>>? target = parts[0] switch
            {
                "RULE" => configured,
                "POLICY" => effective,
                _ => null,
            };

            if (parts[0] == "ERR" && parts.Length > 1) { errors.Add(line[4..]); continue; }
            if (target == null || parts.Length < 3 || parts[1].Length == 0)
            {
                if (!r.Ok) errors.Add(line);
                continue;
            }

            if (!target.TryGetValue(parts[1], out SortedSet<string>? set))
                target[parts[1]] = set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string s in parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                set.Add(s);
        }

        if (!r.Ok && errors.Count == 0)
            errors.Add(string.IsNullOrWhiteSpace(r.Output) ? "PowerShell 執行失敗" : r.Output.Trim());

        var entries = configured.Keys
            .Union(effective.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(ns => ns.TrimStart('.'), StringComparer.OrdinalIgnoreCase)
            .Select(ns =>
            {
                configured.TryGetValue(ns, out SortedSet<string>? c);
                effective.TryGetValue(ns, out SortedSet<string>? e);
                NrptState state = (c, e) switch
                {
                    (not null, null) => NrptState.NotLoaded,
                    (null, not null) => NrptState.NotLocal,
                    _ when c!.SetEquals(e!) => NrptState.Effective,
                    _ => NrptState.Mismatch,
                };
                return new NrptEntry(ns, c?.ToList() ?? [], e?.ToList() ?? [], state);
            })
            .ToList();

        return new NrptStatus(entries, errors);
    }

    // ------------------------------------------------------------------
    // 開機還原
    // ------------------------------------------------------------------

    /// <summary>
    /// 兩張網卡都在、也都有可用的閘道 —— 這是套用路由的最低條件。
    /// 順便把設定裡記住的 index／IP／閘道更新成現在的實際值。
    /// </summary>
    public static bool IsReady(Settings cfg)
    {
        RefreshOutcome found = RefreshInterfaces(cfg);
        return found.ExternalFound && found.InternalFound
               && !string.IsNullOrEmpty(cfg.ExternalInterface?.Gateway)
               && !string.IsNullOrEmpty(cfg.InternalInterface?.Gateway);
    }

    /// <summary>
    /// 等到網路就緒為止。
    ///
    /// 舊版是「最多重試 N 次、每次隔 M 秒」，但這件事沒有猜得準的秒數：USB 網路共用
    /// 可能要等使用者解鎖手機、插上線，十秒或三分鐘都正常。所以改成聽網路變化事件
    /// （<see cref="NetworkWatcher"/>，網卡一上線兩秒內就醒），慢速輪詢只當保險，
    /// 而且預設<b>不設放棄時限</b> —— 程式本來就常駐系統匣，等下去不花什麼成本，
    /// 半途放棄換來的卻是「DNS 與 NRPT 在、路由不在」那種最難查的半套狀態。
    ///
    /// 等待期間刻意不寫逐次的日誌：等三分鐘就是三十幾行沒有新資訊的重複。
    /// </summary>
    public static async Task<bool> WaitForReadyAsync(
        Settings cfg, IProgress<StepResult>? log = null, CancellationToken ct = default)
    {
        if (IsReady(cfg)) return true;

        TimeSpan poll = TimeSpan.FromSeconds(Math.Clamp(
            cfg.RestoreRetryDelaySeconds,
            Settings.MinRestoreRetryDelaySeconds,
            Settings.MaxRestoreRetryDelaySeconds));

        int maxChecks = cfg.RestoreWaitForever
            ? int.MaxValue
            : Math.Clamp(
                cfg.RestoreMaxAttempts, Settings.MinRestoreMaxAttempts, Settings.MaxRestoreMaxAttempts);

        log?.Report(new StepResult(
            cfg.RestoreWaitForever
                ? "[等待] 網路還沒就緒。網卡一上線就會自動套用，不必守在這裡；也可以隨時自己按「套用路由」。"
                : $"[等待] 網路還沒就緒，最多再等 {Describe(poll * (maxChecks - 1))}。",
            true, ""));

        using var watcher = new NetworkWatcher();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan nextNotice = NoticeInterval;

        for (int check = 1; check < maxChecks; check++)
        {
            await watcher.WaitForChangeAsync(poll, NetworkWatcher.DefaultDebounce, ct).ConfigureAwait(false);

            if (IsReady(cfg))
            {
                log?.Report(new StepResult($"[等待] 網路就緒（等了 {Describe(elapsed.Elapsed)}）", true, ""));
                return true;
            }

            // 久等的時候偶爾報一次平安，讓人知道程式沒有卡住，但不要變成洗版
            if (elapsed.Elapsed >= nextNotice)
            {
                log?.Report(new StepResult(
                    $"[等待] 還在等網路（已經等了 {Describe(elapsed.Elapsed)}）", true, ""));
                nextNotice += NoticeInterval;
            }
        }

        return false;
    }

    private static readonly TimeSpan NoticeInterval = TimeSpan.FromMinutes(2);

    private static string Describe(TimeSpan t)
    {
        int seconds = Math.Max(0, (int)Math.Round(t.TotalSeconds));
        return seconds < 60 ? $"{seconds} 秒" : $"{seconds / 60} 分 {seconds % 60} 秒";
    }

    /// <summary>
    /// 登入後自動把路由套回去。
    ///
    /// route add 沒有加 -p，重開機路由就沒了；但 DNS 與 NRPT 是寫進登錄檔的，重開機還在。
    /// 兩者一不同步，內網名稱就解析得到卻連不上，症狀很難懂 —— 所以上次狀態是「已套用」
    /// 時要自己補回來。
    /// </summary>
    public static async Task<bool> RestoreAsync(
        Settings cfg, IProgress<StepResult>? log = null, CancellationToken ct = default)
    {
        // 呼叫端（MainForm）為了讓等待期間視窗還能操作，通常已經自己先等過一輪，
        // 所以這裡多半立刻就回 true。留著這一步是為了讓這個方法自己成立：
        // 不會有人繞過等待，在只有一半網卡就緒的狀態下把路由套下去。
        if (!await WaitForReadyAsync(cfg, log, ct).ConfigureAwait(false))
        {
            log?.Report(new StepResult("[還原] 等不到可用的網路介面，請開啟視窗手動套用", false, ""));
            return false;
        }

        try
        {
            List<StepResult> results = await ApplyAsync(cfg, log, ct).ConfigureAwait(false);
            cfg.Save();   // 把這次更新到的閘道存下來
            int failed = results.Count(r => !r.Ok);
            log?.Report(new StepResult(
                $"[還原] 完成：{results.Count} 項，失敗 {failed} 項", failed == 0, ""));
            return failed == 0;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log?.Report(new StepResult("[還原] 失敗", false, ex.Message));
            return false;
        }
    }

    // ------------------------------------------------------------------
    // route.exe 的成敗判斷
    // ------------------------------------------------------------------

    /// <summary>route.exe 一次呼叫實際發生了什麼事。</summary>
    private enum RouteOutcome { Ok, AlreadyExists, NotFound, Failed }

    /// <summary>
    /// route.exe **不管成功失敗都回傳 exit code 0**，成敗只寫在輸出文字裡
    /// （而且是系統 OEM 字碼頁的中文）。只看 ExitCode 的話，
    /// 「路由新增失敗: 物件已經存在。」會被記成一個綠色的 ✓ —— 日誌就開始說謊了。
    ///
    /// 中英文都比對：機器換成英文版 Windows 時訊息會整組變成英文。
    /// 順序不能調：失敗訊息本身就含「失敗／failed」，所以要先認出
    /// 「已經存在」和「找不到元素」這兩種有意義的情況，剩下的才算真的失敗。
    /// </summary>
    private static RouteOutcome Classify(ExecResult r)
    {
        if (!r.Ok) return RouteOutcome.Failed;   // 行程本身沒跑起來

        string t = r.Output;
        if (t.Length == 0) return RouteOutcome.Ok;

        if (Has(t, "物件已經存在", "object already exists")) return RouteOutcome.AlreadyExists;
        if (Has(t, "找不到元素", "element not found")) return RouteOutcome.NotFound;
        if (Has(t, "失敗", "failed", "拒絕存取", "access is denied", "需要提高的權限", "requires elevation"))
            return RouteOutcome.Failed;

        return RouteOutcome.Ok;
    }

    private static bool Has(string text, params string[] needles)
        => needles.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 刪除的結果。「本來就沒有這條路由」不是失敗 —— 重開機之後路由本來就不在了，
    /// 這時候按清除會整排紅色的 ✗，看起來像出了什麼事，其實結果完全正確。
    /// </summary>
    private static StepResult DeleteResult(string label, ExecResult r) => Classify(r) switch
    {
        RouteOutcome.Ok => new StepResult(label, true, ""),
        RouteOutcome.NotFound => new StepResult(label + "（本來就沒有這條路由）", true, ""),
        _ => new StepResult(label, false, r.Output),
    };

    /// <summary>
    /// 加一條路由，而且要能重複執行。
    ///
    /// 守候模式重新套用、使用者連按兩次套用、或 DHCP 本來就發了一條同目的地的路由時，
    /// <c>route add</c> 會回「物件已經存在」。這時候不能就這樣算了：路由是在，但 metric
    /// 不一定是我們要的（DHCP 發的預設路由 metric 通常比我們大得多），
    /// 留著它等於我們指定的優先順序沒生效 —— 所以改用 <c>route change</c> 把它調成我們要的值。
    ///
    /// change 這一步要指定 <c>IF</c>：機器上可能有不只一條同目的地的路由（最典型的就是
    /// 兩張網卡各自從 DHCP 拿到的預設路由），不指定介面等於讓 route.exe 自己挑一條改，
    /// 有機會改到不該動的那一條。指定了就算對不上也只是失敗，日誌看得到，不會靜靜改錯。
    /// </summary>
    private static async Task<StepResult> AddRouteAsync(
        string dest, string mask, string gateway, string metric, int interfaceIndex)
    {
        string label = $"route add {dest} mask {mask} {gateway} metric {metric}";

        ExecResult r = await ProcessRunner
            .RouteAsync("add", dest, "mask", mask, gateway, "metric", metric).ConfigureAwait(false);

        switch (Classify(r))
        {
            case RouteOutcome.Ok:
                return new StepResult(label, true, r.Output);

            case RouteOutcome.AlreadyExists:
                ExecResult c = await ProcessRunner
                    .RouteAsync("change", dest, "mask", mask, gateway,
                        "metric", metric, "if", interfaceIndex.ToString())
                    .ConfigureAwait(false);
                bool ok = Classify(c) == RouteOutcome.Ok;
                return new StepResult(
                    label + "（已經存在，改用 route change 更新）", ok, ok ? "" : c.Output);

            default:
                return new StepResult(label, false, r.Output);
        }
    }

    // ------------------------------------------------------------------

    private static void Validate(Settings cfg)
    {
        if (cfg.ExternalInterface == null || cfg.InternalInterface == null)
            throw new RouteConfigException("請先選擇外網和內網介面");
        if (cfg.InternalRanges.Count == 0)
            throw new RouteConfigException("請至少新增一個內網 IP 範圍");
        if (string.IsNullOrEmpty(cfg.InternalInterface.Gateway))
            throw new RouteConfigException("內網介面沒有閘道資訊，請按「重新偵測」");
        if (string.IsNullOrEmpty(cfg.ExternalInterface.Gateway))
            throw new RouteConfigException("外網介面沒有閘道資訊，請按「重新偵測」");
    }

    private static List<DomainRule> ValidDomains(Settings cfg) => cfg.InternalDomains
        .Where(d => NetInfo.NormalizeNamespace(d.Namespace).Length > 0 && d.DnsServers.Count > 0)
        .ToList();

    /// <summary>腳本以 "OK|訊息" / "ERR|訊息" 逐行回報，這裡轉回 StepResult。</summary>
    private static List<StepResult> ParseScriptOutput(ExecResult r, string fallbackLabel)
    {
        var results = new List<StepResult>();

        foreach (string line in SplitLines(r.Output))
        {
            if (line.StartsWith("OK|", StringComparison.Ordinal))
                results.Add(new StepResult(line[3..], true, ""));
            else if (line.StartsWith("ERR|", StringComparison.Ordinal))
                results.Add(new StepResult(line[4..], false, ""));
            else if (!r.Ok)
                results.Add(new StepResult(fallbackLabel, false, line));
        }

        // 腳本整個沒跑起來（powershell 本身失敗）時上面一行都收不到，補一筆才不會靜靜失敗
        if (results.Count == 0 && !r.Ok)
            results.Add(new StepResult(fallbackLabel, false, r.Output));

        return results;
    }

    private static IEnumerable<string> SplitLines(string text) => text
        .Split('\n')
        .Select(l => l.Trim('\r', ' ', '\t'))
        .Where(l => l.Length > 0);
}
