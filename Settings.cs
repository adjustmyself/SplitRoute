using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SplitRoute;

/// <summary>被指派為內網／外網的那張網卡，記住上次選了哪一張。</summary>
internal sealed class NetEndpointRef
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public string Ip { get; set; } = "";
    public string Gateway { get; set; } = "";
    public int PrefixLength { get; set; }

    public static NetEndpointRef From(NetAdapter a) => new()
    {
        Index = a.Index,
        Name = a.Name,
        Ip = a.Ip,
        Gateway = a.Gateway,
        PrefixLength = a.PrefixLength,
    };
}

/// <summary>一條 NRPT 規則：這個 domain 的 DNS 查詢走這幾台內網 DNS。</summary>
internal sealed class DomainRule
{
    public string Namespace { get; set; } = "";
    public List<string> DnsServers { get; set; } = new();
}

/// <summary>
/// 存在 %APPDATA%\SplitRoute\settings.json，重建 bin 或換資料夾都不會弄丟。
///
/// 用 JSON 而不是 RevPacker 那種 INI，是因為這裡的資料本身是巢狀的
/// （介面是物件、domain 是「物件裡再包一個清單」），而且**欄位名稱刻意與
/// 舊版 MyRoute 的 config.json 完全一致** —— 舊設定直接反序列化就能匯入，
/// 不用另外寫一套轉換。
/// </summary>
internal sealed class Settings
{
    public NetEndpointRef? ExternalInterface { get; set; }
    public NetEndpointRef? InternalInterface { get; set; }
    public List<string> InternalRanges { get; set; } = new();
    public List<string> InternalDnsServers { get; set; } = new();
    public List<DomainRule> InternalDomains { get; set; } = new();

    /// <summary>"applied" 或 "cleared"。沿用舊版欄位名與值，才能直接吃舊設定檔。</summary>
    public string RouteStatus { get; set; } = "cleared";

    /// <summary>SplitRoute 新增：登入自動啟動時要不要自己把路由套回去。</summary>
    public bool RestoreOnStartup { get; set; } = true;

    /// <summary>SplitRoute 新增：關閉視窗時縮到系統匣而不是結束程式。</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>
    /// SplitRoute 新增：開機還原時最多檢查幾次網路。
    /// <see cref="RestoreWaitForever"/> 為 true（預設）時用不到 —— 那時是一直等下去。
    /// </summary>
    public int RestoreMaxAttempts { get; set; } = DefaultRestoreMaxAttempts;

    /// <summary>
    /// SplitRoute 新增：開機還原時每隔幾秒檢查一次網路。
    ///
    /// 這是「保險輪詢」的間隔，不是反應速度：網卡上線會發出
    /// NetworkAddressChanged，<see cref="NetworkWatcher"/> 收到後兩秒內就會醒，
    /// 這個值防的是事件沒發出來的情況，所以不必為了等久一點把它調大。
    /// </summary>
    public int RestoreRetryDelaySeconds { get; set; } = DefaultRestoreRetryDelaySeconds;

    /// <summary>
    /// SplitRoute 新增：等不到網路時要不要一直等下去。
    ///
    /// 預設 true。程式本來就常駐系統匣，沒有理由等滿一分鐘就放棄 —— 放棄換來的是
    /// 「DNS 與 NRPT 在、路由不在」那種最難查的半套狀態，而 USB 網路共用要等使用者
    /// 解鎖手機、插上線，本來就沒有一個猜得準的秒數。關掉之後才會照
    /// <see cref="RestoreMaxAttempts"/> × <see cref="RestoreRetryDelaySeconds"/> 放棄。
    /// </summary>
    public bool RestoreWaitForever { get; set; } = true;

    /// <summary>
    /// SplitRoute 新增：套用之後留在背景監看網路變化，閘道換掉就自動重新套用。
    /// USB 網路共用拔插、DHCP 換位址之後路由會指著一個不存在的閘道，
    /// 這是這個工具最常見的失效方式，而且畫面上看不出來。
    /// </summary>
    public bool WatchNetworkChanges { get; set; } = true;

    public const int DefaultRestoreMaxAttempts = 12;
    public const int DefaultRestoreRetryDelaySeconds = 5;

