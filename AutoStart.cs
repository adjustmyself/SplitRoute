using System.Security;
using System.Security.Principal;
using System.Text;

namespace SplitRoute;

/// <summary>
/// 「登入時自動啟動」。
///
/// 用排程工作而不是登錄檔的 Run 機碼，是因為這支程式的 manifest 要求系統管理員權限：
/// 從 Run 機碼啟動每次登入都會跳一次 UAC，等於沒有自動可言。排程工作可以指定
/// 「以最高權限執行」，登入時直接安靜地起來。
///
/// 註冊走 XML（<c>schtasks /create /xml</c>）而不是命令列參數，是因為
/// <c>schtasks /create /sc onlogon</c> 建出來的工作帶著兩個會讓它「安靜地不執行」
/// 的預設值，而命令列**沒有參數可以關掉它們**：
///
/// <list type="bullet">
///   <item>DisallowStartIfOnBatteries = true —— 筆電沒插電開機就不會啟動。</item>
///   <item>ExecutionTimeLimit = P3D —— 常駐系統匣的程式會在第 3 天被排程器砍掉。</item>
/// </list>
///
/// 兩個都只有 XML（或 PowerShell 的 ScheduledTasks 模組，但那要多開一個 powershell.exe）
/// 設得到。**不要為了「少一個暫存檔」把這裡改回 /sc onlogon。**
/// </summary>
internal static class AutoStart
{
    public const string TaskName = "SplitRoute";

    /// <summary>排程啟動時帶的參數：直接縮在系統匣，不要跳視窗出來擋人。</summary>
    public const string TrayArgument = "/tray";

    /// <summary>
    /// schtasks /create /xml 只吃 UTF-16。餵它 UTF-8（連帶 BOM 也一樣）會得到
    /// 「工作 XML 包含的值格式不正確或超出範圍」—— 錯誤訊息完全不會提到編碼，
    /// 會讓人以為是 XML 內容寫錯，白查很久。
    /// </summary>
    private static readonly Encoding TaskXmlEncoding = new UnicodeEncoding(false, true);

    public static async Task<bool> IsInstalledAsync()
    {
        ExecResult r = await ProcessRunner.SchTasksAsync("/query", "/tn", TaskName).ConfigureAwait(false);
        return r.Ok;
    }

    public static async Task<ExecResult> InstallAsync()
    {
        // 先移除舊的，避免改過 exe 位置之後留著一條指向舊路徑的排程
        await ProcessRunner.SchTasksAsync("/delete", "/tn", TaskName, "/f").ConfigureAwait(false);

        string tmp = Path.Combine(
            Path.GetTempPath(), "splitroute_task_" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            await File.WriteAllTextAsync(tmp, BuildTaskXml(), TaskXmlEncoding).ConfigureAwait(false);

            return await ProcessRunner
                .SchTasksAsync("/create", "/tn", TaskName, "/xml", tmp, "/f")
                .ConfigureAwait(false);
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

    public static Task<ExecResult> UninstallAsync()
        => ProcessRunner.SchTasksAsync("/delete", "/tn", TaskName, "/f");

    // ------------------------------------------------------------------

    /// <summary>
    /// exe 是不是放在一般使用者寫不進去的地方。
    ///
    /// 這條排程是「以最高權限執行」的，執行路徑寫死在工作定義裡。如果那個路徑
    /// 一般使用者寫得進去（桌面、D:\ 的專案資料夾……），任何能寫該檔案的帳號
    /// 就等於能讓自己的程式碼以系統管理員身分執行 —— 自己在機器上開了一個
    /// 本機提權的後門。所以裝好之後要提醒使用者把 exe 搬到 Program Files。
    /// </summary>
    public static bool ExeIsInProtectedFolder
    {
        get
        {
            string exe = AppInfo.ExePath;
            foreach (Environment.SpecialFolder f in new[]
                     {
                         Environment.SpecialFolder.ProgramFiles,
                         Environment.SpecialFolder.ProgramFilesX86,
                         Environment.SpecialFolder.Windows,
                     })
            {
                string root = Environment.GetFolderPath(f);
                if (!string.IsNullOrEmpty(root) &&
                    exe.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// 工作定義。元素順序是照 schema 的 sequence 排的，**不能任意調動** ——
    /// 順序錯了 schtasks 一樣只回「格式不正確」那句沒有指向性的錯誤。
    /// </summary>
    private static string BuildTaskXml()
    {
        // 用 SID 而不是「電腦名稱\帳號」：電腦改名或帳號改名之後，寫死的名稱會對不上，
        // 而 SID 不會變。
        //
        // 註：程式本身是提權執行的，若是「以其他系統管理員身分執行」提權上來的，
        // 這裡拿到的會是那個管理員而不是坐在電腦前的人。這台機器的使用者自己就是
        // 系統管理員（UAC 只是同意，不換帳號），所以不特別處理。
        string sid = WindowsIdentity.GetCurrent().User?.Value
                     ?? WindowsIdentity.GetCurrent().Name;

        // 路徑理論上不會有 & 或 <，但 XML 沒有「參數化傳值」這種東西，
        // 唯一的辦法就是跳脫。這裡是把值放進標記語言，和 PowerShell 那邊
        // 一律走環境變數是同一個理由。
        string command = SecurityElement.Escape(AppInfo.ExePath) ?? string.Empty;

        return $"""
            <?xml version="1.0" encoding="utf-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Author>SplitRoute</Author>
                <Description>登入時自動啟動 SplitRoute（縮在系統匣），並還原上次套用的分流路由。</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{sid}</UserId>
                  <Delay>PT20S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{sid}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{command}</Command>
                  <Arguments>{TrayArgument}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }
}
