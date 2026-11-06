using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SplitRoute;

/// <summary>一次外部指令的結果。Output 已經把 stdout 與 stderr 合併。</summary>
internal readonly record struct ExecResult(bool Ok, string Output);

internal static class ProcessRunner
{
    /// <summary>
    /// route.exe 用系統的 OEM 字碼頁輸出（正體中文是 950），不是 UTF-8。
    /// 舊版 MyRoute 硬用 UTF-8 解，中文訊息都是亂碼，所以那邊只好把輸出當成
    /// 「有字就當資訊」草草帶過。這裡先問系統實際的字碼頁再解，訊息才看得懂。
    /// </summary>
    // 用 DllImport 而不是新版的 LibraryImport：後者的來源產生器要求整個專案開
    // AllowUnsafeBlocks，為了一支無參數、回傳 uint 的 API 不值得。
    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    private static readonly Lazy<Encoding> ConsoleEncoding = new(() =>
    {
        // .NET Core 起預設只帶 UTF-8 系列，CP950 要先註冊才拿得到
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try { return Encoding.GetEncoding((int)GetOEMCP()); }
        catch { return Encoding.UTF8; }
    });

    private static string System32(string exe) => Path.Combine(Environment.SystemDirectory, exe);

    private static string PowerShellExe => Path.Combine(
        Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    // ------------------------------------------------------------------

    private static async Task<ExecResult> RunAsync(
        string exe,
        IEnumerable<string> args,
        Encoding encoding,
        IDictionary<string, string>? environment = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        if (environment != null)
            foreach (var kv in environment) psi.Environment[kv.Key] = kv.Value;

        try
        {
            using var proc = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            string text = (stdout.ToString() + stderr).Trim();
            return new ExecResult(proc.ExitCode == 0, text);
        }
        catch (Exception ex)
        {
            return new ExecResult(false, ex.Message);
        }
    }

    // ------------------------------------------------------------------
    // route.exe
    // ------------------------------------------------------------------

    /// <summary>
    /// 跑 route.exe。參數逐項傳（不是拼成一整條命令列），少一層被路徑或
    /// 奇怪字元咬到的機會。
    /// </summary>
    public static Task<ExecResult> RouteAsync(params string[] args)
        => RunAsync(System32("route.exe"), args, ConsoleEncoding.Value);

    /// <summary>route print 的完整輸出，給「檢視路由表」用。</summary>
    public static async Task<string> RoutePrintAsync()
    {
        ExecResult r = await RouteAsync("print", "-4").ConfigureAwait(false);
        return r.Output;
    }

    /// <summary>ipconfig /renew：清除路由後外網預設路由不見時，用它把 DHCP 的那條要回來。</summary>
    public static Task<ExecResult> RenewAsync(string adapterName)
        => RunAsync(System32("ipconfig.exe"), new[] { "/renew", adapterName }, ConsoleEncoding.Value);

    /// <summary>schtasks.exe，用來裝／移除「登入時自動啟動」的排程工作。</summary>
    public static Task<ExecResult> SchTasksAsync(params string[] args)
        => RunAsync(System32("schtasks.exe"), args, ConsoleEncoding.Value);

    // ------------------------------------------------------------------
    // PowerShell
    // ------------------------------------------------------------------

    /// <summary>
    /// 跑一段 PowerShell。
    ///
    /// 要傳給腳本的值一律走環境變數（腳本裡用 $env:XXX 讀），**不做字串內插**：
    /// 舊版是把 domain 直接嵌進 '...' 裡組指令，只要值裡有一個單引號就能讓
    /// 後面接的東西被當成指令執行。走環境變數就完全沒有這個面。
    ///
    /// 另外 powershell.exe 每次啟動要花將近一秒，所以呼叫端請盡量把多件事
    /// 包成同一段腳本一次做完，不要一條規則開一個行程。
    /// </summary>
    public static async Task<ExecResult> PowerShellAsync(
        string script,
        IDictionary<string, string>? values = null,
        CancellationToken ct = default)
    {
        string tmp = Path.Combine(Path.GetTempPath(), "splitroute_" + Guid.NewGuid().ToString("N") + ".ps1");
        try
        {
            // BOM 讓 powershell.exe 確定以 UTF-8 讀，中文 domain 才不會變成亂碼
            await File.WriteAllTextAsync(tmp, script, new UTF8Encoding(true), ct).ConfigureAwait(false);

            return await RunAsync(
                PowerShellExe,
                new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", tmp },
                Encoding.UTF8,
                values,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new ExecResult(false, ex.Message);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* 暫存檔刪不掉不影響結果 */ }
        }
    }
}