    // 上下限：畫面上的輸入框和 RouteEngine 都拿這組值，才不會出現「畫面上填得進去、
    // 實際卻被夾掉」的落差。上限抓寬鬆（120 次 × 300 秒），只是擋住手滑打成天文數字
    // 讓還原永遠跑不完；下限 1 次／1 秒則是擋掉 0 —— 0 次等於這個功能靜靜不執行。
    public const int MinRestoreMaxAttempts = 1;
    public const int MaxRestoreMaxAttempts = 120;
    public const int MinRestoreRetryDelaySeconds = 1;
    public const int MaxRestoreRetryDelaySeconds = 300;

    [JsonIgnore]
    public bool Applied
    {
        get => string.Equals(RouteStatus, "applied", StringComparison.OrdinalIgnoreCase);
        set => RouteStatus = value ? "applied" : "cleared";
    }

    // ------------------------------------------------------------------
    // 位置
    // ------------------------------------------------------------------

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SplitRoute");

    /// <summary>設定檔位置，「關於」視窗要顯示（協助別人排除問題時會用到）。</summary>
    public static string FilePath => Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ------------------------------------------------------------------
    // 讀寫
    // ------------------------------------------------------------------

    /// <summary>
    /// 讀設定。第一次執行（還沒有自己的設定檔）時會去找舊版 MyRoute 的
    /// config.json 並直接沿用，使用者不必把三個內網範圍、DNS、domain 重打一次。
    /// </summary>
    public static Settings Load(out string? importedFrom)
    {
        importedFrom = null;

        Settings? s = TryRead(FilePath);
        if (s != null) return s.Normalized();

        string? legacy = FindLegacyConfig();
        if (legacy != null)
        {
            s = TryRead(legacy);
            if (s != null)
            {
                importedFrom = legacy;
                s = s.Normalized();
                s.Save();          // 立刻落地，之後就走自己的設定檔
                return s;
            }
        }

        return new Settings();
    }

    public static Settings Load() => Load(out _);

    /// <summary>手動匯入（檔案 → 匯入設定）。失敗回傳 null，呼叫端負責提示。</summary>
    public static Settings? Import(string path) => TryRead(path)?.Normalized();

    private static Settings? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path, Encoding.UTF8), JsonOpts);
        }
        catch { return null; }   // 設定檔壞掉就當作沒有，不要擋住工具
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);

            // 寫 BOM：這個檔使用者會自己打開來看或備份，而 Windows 上一堆工具
            // （PowerShell 5.1 的 Get-Content、舊版記事本）對無 BOM 的 UTF-8
            // 會用系統字碼頁解，中文介面名稱就變成亂碼。讀取端會自動去掉 BOM。
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts), new UTF8Encoding(true));
        }
        catch { /* 存不起來不是大事，下次重設就好 */ }
    }

    /// <summary>反序列化出來的清單可能是 null（舊檔沒有那些欄位），一律補成空清單。</summary>
    private Settings Normalized()
    {
        InternalRanges ??= new List<string>();
        InternalDnsServers ??= new List<string>();
        InternalDomains ??= new List<DomainRule>();
        foreach (DomainRule d in InternalDomains) d.DnsServers ??= new List<string>();
        if (string.IsNullOrWhiteSpace(RouteStatus)) RouteStatus = "cleared";

        // 舊設定檔沒有這兩個欄位時反序列化不會覆蓋屬性初始值，所以這裡夾的是
        // 手改設定檔或匯入到超出範圍的數字（尤其是 0）的情況。
        RestoreMaxAttempts = Math.Clamp(RestoreMaxAttempts, MinRestoreMaxAttempts, MaxRestoreMaxAttempts);
        RestoreRetryDelaySeconds = Math.Clamp(
            RestoreRetryDelaySeconds, MinRestoreRetryDelaySeconds, MaxRestoreRetryDelaySeconds);
        return this;
    }

    /// <summary>
    /// 從 exe 所在位置往上找舊版設定：exe 旁邊的 config.json，或任何一層的
    /// MyRoute\config.json。
    ///
    /// 層數要夠深：發行版的 exe 在 bin\Release\net10.0-windows\win-x64\publish\，
    /// 離「MyRoute 的隔壁」有七層。找不到也不是問題，畫面上還有「檔案 → 匯入設定」。
    /// </summary>
    private static string? FindLegacyConfig()
    {
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            {
                string beside = Path.Combine(dir.FullName, "config.json");
                if (File.Exists(beside)) return beside;

                string myRoute = Path.Combine(dir.FullName, "MyRoute", "config.json");
                if (File.Exists(myRoute)) return myRoute;
            }
        }
        catch { /* 路徑怪掉就當作找不到 */ }
        return null;
    }
}
