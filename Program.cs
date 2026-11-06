using System.Reflection;

namespace SplitRoute;

/// <summary>
/// 版本字串。工具是複製 exe 散佈的，別人手上那份是哪一版只能靠這個問出來，
/// 所以標題列和「關於」都要顯示。版本號在 SplitRoute.csproj 的 &lt;Version&gt;。
/// </summary>
internal static class AppInfo
{
    public const string Name = "SplitRoute";
    public const string Tagline = "內外網雙網卡分流路由管理";

    public static string Version
    {
        get
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            string? v = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(v))
            {
                // SDK 專案在 InformationalVersion 後面可能接 "+<commit hash>"
                int plus = v.IndexOf('+');
                return plus > 0 ? v[..plus] : v;
            }
            return asm.GetName().Version?.ToString(3) ?? "1.0.0";
        }
    }

    /// <summary>
    /// exe 的實際路徑。單一檔案發行時 Assembly.Location 是空字串（組件並沒有以檔案存在），
    /// 所以一定要走 Environment.ProcessPath —— 排程工作要靠它填執行路徑。
    /// </summary>
    public static string ExePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, Name + ".exe");

    public static string ExeFolder => Path.GetDirectoryName(ExePath) ?? AppContext.BaseDirectory;
}

internal static class Program
{
    // 提權後仍在同一個登入工作階段，所以用 Local\ 就夠，不需要 Global\
    private const string MutexName = @"Local\SplitRoute.SingleInstance";
    private const string ShowEventName = @"Local\SplitRoute.ShowWindow";

    /// <summary>
    /// 可帶 /tray 啟動（登入排程用）：直接縮在系統匣，不跳視窗，
    /// 並在網路就緒後自動把上次套用的路由補回去。
    /// </summary>
    [STAThread]
    private static void Main(string[] args)
    {
        bool startInTray = args.Any(a =>
            a.Equals("/tray", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("-tray", StringComparison.OrdinalIgnoreCase));

        // 這支程式會改整台機器的路由表，兩份同時在跑就會互相打架
        // （一邊套用一邊清除），所以只准一份。
        using var mutex = new Mutex(true, MutexName, out bool isFirst);
        if (!isFirst)
        {
            // 已經有一份在跑（很可能正縮在系統匣），叫它把視窗叫出來就好
            try
            {
                using EventWaitHandle existing = EventWaitHandle.OpenExisting(ShowEventName);
                existing.Set();
            }
            catch { /* 對方剛好正在關閉，那就什麼都不做 */ }
            return;
        }

        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

        // PerMonitorV2 要在建立任何視窗之前設定。寫在這裡而不是 app.manifest，
        // 是因為 WinForms 明確要求走這個 API（manifest 版會出 WFO0003 警告）。
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try
        {
            Settings settings = Settings.Load(out string? importedFrom);
            var form = new MainForm(settings, importedFrom, startInTray);
            WatchForShowRequests(showEvent, form);
            Application.Run(form);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), AppInfo.Name + " 發生未預期錯誤",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        GC.KeepAlive(mutex);
    }

    /// <summary>
    /// 第二份被擋掉之前會設定這個事件，這裡收到就把主視窗叫回前景 ——
    /// 不然使用者按了捷徑卻什麼都沒發生，只會以為程式壞了。
    /// </summary>
    private static void WatchForShowRequests(EventWaitHandle showEvent, MainForm form)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try { showEvent.WaitOne(); }
                catch { return; }

                try { form.BeginInvoke(form.ShowFromTray); }
                catch { return; }   // 視窗已經銷毀，代表程式正在結束
            }
        })
        {
            IsBackground = true,
            Name = "SplitRoute.ShowWatcher",
        };
        thread.Start();
    }
}
