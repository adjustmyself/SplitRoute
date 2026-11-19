using System.Diagnostics;
using System.Drawing;
using System.Reflection;

namespace SplitRoute;

internal sealed class MainForm : Form
{
    private readonly Settings _cfg;
    private readonly string? _importedFrom;

    /// <summary>帶 /tray 啟動時，第一次的 Show 要被吃掉（見 SetVisibleCore）。</summary>
    private bool _startHidden;

    /// <summary>這次是登入排程叫起來的（而不是使用者自己點開的）。</summary>
    private readonly bool _launchedInTray;

    /// <summary>StartupAsync 只能跑一次。</summary>
    private bool _startedUp;

    /// <summary>使用者是真的要離開，而不是按 X 縮到系統匣。</summary>
    private bool _exiting;

    /// <summary>正在跑套用／清除，期間不接受第二次操作。</summary>
    private bool _busy;

    /// <summary>
    /// 正在等網路上線。這段時間刻意<b>不算</b>忙碌：可能要等使用者走過去插 USB 線，
    /// 幾分鐘都有可能，總不能因此把整個視窗鎖住。
    /// </summary>
    private bool _waitingForNetwork;

    /// <summary>正在用設定填畫面，此時的 change 事件不算使用者操作。</summary>
    private bool _loading;

    private List<NetAdapter> _adapters = new();
    private CancellationTokenSource? _work;

    /// <summary>取消「等網路就緒」用的；使用者自己動手套用／清除就不必再等了。</summary>
    private CancellationTokenSource? _wait;

    /// <summary>套用之後在背景看著網路，閘道換了就自動重套。</summary>
    private RouteGuard? _guard;

    /// <summary>上次「因為監看而自動重套」的時間，用來擋住網路一直抖時的連續重套。</summary>
    private DateTime _lastAutoApply = DateTime.MinValue;

    /// <summary>兩次自動重套之間至少要隔這麼久。</summary>
    private static readonly TimeSpan AutoApplyCooldown = TimeSpan.FromSeconds(30);

    // --- 控制項 ---
    private NotifyIcon _tray = null!;
    private Label _lblState = null!;
    private ListView _lvAdapters = null!;
    private ComboBox _cboExternal = null!, _cboInternal = null!;
    private ListBox _lstRanges = null!, _lstDns = null!;
    private ListView _lvDomains = null!;
    private TextBox _txtRange = null!, _txtDns = null!, _txtDomain = null!, _txtDomainDns = null!;
    private Button _btnApply = null!, _btnClear = null!, _btnDetect = null!;
    private RichTextBox _log = null!;
    private ToolStripStatusLabel _lblStatus = null!;
    private ToolStripMenuItem _miAutoStart = null!, _miMinimizeToTray = null!, _miRestore = null!;
    private ToolStripMenuItem _miRestoreRetry = null!, _miWatch = null!;

    private static readonly Color OkColor = Color.FromArgb(0, 110, 40);
    private static readonly Color ErrColor = Color.FromArgb(178, 34, 34);
    private static readonly Color InfoColor = Color.FromArgb(90, 90, 90);
    private static readonly Color WarnColor = Color.FromArgb(170, 100, 0);

    public MainForm(Settings cfg, string? importedFrom, bool startInTray)
    {
        _cfg = cfg;
        _importedFrom = importedFrom;
        _startHidden = startInTray;
        _launchedInTray = startInTray;

        BuildUi();
        Load += async (_, _) => await StartupAsync();
        FormClosing += MainForm_FormClosing;
    }

    // ==================================================================
    // 介面
    // ==================================================================

    /// <summary>
    /// 視窗標題列／工作列／系統匣的圖示。exe 本身的圖示是 csproj 的 ApplicationIcon 決定，
    /// 這裡另外從嵌入資源載入同一個 .ico，才有完整的多尺寸可用。
    /// 換圖示只要替換 SplitRoute.ico 再重新建置即可，程式碼不用動。
    /// </summary>
    private static Icon? LoadAppIcon()
    {
        try
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            using Stream? s = asm.GetManifestResourceStream("SplitRoute.SplitRoute.ico");
            return s == null ? null : new Icon(s);
        }
        catch { return null; }   // 沒圖示就用預設的，不值得為這個中斷
    }

    private static Label MakeCaption(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Margin = new Padding(0, 6, 6, 0),
    };

    private static Button MakeButton(string text, Action onClick, int width = 88)
    {
        var b = new Button { Text = text, Width = width, Height = 27, Margin = new Padding(0, 0, 6, 0) };
        b.Click += (_, _) => onClick();
        return b;
    }

    private static GroupBox MakeGroup(string text, DockStyle dock, int size = 0)
    {
        var g = new GroupBox { Text = text, Dock = dock, Padding = new Padding(8, 6, 8, 8) };
        if (dock == DockStyle.Top || dock == DockStyle.Bottom) g.Height = size;
        if (dock == DockStyle.Left || dock == DockStyle.Right) g.Width = size;
        return g;
    }

    /// <summary>
    /// 清單底下那一列「輸入框 + 按鈕」。寬度 &gt; 0 是固定像素，&lt; 0 是百分比，
    /// 0 表示吃掉剩下的空間。輸入框和按鈕的天生高度不一樣，靠 Margin 對齊，
    /// 不然一列裡會高高低低。
    /// </summary>
    private static TableLayoutPanel MakeInputRow(params (Control Control, int Width)[] cells)
    {
        var t = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 34,
            ColumnCount = cells.Length,
            RowCount = 1,
            Padding = new Padding(0, 2, 0, 0),
        };

        for (int i = 0; i < cells.Length; i++)
        {
            (Control c, int w) = cells[i];
            t.ColumnStyles.Add(w switch
            {
                > 0 => new ColumnStyle(SizeType.Absolute, w),
                < 0 => new ColumnStyle(SizeType.Percent, -w),
                _ => new ColumnStyle(SizeType.Percent, 100),
            });
            c.Dock = DockStyle.Fill;
            c.Margin = c is TextBox ? new Padding(0, 4, 6, 4) : new Padding(0, 2, 6, 2);
            t.Controls.Add(c, i, 0);
        }
        return t;
    }

    private void BuildUi()
    {
        Text = $"{AppInfo.Name} {AppInfo.Version} — {AppInfo.Tagline}";
        ClientSize = new Size(1180, 780);
        MinimumSize = new Size(1000, 680);
        StartPosition = FormStartPosition.CenterScreen;
        try { Font = new Font("Microsoft JhengHei UI", 9.75F); } catch { /* 沒這個字型就用預設 */ }
        Icon = LoadAppIcon();

        // 依 WinForms 的停駐規則，Fill 的要先加，Top/Bottom 的後加
        Controls.Add(BuildBody());
        Controls.Add(BuildActionBar());
        Controls.Add(BuildHeader());
        Controls.Add(BuildStatusBar());
        Controls.Add(BuildMenu());

        BuildTray();
    }

    private MenuStrip BuildMenu()
    {
        var miImport = new ToolStripMenuItem("匯入設定(&I)…", null, (_, _) => ImportSettings());
        var miOpenFolder = new ToolStripMenuItem("開啟設定檔位置(&O)", null, (_, _) => OpenSettingsFolder());
        var miExit = new ToolStripMenuItem("結束(&X)", null, (_, _) => ExitApp());

        _miAutoStart = new ToolStripMenuItem("登入時自動啟動(&L)") { CheckOnClick = true };
        _miAutoStart.Click += async (_, _) => await ToggleAutoStartAsync();

        _miRestore = new ToolStripMenuItem("自動啟動時還原上次的路由(&R)")
        {
            CheckOnClick = true,
            Checked = _cfg.RestoreOnStartup,
        };
        _miRestore.CheckedChanged += (_, _) => { _cfg.RestoreOnStartup = _miRestore.Checked; _cfg.Save(); };

        // 次數與秒數放在同一個小視窗裡改：兩個值要一起看才知道總共會等多久，
        // 拆成兩個選單項目反而要按兩次、還看不到相乘的結果。
        _miRestoreRetry = new ToolStripMenuItem("", null, (_, _) => EditRestoreRetry());
        UpdateRestoreRetryText();

        _miWatch = new ToolStripMenuItem("網路變更時自動重新套用(&W)")
        {
            CheckOnClick = true,
            Checked = _cfg.WatchNetworkChanges,
            ToolTipText = "USB 網路共用拔插、DHCP 換閘道之後，路由會指著一個不存在的閘道。"
                          + "打開這個就會自動重套。",
        };
        _miWatch.CheckedChanged += (_, _) =>
        {
            _cfg.WatchNetworkChanges = _miWatch.Checked;
            _cfg.Save();
            if (_miWatch.Checked) StartGuard(); else _guard?.Stop();
            LogLine(_miWatch.Checked ? "已開啟網路變更監看" : "已關閉網路變更監看", InfoColor);
        };

        _miMinimizeToTray = new ToolStripMenuItem("關閉視窗時縮到系統匣(&T)")
        {
            CheckOnClick = true,
            Checked = _cfg.MinimizeToTray,
        };
        _miMinimizeToTray.CheckedChanged += (_, _) => { _cfg.MinimizeToTray = _miMinimizeToTray.Checked; _cfg.Save(); };

        var menu = new MenuStrip();
        menu.Items.Add(new ToolStripMenuItem("檔案(&F)", null, miImport, miOpenFolder, new ToolStripSeparator(), miExit));
        menu.Items.Add(new ToolStripMenuItem("設定(&S)", null,
            _miAutoStart, _miRestore, _miRestoreRetry, _miWatch, _miMinimizeToTray));
        menu.Items.Add(new ToolStripMenuItem("工具(&T)", null,
            new ToolStripMenuItem("檢視路由表(&R)", null, async (_, _) => await ViewRouteTableAsync()),
            new ToolStripMenuItem("檢視 NRPT 規則（已設定 vs 生效中）(&N)", null, async (_, _) => await ViewNrptAsync()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("重新取得外網 IP（ipconfig /renew）(&E)", null, async (_, _) => await RenewExternalAsync()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("清除日誌(&C)", null, (_, _) => _log.Clear())));
        menu.Items.Add(new ToolStripMenuItem("說明(&H)", null,
            new ToolStripMenuItem("使用說明(&M)", null, (_, _) => OpenManual()),
            new ToolStripMenuItem("關於(&A)", null, (_, _) => ShowAbout())));
        return menu;
    }

    private Panel BuildHeader()
    {
        _lblState = new Label
        {
            Dock = DockStyle.Left,
            AutoSize = false,
            Width = 520,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(Font.FontFamily, 11F, FontStyle.Bold),
        };

        _btnDetect = MakeButton("重新偵測", async () => await DetectAsync(), 100);
        _btnDetect.Dock = DockStyle.Right;
        _btnDetect.Margin = new Padding(0);

        var p = new Panel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(12, 8, 12, 4) };
        p.Controls.Add(_lblState);
        p.Controls.Add(_btnDetect);
        return p;
    }

    private StatusStrip BuildStatusBar()
    {
        _lblStatus = new ToolStripStatusLabel("就緒") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        var strip = new StatusStrip();
        strip.Items.Add(_lblStatus);
        return strip;
    }

    private Panel BuildActionBar()
    {
        _btnApply = MakeButton("套用路由", async () => await ApplyAsync(), 120);
        _btnApply.Height = 32;
        _btnClear = MakeButton("清除路由", async () => await ClearAsync(), 120);
        _btnClear.Height = 32;

        var btnRoutes = MakeButton("檢視路由表", async () => await ViewRouteTableAsync(), 120);
        btnRoutes.Height = 32;

        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        flow.Controls.AddRange(new Control[] { _btnApply, _btnClear, btnRoutes });

        var p = new Panel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(12, 7, 12, 7) };
        p.Controls.Add(flow);
        return p;
    }

    private SplitContainer BuildBody()
    {
        // SplitterDistance 與 Panel*MinSize 都是拿目前寬度去驗證的，而 SplitContainer
        // 剛建出來只有預設的 150 寬 —— 先給尺寸再設這三個值，否則建構當場就丟例外。
        var split = new SplitContainer
        {
            Orientation = Orientation.Vertical,
            Size = new Size(1156, 600),
            SplitterWidth = 6,
        };
        split.Panel1MinSize = 420;
        split.Panel2MinSize = 380;
        split.SplitterDistance = 560;
        split.Dock = DockStyle.Fill;

        split.Panel1.Padding = new Padding(12, 0, 3, 0);
        split.Panel2.Padding = new Padding(3, 0, 12, 0);

        split.Panel1.Controls.Add(BuildRangesGroup());
        split.Panel1.Controls.Add(BuildAdaptersGroup());

        split.Panel2.Controls.Add(BuildLogGroup());
        split.Panel2.Controls.Add(BuildDomainsGroup());
        split.Panel2.Controls.Add(BuildDnsGroup());

        return split;
    }

    private GroupBox BuildAdaptersGroup()
    {
        _lvAdapters = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        _lvAdapters.Columns.Add("介面名稱", 168);
        _lvAdapters.Columns.Add("IP 位址", 110);
        _lvAdapters.Columns.Add("閘道", 172);
        _lvAdapters.Columns.Add("角色", 54);

        _cboExternal = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        _cboInternal = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        _cboExternal.SelectedIndexChanged += (_, _) => OnRoleChanged();
        _cboInternal.SelectedIndexChanged += (_, _) => OnRoleChanged();

        var picker = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 62,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(0, 6, 0, 0),
        };
        picker.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        picker.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        picker.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        picker.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        picker.Controls.Add(MakeCaption("外網（預設路由 / USB）"), 0, 0);
        picker.Controls.Add(_cboExternal, 1, 0);
        picker.Controls.Add(MakeCaption("內網（公司網路線）"), 0, 1);
        picker.Controls.Add(_cboInternal, 1, 1);

        GroupBox g = MakeGroup("網路介面", DockStyle.Top, 268);
        g.Controls.Add(_lvAdapters);
        g.Controls.Add(picker);
        return g;
    }

    private GroupBox BuildRangesGroup()
    {
        _lstRanges = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        try { _lstRanges.Font = new Font("Consolas", 10F); } catch { /* 用預設字型 */ }
        _lstRanges.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) RemoveRange(); };

        _txtRange = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "192.168.1.0/24" };
        _txtRange.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            AddRange();
        };

        TableLayoutPanel input = MakeInputRow(
            (_txtRange, 0),
            (MakeButton("新增", AddRange), 72),
            (MakeButton("刪除", RemoveRange), 72),
            (MakeButton("一鍵新增所有內網範圍", AddAllPrivateRanges), 172));

        GroupBox g = MakeGroup("內網 IP 範圍（CIDR，走內網閘道）", DockStyle.Fill);
        g.Controls.Add(_lstRanges);
        g.Controls.Add(input);
        return g;
    }

    private GroupBox BuildDnsGroup()
    {
        _lstDns = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        try { _lstDns.Font = new Font("Consolas", 10F); } catch { /* 用預設字型 */ }
        _lstDns.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) RemoveDns(); };

        _txtDns = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "10.0.0.53" };
        _txtDns.KeyDown += (_, e)
            =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            AddDns();
        };

        TableLayoutPanel input = MakeInputRow(
            (_txtDns, 0),
            (MakeButton("新增", AddDns), 72),
            (MakeButton("刪除", RemoveDns), 72));

        GroupBox g = MakeGroup("內網 DNS 伺服器（套用時設到內網介面；留空則不動 DNS）", DockStyle.Top, 156);
        g.Controls.Add(_lstDns);
        g.Controls.Add(input);
        return g;
    }

    private GroupBox BuildDomainsGroup()
    {
        _lvDomains = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        _lvDomains.Columns.Add("Domain", 210);
        _lvDomains.Columns.Add("DNS 伺服器", 220);
        _lvDomains.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) RemoveDomain(); };

        _txtDomain = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "corp.example.com" };
        _txtDomainDns = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "10.0.0.1, 10.0.0.2" };
        _txtDomainDns.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            AddDomain();
        };

        TableLayoutPanel input = MakeInputRow(
            (_txtDomain, -55),
            (_txtDomainDns, -45),
            (MakeButton("新增", AddDomain), 72),
            (MakeButton("刪除", RemoveDomain), 72));

        // 這個提醒在舊版也有，而且是實務上最常見的「設了卻沒生效」原因，一定要留著
        var hint = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 34,
            ForeColor = InfoColor,
            Text = "把特定 domain 的 DNS 查詢導向內網 DNS。\n"
                 + "⚠ 瀏覽器的「安全 DNS / DoH」會繞過這個規則，沒生效時請先去瀏覽器把它關掉。",
        };

        GroupBox g = MakeGroup("內網 DNS Domain（NRPT）", DockStyle.Top, 232);
        g.Controls.Add(_lvDomains);
        g.Controls.Add(hint);
        g.Controls.Add(input);
        return g;
    }

    private GroupBox BuildLogGroup()
    {
        _log = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = Color.White,
            BorderStyle = BorderStyle.None,
            WordWrap = false,
            ScrollBars = RichTextBoxScrollBars.Both,
            DetectUrls = false,
        };
        try { _log.Font = new Font("Consolas", 9.5F); } catch { /* 用預設字型 */ }

        GroupBox g = MakeGroup("執行日誌", DockStyle.Fill);
        g.Controls.Add(_log);
        return g;
    }

    private void BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("顯示視窗(&O)", null, (_, _) => ShowFromTray()) { Font = new Font(Font, FontStyle.Bold) });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("套用路由(&A)", null, async (_, _) => await ApplyAsync()));
        menu.Items.Add(new ToolStripMenuItem("清除路由(&C)", null, async (_, _) => await ClearAsync()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("結束(&X)", null, (_, _) => ExitApp()));

        _tray = new NotifyIcon
        {
            Icon = Icon ?? SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();
    }

    // ==================================================================
    // 生命週期
    // ==================================================================

    /// <summary>
    /// /tray 啟動時要一路隱形到系統匣。Form 的第一次 Show 是 Application.Run 發動的，
    /// 攔不到，所以在這裡把它吃掉（handle 還是要建，不然 BeginInvoke 沒得用）。
    ///
    /// 視窗不顯示，Load 事件就不會發生，所以這條路要自己把啟動流程排下去 ——
    /// 否則登入自動啟動最重要的那件事（還原路由）永遠不會執行。
    /// </summary>
    protected override void SetVisibleCore(bool value)
    {
        if (_startHidden)
        {
            _startHidden = false;
            if (!IsHandleCreated) CreateHandle();
            value = false;
            BeginInvoke(async () => await StartupAsync());
        }
        base.SetVisibleCore(value);
    }

    private async Task StartupAsync()
    {
        if (_startedUp) return;
        _startedUp = true;

        if (_importedFrom != null)
            LogLine($"已匯入舊版設定：{_importedFrom}", InfoColor);

        LogLine($"設定檔：{Settings.FilePath}", InfoColor);

        _miAutoStart.Checked = await AutoStart.IsInstalledAsync();

        await DetectAsync();

        // 只有「登入自動啟動」那條路才會自己套用路由。使用者親手打開視窗時
        // 不要偷偷改他的路由表 —— 他可能只是想看一眼目前狀態。
        if (_launchedInTray && _cfg.RestoreOnStartup && _cfg.Applied)
            await RestoreAsync();

        // 還原成功與否都要開始監看：失敗（等待被取消、套用出錯）之後網路才好起來的話，
        // 接手把它補上的正是守候這一段。
        StartGuard();
    }

    /// <summary>
    /// 開始／繼續在背景監看網路變化。已經在看了就什麼都不做，
    /// 所以可以在啟動、套用完成、選單切換等地方放心地重複呼叫。
    /// </summary>
    private void StartGuard()
    {
        if (!_cfg.WatchNetworkChanges) return;

        _guard ??= new RouteGuard(_cfg, ReportGuardChange);
        _guard.Start();
    }

    /// <summary>
    /// 守候是在背景執行緒上跑的，通知一律先丟回 UI 執行緒再處理。
    /// 視窗正在關的時候 BeginInvoke 會丟例外，那時候通知不到也無所謂。
    /// </summary>
    private void ReportGuardChange(GuardChange change, string message)
    {
        try
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(() => OnGuardChange(change, message));
        }
        catch (ObjectDisposedException) { /* 正在收尾 */ }
        catch (InvalidOperationException) { /* handle 已經沒了 */ }
    }

    private async void OnGuardChange(GuardChange change, string message)
    {
        LogLine("[監看] " + message, WarnColor);

        if (change == GuardChange.ExternalOffline)
        {
            SetStatus("外網已離線，等待重新連線…");
            return;
        }

        // 使用者正在手動操作、或還原還在等網路，就讓那邊去處理，不要兩邊搶著套
        if (_busy || _waitingForNetwork) return;

        // 網路抖動時事件會一直來，擋一下才不會變成連續重套
        if (DateTime.UtcNow - _lastAutoApply < AutoApplyCooldown)
        {
            LogLine("[監看] 剛剛才自動套用過，這次先跳過。", InfoColor);
            return;
        }
        _lastAutoApply = DateTime.UtcNow;

        await ApplyAsync();
        _tray.ShowBalloonTip(4000, AppInfo.Name,
            "外網連線有變動，已自動重新套用路由設定。", ToolTipIcon.Info);
    }

    private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_exiting && _cfg.MinimizeToTray && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            _tray.ShowBalloonTip(3000, AppInfo.Name,
                "還在系統匣裡執行中，路由設定維持不變。要完全結束請用系統匣圖示 → 結束。",
                ToolTipIcon.Info);
            return;
        }

        _work?.Cancel();
        _wait?.Cancel();
        _guard?.Dispose();
        _cfg.Save();
        _tray.Visible = false;
        _tray.Dispose();
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    private void ExitApp()
    {
        _exiting = true;
        Close();
        Application.Exit();
    }

    // ==================================================================
    // 偵測與繪製
    // ==================================================================

    private async Task DetectAsync()
    {
        _btnDetect.Enabled = false;
        SetStatus("偵測網路介面中…");
        try
        {
            _adapters = await Task.Run(NetInfo.List);
            RouteEngine.RefreshInterfaces(_cfg);
            _cfg.Save();
            RenderAll();
            SetStatus($"找到 {_adapters.Count} 個網路介面");
        }
        catch (Exception ex)
        {
            LogLine("偵測網路介面失敗：" + ex.Message, ErrColor);
            SetStatus("偵測失敗");
        }
        finally
        {
            _btnDetect.Enabled = true;
        }
    }

    private void RenderAll()
    {
        _loading = true;
        try
        {
            RenderAdapters();
            RenderRanges();
            RenderDns();
            RenderDomains();
        }
        finally { _loading = false; }

        RenderState();
    }

    private void RenderAdapters()
    {
        _lvAdapters.BeginUpdate();
        _lvAdapters.Items.Clear();

        foreach (NetAdapter a in _adapters)
        {
            string role = "—";
            if (_cfg.ExternalInterface?.Index == a.Index) role = "外網";
            if (_cfg.InternalInterface?.Index == a.Index) role = "內網";

            // 閘道不在自己網段上時要講出來：這正是「套用後上不了網」最常見的原因，
            // 而畫面上只印一個 IP 是看不出問題的。
            string gw = string.IsNullOrEmpty(a.Gateway)
                ? "—"
                : a.Gateway + (a.GatewayOnLink ? "" : "（非同網段，不採用）");

            var item = new ListViewItem(new[] { a.Name, string.IsNullOrEmpty(a.Ip) ? "—" : a.Ip, gw, role })
            {
                ToolTipText = a.Description,
            };
            if (role == "外網") item.ForeColor = OkColor;
            if (role == "內網") item.ForeColor = Color.FromArgb(20, 70, 160);
            _lvAdapters.Items.Add(item);
        }

        _lvAdapters.EndUpdate();

        FillCombo(_cboExternal, _cfg.ExternalInterface);
        FillCombo(_cboInternal, _cfg.InternalInterface);
    }

    private void FillCombo(ComboBox combo, NetEndpointRef? selected)
    {
        combo.BeginUpdate();
        combo.Items.Clear();
        combo.Items.Add("-- 未選擇 --");
        foreach (NetAdapter a in _adapters) combo.Items.Add(a);

        int index = selected == null
            ? -1
            : _adapters.FindIndex(a => a.Index == selected.Index);
        combo.SelectedIndex = index >= 0 ? index + 1 : 0;
        combo.EndUpdate();
    }

    private void OnRoleChanged()
    {
        if (_loading) return;

        _cfg.ExternalInterface = SelectedRef(_cboExternal);
        _cfg.InternalInterface = SelectedRef(_cboInternal);
        _cfg.Save();

        _loading = true;
        try { RenderAdapters(); }
        finally { _loading = false; }

        RenderState();
    }

    private NetEndpointRef? SelectedRef(ComboBox combo)
        => combo.SelectedItem is NetAdapter a ? NetEndpointRef.From(a) : null;

    private void RenderRanges()
    {
        _lstRanges.BeginUpdate();
        _lstRanges.Items.Clear();
        foreach (string r in _cfg.InternalRanges) _lstRanges.Items.Add(r);
        _lstRanges.EndUpdate();
    }

    private void RenderDns()
    {
        _lstDns.BeginUpdate();
        _lstDns.Items.Clear();
        foreach (string d in _cfg.InternalDnsServers) _lstDns.Items.Add(d);
        _lstDns.EndUpdate();
    }

    private void RenderDomains()
    {
        _lvDomains.BeginUpdate();
        _lvDomains.Items.Clear();
        foreach (DomainRule d in _cfg.InternalDomains)
            _lvDomains.Items.Add(new ListViewItem(new[] { d.Namespace, string.Join(", ", d.DnsServers) }));
        _lvDomains.EndUpdate();
    }

    private void RenderState()
    {
        string text;
        Color color;

        if (_waitingForNetwork)
        {
            // 這個狀態要在系統匣提示上看得到：使用者八成正是因為「開機後上不了內網」
            // 才把滑鼠移過去的，答案就是「還在等網卡」。
            text = "● 等待網路就緒…（網卡一上線就自動套用）";
            color = WarnColor;
        }
        else if (_cfg.ExternalInterface == null || _cfg.InternalInterface == null)
        {
            text = "● 尚未指定內外網介面";
            color = Color.DarkOrange;
        }
        else if (_cfg.Applied)
        {
            text = "● 路由已套用";
            color = OkColor;
        }
        else
        {
            text = "● 路由未套用（使用系統預設）";
            color = Color.DarkOrange;
        }

        _lblState.Text = text;
        _lblState.ForeColor = color;
        _tray.Text = $"{AppInfo.Name} — {text[2..]}";   // 系統匣提示最長 63 字元，去掉圓點省一點
        _btnApply.Enabled = !_busy;
        _btnClear.Enabled = !_busy;
    }

    // ==================================================================
    // 清單編輯
    // ==================================================================

    private void AddRange()
    {
        string val = _txtRange.Text.Trim();
        if (val.Length == 0) return;

        if (!NetInfo.IsValidCidr(val))
        {
            Warn("格式錯誤，請使用 CIDR 格式（例如 192.168.1.0/24）");
            return;
        }
        if (_cfg.InternalRanges.Contains(val))
        {
            Warn("此範圍已存在");
            return;
        }

        _cfg.InternalRanges.Add(val);
        _txtRange.Clear();
        _cfg.Save();
        RenderRanges();
    }

    private void RemoveRange()
    {
        int i = _lstRanges.SelectedIndex;
        if (i < 0 || i >= _cfg.InternalRanges.Count) return;
        _cfg.InternalRanges.RemoveAt(i);
        _cfg.Save();
        RenderRanges();
    }

    private void AddAllPrivateRanges()
    {
        string[] all = { "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16" };
        int added = all.Count(r => !_cfg.InternalRanges.Contains(r));
        foreach (string r in all)
            if (!_cfg.InternalRanges.Contains(r)) _cfg.InternalRanges.Add(r);

        if (added == 0) { Warn("所有內網範圍都已經在清單裡了"); return; }
        _cfg.Save();
        RenderRanges();
        SetStatus($"已新增 {added} 個內網範圍");
    }

    private void AddDns()
    {
        string val = _txtDns.Text.Trim();
        if (val.Length == 0) return;

        if (!NetInfo.IsValidIp(val)) { Warn("請輸入有效的 IPv4 位址"); return; }
        if (_cfg.InternalDnsServers.Contains(val)) { Warn("此 DNS 已存在"); return; }

        _cfg.InternalDnsServers.Add(val);
        _txtDns.Clear();
        _cfg.Save();
        RenderDns();
    }

    private void RemoveDns()
    {
        int i = _lstDns.SelectedIndex;
        if (i < 0 || i >= _cfg.InternalDnsServers.Count) return;
        _cfg.InternalDnsServers.RemoveAt(i);
        _cfg.Save();
        RenderDns();
    }

    private void AddDomain()
    {
        string ns = NetInfo.NormalizeNamespace(_txtDomain.Text);
        string raw = _txtDomainDns.Text.Trim();

        if (ns.Length == 0 || raw.Length == 0) { Warn("請輸入 Domain 與 DNS 伺服器"); return; }

        var servers = raw.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                         .ToList();
        string? bad = servers.FirstOrDefault(s => !NetInfo.IsValidIp(s));
        if (bad != null) { Warn("DNS IP 格式錯誤：" + bad); return; }

        if (_cfg.InternalDomains.Any(d => NetInfo.NormalizeNamespace(d.Namespace) == ns))
        {
            Warn("此 Domain 已存在");
            return;
        }

        _cfg.InternalDomains.Add(new DomainRule { Namespace = ns, DnsServers = servers });
        _txtDomain.Clear();
        _txtDomainDns.Clear();
        _cfg.Save();
        RenderDomains();
    }

    private void RemoveDomain()
    {
        if (_lvDomains.SelectedIndices.Count == 0) return;
        int i = _lvDomains.SelectedIndices[0];
        if (i < 0 || i >= _cfg.InternalDomains.Count) return;
        _cfg.InternalDomains.RemoveAt(i);
        _cfg.Save();
        RenderDomains();
    }

    // ==================================================================
    // 路由操作
    // ==================================================================

    private async Task ApplyAsync()
    {
        if (_busy) return;
        CancelWait();   // 使用者自己動手了，還原就不必再等網路

        await RunWorkAsync("套用路由", async (progress, ct) =>
        {
            RouteEngine.RefreshInterfaces(_cfg);
            List<StepResult> results = await RouteEngine.ApplyAsync(_cfg, progress, ct);

            _cfg.Applied = true;
            _cfg.Save();

            int failed = results.Count(r => !r.Ok);
            return failed == 0 ? "路由已套用" : $"路由已套用，但有 {failed} 項失敗";
        });

        StartGuard();   // 套上去之後就要開始盯著它有沒有被網路變動弄壞
    }

    private async Task ClearAsync()
    {
        if (_busy) return;
        CancelWait();

        // 先停監看再清除：不然清到一半的路由表看起來就像「網路變了」，
        // 守候會回頭把我們正在拆的東西再裝一次。
        _guard?.Stop();

        await RunWorkAsync("清除路由", async (progress, ct) =>
        {
            List<StepResult> results = await RouteEngine.ClearAsync(_cfg, progress, ct);

            _cfg.Applied = false;
            _cfg.Save();

            int failed = results.Count(r => !r.Ok);
            return failed == 0 ? "路由已清除" : $"路由已清除，但有 {failed} 項需要注意";
        });
    }

    /// <summary>
    /// 開機還原：先等網路，再套用。
    ///
    /// 兩段刻意分開跑。等待可能長達好幾分鐘（USB 網路共用要等人插線、等手機解鎖），
    /// 如果整段包在 RunWorkAsync 裡，這期間視窗會一直是「忙碌」，使用者打開來
    /// 連「套用路由」都按不下去 —— 那才是真正惱人的地方。所以等待期間不算忙碌，
    /// 而且使用者一動手（套用／清除）就會把等待取消掉。
    /// </summary>
    private async Task RestoreAsync()
    {
        if (!RouteEngine.IsReady(_cfg) && !await WaitForNetworkAsync()) return;

        await RunWorkAsync("還原上次的路由", async (progress, ct) =>
        {
            bool ok = await RouteEngine.RestoreAsync(_cfg, progress, ct);
            _tray.ShowBalloonTip(4000, AppInfo.Name,
                ok ? "已還原上次套用的路由設定。" : "還原路由時發生問題，請開啟視窗查看日誌。",
                ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
            return ok ? "路由已還原" : "路由還原未完全成功";
        });
    }

    /// <summary>等網路就緒。回傳 false 表示等不到，或使用者中途自己動手了。</summary>
    private async Task<bool> WaitForNetworkAsync()
    {
        LogLine("───── 等待網路就緒 ─────", InfoColor);

        _wait = new CancellationTokenSource();
        _waitingForNetwork = true;
        RenderState();
        SetStatus("等待網路就緒…");

        try
        {
            bool ready = await RouteEngine.WaitForReadyAsync(
                _cfg, new Progress<StepResult>(AppendNote), _wait.Token);

            if (!ready)
            {
                LogLine("等不到可用的網路，還原中止。網路好了之後請自己按「套用路由」。", WarnColor);
                SetStatus("等不到網路，還原中止");
                _tray.ShowBalloonTip(4000, AppInfo.Name,
                    "等不到可用的網路，這次沒有還原路由。", ToolTipIcon.Warning);
            }
            return ready;
        }
        catch (OperationCanceledException)
        {
            LogLine("等待網路已取消。", InfoColor);
            return false;
        }
        catch (Exception ex)
        {
            LogLine("等待網路時發生錯誤：" + ex.Message, ErrColor);
            return false;
        }
        finally
        {
            _waitingForNetwork = false;
            _wait?.Dispose();
            _wait = null;
            RenderState();
        }
    }

    private void CancelWait()
    {
        try { _wait?.Cancel(); }
        catch (ObjectDisposedException) { /* 已經結束了 */ }
    }

    /// <summary>
    /// 套用／清除／還原共用的外框：鎖住按鈕、把每個步驟即時寫進日誌、
    /// 結束後解鎖並更新狀態。工作跑在背景，畫面不會整個凍住。
    /// </summary>
    private async Task RunWorkAsync(string title, Func<IProgress<StepResult>, CancellationToken, Task<string>> body)
    {
        _busy = true;
        RenderState();
        SetStatus(title + "…");
        LogLine($"───── {title} ─────", InfoColor);

        _work?.Cancel();
        _work = new CancellationTokenSource();

        var progress = new Progress<StepResult>(AppendStep);

        try
        {
            string summary = await body(progress, _work.Token);
            LogLine(summary, OkColor);
            SetStatus(summary);
        }
        catch (OperationCanceledException)
        {
            LogLine(title + "已取消", InfoColor);
            SetStatus("已取消");
        }
        catch (RouteConfigException ex)
        {
            LogLine(ex.Message, ErrColor);
            SetStatus(ex.Message);
            Warn(ex.Message);
        }
        catch (Exception ex)
        {
            LogLine("發生錯誤：" + ex.Message, ErrColor);
            SetStatus("發生錯誤");
        }
        finally
        {
            _busy = false;
            RenderState();
        }
    }

    private async Task ViewRouteTableAsync()
    {
        SetStatus("讀取路由表…");
        _log.Clear();
        LogLine("───── 目前路由表 ─────", InfoColor);
        LogLine(await RouteEngine.RouteTableAsync(), Color.Black);
        SetStatus("就緒");
    }

    private async Task ViewNrptAsync()
    {
        SetStatus("讀取 NRPT 規則…");
        LogLine("───── NRPT 規則（已設定 vs 生效中）─────", InfoColor);

        NrptStatus status = await RouteEngine.NrptStatusAsync();
        foreach (string err in status.Errors)
            LogLine($"  ✗ {err}", ErrColor);

        if (status.Entries.Count == 0 && status.Errors.Count == 0)
            LogLine("（目前沒有任何 NRPT 規則）", InfoColor);

        static string Join(List<string> s) => s.Count == 0 ? "（無）" : string.Join(", ", s);

        foreach (NrptEntry e in status.Entries)
        {
            switch (e.State)
            {
                case NrptState.Effective:
                    LogLine($"  ✓ {e.Namespace} -> {Join(e.Effective)}", OkColor);
                    break;
                case NrptState.NotLoaded:
                    LogLine($"  ⚠ {e.Namespace} -> {Join(e.Configured)}　已設定，但尚未生效", WarnColor);
                    break;
                case NrptState.NotLocal:
                    LogLine($"  ⚠ {e.Namespace} -> {Join(e.Effective)}　生效中，但不是本機設定的（可能來自群組原則）", WarnColor);
                    break;
                case NrptState.Mismatch:
                    LogLine($"  ⚠ {e.Namespace}　已設定 {Join(e.Configured)}，生效中卻是 {Join(e.Effective)}", WarnColor);
                    break;
            }
        }

        if (status.Entries.Any(e => e.State is NrptState.NotLoaded or NrptState.Mismatch))
        {
            LogLine("  DNS Client 服務還沒重新載入設定，通常稍等一下再看一次就好。"
                  + "一直沒生效的話，先拔插一次網路線；再不行，檢查是否有群組原則下發的 NRPT"
                  + "（有的話本機規則會整組被忽略）。", InfoColor);
        }
        SetStatus("就緒");
    }

    /// <summary>
    /// 清除路由會連 DHCP 原本發的預設路由一起刪掉，這是把它要回來的最快方式。
    /// </summary>
    private async Task RenewExternalAsync()
    {
        string? name = _cfg.ExternalInterface?.Name;
        if (string.IsNullOrEmpty(name))
        {
            Warn("請先指定外網介面");
            return;
        }

        SetStatus("重新取得外網 IP…");
        LogLine($"───── ipconfig /renew \"{name}\" ─────", InfoColor);

        ExecResult r = await ProcessRunner.RenewAsync(name);
        LogLine(r.Output, r.Ok ? Color.Black : ErrColor);

        await DetectAsync();
    }

    // ==================================================================
    // 設定與說明
    // ==================================================================

    private async Task ToggleAutoStartAsync()
    {
        bool want = _miAutoStart.Checked;
        ExecResult r = want ? await AutoStart.InstallAsync() : await AutoStart.UninstallAsync();

        if (r.Ok)
        {
            LogLine(want
                ? $"已設定登入時自動啟動（排程工作「{AutoStart.TaskName}」）"
                : $"已移除登入時自動啟動（排程工作「{AutoStart.TaskName}」）", OkColor);

            // 這條排程是「以最高權限執行」的，執行路徑就寫在工作定義裡。放在一般
            // 使用者寫得進去的地方，等於誰都能換掉那個檔案，讓自己的程式碼在開機時
            // 以系統管理員身分執行。只提醒不阻擋 —— 擋下來會讓人根本用不了這功能。
            if (want && !AutoStart.ExeIsInProtectedFolder)
            {
                LogLine($"提醒：exe 目前放在一般使用者可寫入的位置（{AppInfo.ExeFolder}）。", WarnColor);
                LogLine(@"      排程以最高權限執行，建議把程式搬到 C:\Program Files\SplitRoute\ 之後再重設一次自動啟動。", WarnColor);
            }
        }
        else
        {
            LogLine((want ? "設定" : "移除") + "登入自動啟動失敗：" + r.Output, ErrColor);
            _miAutoStart.Checked = !want;   // 沒成功就別讓勾勾說謊
        }
    }

    /// <summary>選單上直接寫出目前設定值，不用打開視窗就看得到會等多久。</summary>
    private void UpdateRestoreRetryText() => _miRestoreRetry.Text = _cfg.RestoreWaitForever
        ? $"開機還原等待網路（一直等，每 {_cfg.RestoreRetryDelaySeconds} 秒檢查）(&D)…"
        : $"開機還原等待網路（最多 {_cfg.RestoreMaxAttempts} 次 × {_cfg.RestoreRetryDelaySeconds} 秒）(&D)…";

    /// <summary>
    /// 調整開機還原要等網路多久。
    ///
    /// 預設是「一直等」：USB 網路共用要等使用者解鎖手機、插上線，十秒或三分鐘都正常，
    /// 沒有一個猜得準的秒數，而中途放棄換來的是 DNS／NRPT 在、路由不在的半套狀態。
    /// 真的想要「等不到就算了」的人才把它關掉，那時候才會用到次數與間隔。
    /// </summary>
    private void EditRestoreRetry()
    {
        using var dlg = new Form
        {
            Text = "開機還原等待網路",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(440, 226),
            Font = Font,
            Icon = Icon,
        };

        var chkForever = new CheckBox
        {
            Text = "一直等到網路就緒為止（建議）",
            Checked = _cfg.RestoreWaitForever,
            AutoSize = false,
            Dock = DockStyle.Fill,
        };

        var numAttempts = new NumericUpDown
        {
            Minimum = Settings.MinRestoreMaxAttempts,
            Maximum = Settings.MaxRestoreMaxAttempts,
            Value = Math.Clamp(_cfg.RestoreMaxAttempts,
                Settings.MinRestoreMaxAttempts, Settings.MaxRestoreMaxAttempts),
            Width = 80,
            TextAlign = HorizontalAlignment.Right,
        };
        var numDelay = new NumericUpDown
        {
            Minimum = Settings.MinRestoreRetryDelaySeconds,
            Maximum = Settings.MaxRestoreRetryDelaySeconds,
            Value = Math.Clamp(_cfg.RestoreRetryDelaySeconds,
                Settings.MinRestoreRetryDelaySeconds, Settings.MaxRestoreRetryDelaySeconds),
            Width = 80,
            TextAlign = HorizontalAlignment.Right,
        };

        var lblTotal = new Label { AutoSize = false, Dock = DockStyle.Fill, ForeColor = InfoColor };
        void ShowTotal()
        {
            // 一直等的時候「最多幾次」沒有意義，直接鎖住，不要讓人以為它還有作用
            numAttempts.Enabled = !chkForever.Checked;

            if (chkForever.Checked)
            {
                lblTotal.Text = "網卡一上線就會自動套用，不會因為等太久而放棄。";
                return;
            }

            // 最後一次失敗後不會再等，所以總等待是 (次數 - 1) × 秒數
            int seconds = (int)(numAttempts.Value - 1) * (int)numDelay.Value;
            lblTotal.Text = $"網路一直沒就緒時，最多等 {seconds / 60} 分 {seconds % 60} 秒才放棄。";
        }
        chkForever.CheckedChanged += (_, _) => ShowTotal();
        numAttempts.ValueChanged += (_, _) => ShowTotal();
        numDelay.ValueChanged += (_, _) => ShowTotal();
        ShowTotal();

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 5,
            Padding = new Padding(14, 12, 14, 0),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var lblIntro = new Label
        {
            Text = "登入自動還原時，網卡還沒拿到 IP 就先等著。網卡一上線會自動醒來，"
                   + "下面的間隔只是保險用的定期檢查。",
            AutoSize = false,
            Dock = DockStyle.Fill,
        };
        grid.Controls.Add(lblIntro, 0, 0);
        grid.SetColumnSpan(lblIntro, 3);
        grid.Controls.Add(chkForever, 0, 1);
        grid.SetColumnSpan(chkForever, 3);
        grid.Controls.Add(MakeCaption("最多檢查"), 0, 2);
        grid.Controls.Add(numAttempts, 1, 2);
        grid.Controls.Add(MakeCaption("次"), 2, 2);
        grid.Controls.Add(MakeCaption("檢查間隔"), 0, 3);
        grid.Controls.Add(numDelay, 1, 3);
        grid.Controls.Add(MakeCaption("秒"), 2, 3);
        grid.Controls.Add(lblTotal, 0, 4);
        grid.SetColumnSpan(lblTotal, 3);
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var btnOk = MakeButton("確定", () => { dlg.DialogResult = DialogResult.OK; dlg.Close(); });
        var btnCancel = MakeButton("取消", () => { dlg.DialogResult = DialogResult.Cancel; dlg.Close(); });
        var btnReset = MakeButton("回到預設", () =>
        {
            chkForever.Checked = true;
            numAttempts.Value = Settings.DefaultRestoreMaxAttempts;
            numDelay.Value = Settings.DefaultRestoreRetryDelaySeconds;
        }, 96);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 44,
            Padding = new Padding(14, 8, 14, 8),
        };
        buttons.Controls.AddRange(new Control[] { btnCancel, btnOk, btnReset });

        // Fill 的先加、Bottom 的後加，後加的才會排在外側
        dlg.Controls.Add(grid);
        dlg.Controls.Add(buttons);
        dlg.AcceptButton = btnOk;
        dlg.CancelButton = btnCancel;

        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        _cfg.RestoreWaitForever = chkForever.Checked;
        _cfg.RestoreMaxAttempts = (int)numAttempts.Value;
        _cfg.RestoreRetryDelaySeconds = (int)numDelay.Value;
        _cfg.Save();
        UpdateRestoreRetryText();
        LogLine(
            _cfg.RestoreWaitForever
                ? $"開機還原已設為一直等到網路就緒（每 {_cfg.RestoreRetryDelaySeconds} 秒檢查一次）"
                : $"開機還原已設為最多檢查 {_cfg.RestoreMaxAttempts} 次、每次隔 {_cfg.RestoreRetryDelaySeconds} 秒",
            OkColor);
    }

    private void ImportSettings()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "匯入設定（可直接選 MyRoute 的 config.json）",
            Filter = "設定檔 (*.json)|*.json|所有檔案 (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        Settings? imported = Settings.Import(dlg.FileName);
        if (imported == null)
        {
            Warn("讀不到這個設定檔，或格式不正確。");
            return;
        }

        _cfg.ExternalInterface = imported.ExternalInterface;
        _cfg.InternalInterface = imported.InternalInterface;
        _cfg.InternalRanges = imported.InternalRanges;
        _cfg.InternalDnsServers = imported.InternalDnsServers;
        _cfg.InternalDomains = imported.InternalDomains;
        _cfg.Save();

        LogLine("已匯入設定：" + dlg.FileName, OkColor);
        _ = DetectAsync();
    }

    private static void OpenSettingsFolder()
    {
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            Process.Start(new ProcessStartInfo(Settings.Folder) { UseShellExecute = true });
        }
        catch { /* 開不起來就算了，路徑在「關於」裡看得到 */ }
    }

    private void OpenManual()
    {
        string path = Path.Combine(AppInfo.ExeFolder, "使用說明.md");
        if (!File.Exists(path))
        {
            Warn("找不到使用說明.md，它應該和 " + AppInfo.Name + ".exe 放在一起。");
            return;
        }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Warn("開啟使用說明失敗：" + ex.Message); }
    }

    private void ShowAbout()
    {
        MessageBox.Show(this,
            $"{AppInfo.Name} {AppInfo.Version}\n{AppInfo.Tagline}\n\n"
            + $"執行檔：{AppInfo.ExePath}\n"
            + $"設定檔：{Settings.FilePath}\n\n"
            + "由 MyRoute（Node.js + 網頁介面）改寫而成。",
            "關於 " + AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ==================================================================
    // 日誌與狀態
    // ==================================================================

    private void AppendStep(StepResult step)
    {
        LogLine((step.Ok ? "  ✓ " : "  ✗ ") + step.Label, step.Ok ? OkColor : ErrColor);
        if (!string.IsNullOrWhiteSpace(step.Output))
            foreach (string line in step.Output.Split('\n'))
                if (line.Trim().Length > 0)
                    LogLine("      " + line.TrimEnd(), step.Ok ? InfoColor : ErrColor);
    }

    /// <summary>
    /// 等待期間的訊息不加 ✓／✗。那兩個符號的意思是「這一步做完了」，拿來標「還在等」
    /// 等於在說謊 —— 舊版每 5 秒印一個綠色的 ✓ 說「網路尚未就緒」，正是要修掉的東西。
    /// </summary>
    private void AppendNote(StepResult step)
    {
        LogLine("  " + step.Label, step.Ok ? InfoColor : WarnColor);
        if (!string.IsNullOrWhiteSpace(step.Output))
            foreach (string line in step.Output.Split('\n'))
                if (line.Trim().Length > 0)
                    LogLine("      " + line.TrimEnd(), InfoColor);
    }

    private void LogLine(string text, Color color)
    {
        _log.SelectionStart = _log.TextLength;
        _log.SelectionLength = 0;
        _log.SelectionColor = color;
        _log.AppendText(text + Environment.NewLine);
        _log.SelectionColor = _log.ForeColor;
        _log.ScrollToCaret();
    }

    private void SetStatus(string text) => _lblStatus.Text = text;

    private void Warn(string message)
        => MessageBox.Show(this, message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
}
