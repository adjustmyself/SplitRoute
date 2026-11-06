# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

SplitRoute 是 Windows 雙網卡分流路由管理工具（WinForms）。內網網段走網路線、其餘流量走另一張網卡
（USB 網路共用等），同時設定內網 DNS 與特定 domain 的 NRPT 規則。介面與所有使用者可見字串都是正體中文（zh-TW）。

由 `..\MyRoute`（Node.js + Express + 網頁介面）改寫而成。舊專案還在，可以拿來對照行為。

## 建置與執行

```bash
dotnet build -c Release
dotnet publish -c Release    # 產出單一自封裝 exe，約 47MB
```

需要 **.NET 10 SDK**。目標 `net10.0-windows`，`win-x64` 自封裝 + `PublishSingleFile`
—— 這工具是複製 exe 給同事用的，不能要求對方先裝 runtime。

`app.manifest` 要求 `requireAdministrator`，所以**執行時一定會跳 UAC**。這代表：

* 從非提權的 shell 用 `Start-Process` 啟動要加 `-Verb RunAs`。
* 提權中的行程，非提權的 shell **殺不掉**（Access denied），也不能用 `PrintWindow` 截圖（UIPI 擋跨完整性等級的訊息）。
* 程式在跑的時候 `publish\SplitRoute.exe` 被鎖住，`dotnet publish` 會失敗 —— 要先關掉它。

## 架構

沒有 designer 檔，介面全部在 `MainForm.cs` 裡用程式碼建（與 `..\RevPacker`、`..\SchemaDiff` 一致）。

| 檔案 | 負責 |
|---|---|
| `Program.cs` | `AppInfo`、單一實例（Mutex + EventWaitHandle 叫回既有視窗）、`/tray` 參數 |
| `Settings.cs` | 設定模型與讀寫 |
| `NetInfo.cs` | 列舉網卡、CIDR ↔ 遮罩、on-link 判斷、NRPT namespace 正規化 |
| `ProcessRunner.cs` | 跑 `route.exe` / `ipconfig` / `schtasks` / PowerShell |
| `RouteEngine.cs` | 套用／清除／開機還原流程，路由表與 NRPT 查詢 |
| `NetworkWatcher.cs` | 把 `NetworkChange` 事件變成「可以 await 的訊號」＋ 慢速保險輪詢 |
| `RouteGuard.cs` | 套用後在背景監看網路，閘道換掉／外網拔插就通知重套 |
| `AutoStart.cs` | 登入自動啟動（`schtasks`，`/rl highest`） |
| `MainForm.cs` | 視窗、系統匣、日誌 |

## 動手前一定要知道的陷阱

以下每一條都是實際踩過才發現的，改動相關程式碼時請先看完。

### WinForms

* **`SplitContainer` 的 `SplitterDistance` / `Panel1MinSize` / `Panel2MinSize` 是拿「目前寬度」驗證的。**
  控制項剛 new 出來只有預設 150 寬，直接在物件初始化器裡設 560 會當場丟例外。
  必須**先給 `Size`，再設這三個值，最後才 `Dock = Fill`**。
  症狀很難認：例外被 `Program.Main` 的 try/catch 接住，使用者只看到「雙擊沒反應」。

* **停駐順序：Fill 的控制項要「先」加進 Controls，Top/Bottom 的後加。**
  後加的 Top/Bottom 會排在更外側。三個清單群組的「輸入列在最下面」就是靠這個順序。

* **`/tray` 啟動時視窗永遠不顯示，`Load` 事件就不會發生。**
  所以啟動流程（`StartupAsync`）是從 `SetVisibleCore` 用 `BeginInvoke` 排下去的，
  並用 `_startedUp` 旗標保證只跑一次。改啟動流程時不要退回只掛 `Load`，
  否則登入自動啟動最重要的那件事（還原路由）會靜靜地不執行。

* **高 DPI 設定寫在 `Program.Main` 的 `Application.SetHighDpiMode(PerMonitorV2)`，不是 `app.manifest`。**
  寫進 manifest 會被 WinForms 以 `WFO0003` 警告要求搬走。

* **`LibraryImport` 需要整個專案開 `AllowUnsafeBlocks`。**
  `ProcessRunner` 只為了一支 `GetOEMCP()` 用 `DllImport`，不值得為它開 unsafe。

### 網路邏輯

* **`route add` 沒有加 `-p`，重開機路由就沒了；但 DNS 與 NRPT 是寫進登錄檔的，重開機還在。**
  兩者一不同步就是「內網名稱解析得到但連不上」這種很難查的症狀。
  所以 `routeStatus == "applied"` 時，登入後要由 `RouteEngine.RestoreAsync` 自己補回來。
  **不要為了「簡化」把這段拿掉。**

* **等網路就緒是事件驅動的（`NetworkWatcher`），不是固定次數的輪詢。**
  USB 網路共用要等使用者解鎖手機、插上線，十秒或三分鐘都正常，固定次數對這件事沒有正確答案 ——
  調短了不夠等，調長了只是把同一句「尚未就緒」洗更久，而**中途放棄換來的正是
  「DNS／NRPT 在、路由不在」那個一開始就要避免的半套狀態**。
  所以聽 `NetworkAddressChanged`（網卡一上線兩秒內就醒，去抖 2 秒等閘道也到位），
  `restoreRetryDelaySeconds` 降級成保險輪詢的間隔，而且預設 `restoreWaitForever = true`
  不設時限 —— 程式本來就常駐系統匣，等下去不花什麼成本。
  `restoreMaxAttempts` 只有在使用者把「一直等」關掉時才用得到。

* **等待期間不能讓視窗變成「忙碌」。** 可能要等使用者走過去插線，幾分鐘都有可能；
  包在 `RunWorkAsync` 裡的話他打開視窗連「套用路由」都按不下去。所以 `MainForm.RestoreAsync`
  把「等網路」（`WaitForNetworkAsync`，`_waitingForNetwork`）和「套用」（`RunWorkAsync`）分成兩段，
  而且使用者一按套用／清除就 `CancelWait()`。

* **`RouteGuard` 只在 `Applied` 時動作。** 使用者按過「清除路由」之後，我們記憶中的狀態
  不代表他要的狀態，這時候把路由偷偷加回去是最惹人厭的行為。清除前也要先 `Stop()`，
  否則清到一半的路由表看起來就像「網路變了」，守候會回頭把正在拆的東西再裝一次。

* **套用時內網網卡被設成 `IgnoreDefaultRoutes = Enabled`（IPv4、IPv6 都設），並刪掉它 ActiveStore 的預設路由。**
  目標是「外網只能走外網網卡」：只靠 metric 讓外網預設路由優先是不夠的，USB 一拔，
  我們那條跟著網卡消失，內網 DHCP 發的那條就接手，外網流量悄悄改從內網出去。
  只 `route delete` 也不夠，DHCP 續約會加回來。這個設定**刻意是持久的**（開機到登入還原之間
  有空窗，不持久就漏），所以「清除路由」一定要把它改回 `Disabled`，而且改回去之後預設路由
  不會自己回來 —— `ClearSystemAsync` 會 `ipconfig /renew`（DHCP）或從 PersistentStore 抄回
  （固定 IP）。**只刪 ActiveStore**：PersistentStore 裡的是使用者手設的固定閘道，刪了還原不回來。
  沒按清除就把 exe 刪掉，內網網卡會一直沒有預設路由，手動還原：
  `Set-NetIPInterface -InterfaceIndex <n> -IgnoreDefaultRoutes Disabled` 再 `ipconfig /renew`。

* **內網預設路由被擋掉之後，`GatewayAddresses` 就看不到內網閘道了**（它是從路由表推的，不是網卡設定）。
  所以 `NetInfo.List` 在路由表的閘道不在同網段或沒有時，改讀登錄檔
  `Tcpip\Parameters\Interfaces\{GUID}` 的 `DhcpDefaultGateway`（`EnableDHCP=1`）或 `DefaultGateway`。
  看 `EnableDHCP` 決定讀哪個：改成固定 IP 之後 `DhcpDefaultGateway` 會留著舊值。

* **偵測到的閘道只有 on-link（同網段）時才採用。**
  Windows 會把我們自己加的預設路由算回某張網卡頭上，於是那張網卡會回報一個它根本走不到的閘道。
  照單全收會用不通的位址覆蓋掉好好的設定 —— `NetInfo.SameSubnet` 就是為了擋這個。

* **USB 網路共用／DHCP 每次重連給的閘道都不一樣**，設定檔存的值過夜就過期，
  所以每次套用前都要先 `RefreshInterfaces`。

* **`route delete 0.0.0.0 mask 0.0.0.0 <gw>` 會刪掉指向該閘道的「所有」預設路由**，
  包含 DHCP 原本發的那條。清完可能整台機器對外不通而畫面上看不出來，
  所以 `ClearAsync` 最後會用 `NetInfo.HasDefaultRoute()` 自我檢查並在日誌明講。

### 外部行程

* **傳給 PowerShell 的值一律走環境變數（`$env:SR_PLAN`，內容是 JSON），不做字串內插。**
  舊版 MyRoute 是把 domain 直接嵌進 `'${ns}'` 組指令。改回內插等於把注入面加回來。

* **`powershell.exe` 每次啟動要花將近一秒**，所以 DNS + 全部 NRPT 規則包成同一段腳本一次跑完，
  腳本以 `OK|訊息` / `ERR|訊息` 逐行回報。不要退回「一條規則開一個行程」。

* **`route.exe` 不管成功失敗都回傳 exit code 0**，成敗只寫在輸出文字裡。
  只看 `ExecResult.Ok` 的話，「路由新增失敗: 物件已經存在。」會被記成一個綠色的 ✓ —— 日誌開始說謊。
  判斷在 `RouteEngine.Classify`，中英文都要比對（英文版 Windows 會整組換成英文），
  而且**順序不能調**：失敗訊息本身就含「失敗／failed」，要先認出「物件已經存在」與
  「找不到元素」這兩種有意義的情況。`route add` 撞到已存在時改用
  `route change … if <index>` 把 metric 調成我們要的（DHCP 發的預設路由 metric 通常比我們大，
  留著等於我們指定的優先順序沒生效）；`if` 一定要帶，不然 route.exe 會自己挑一條同目的地的路由改。

* **`route.exe` 用系統 OEM 字碼頁輸出（正體中文是 950），不是 UTF-8。**
  `ProcessRunner` 先問 `GetOEMCP()` 再解碼。.NET Core 起預設不帶 CP950，
  要先 `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)`。

* **自動啟動的排程一定要用 `schtasks /create /xml` 註冊，不能用 `/sc onlogon` 那組命令列參數。**
  `schtasks` 從命令列建出來的工作帶兩個會讓它安靜地不執行的預設值，而命令列沒有參數關得掉：
  `DisallowStartIfOnBatteries=true`（筆電沒插電開機就不啟動）與 `ExecutionTimeLimit=P3D`
  （常駐系統匣的程式第 3 天會被排程器砍掉）。
  登入觸發程序帶 `<Delay>PT20S</Delay>`（在 `<UserId>` 之後，順序照 schema）：
  剛登入那幾十秒磁碟與 CPU 都在搶，晚一點起來反而穩，反正等網路是事件驅動的，晚起不影響結果。
  另外 **XML 檔要存成 UTF-16**：餵 UTF-8 只會得到「工作 XML 包含的值格式不正確或超出範圍」，
  訊息完全不提編碼，會讓人往 XML 內容去查。工作定義裡的元素順序也要照 schema 的 sequence，
  順序錯了是同一句錯誤訊息。

* **手動在工作排程器裡建這條工作很容易建錯**：觸發程序選「在系統啟動時」配上互動式的執行身分，
  工作會永遠等不到能跑的時機（`LastTaskResult` 停在 `0x41303`「工作尚未執行」）——
  系統啟動那一刻沒有人登入。要的是「當使用者登入時」。

### 設定檔

* 存在 `%APPDATA%\SplitRoute\settings.json`，**欄位名稱刻意與舊版 MyRoute 的 `config.json` 完全一致**
  （包含 `routeStatus` 是字串 `"applied"` / `"cleared"`），舊檔直接反序列化就能匯入。
  改欄位名會破壞這條路。

* **寫檔要帶 BOM**（`new UTF8Encoding(true)`）。內容是正確 UTF-8，但無 BOM 時
  PowerShell 5.1 的 `Get-Content` 和舊版記事本會用系統字碼頁解，中文網卡名稱看起來像亂碼。

* `FindLegacyConfig` 從 exe 位置往上找 8 層。**層數不能減**：發行版 exe 在
  `bin\Release\net10.0-windows\win-x64\publish\`，離「MyRoute 的隔壁」有七層，
  6 層就剛好搆不到。

* 單一檔案發行時 `Assembly.Location` 是空字串，取 exe 路徑一律用 `Environment.ProcessPath`
  （`AppInfo.ExePath`）—— 排程工作要靠它填執行路徑。

## 沒有的東西

沒有測試、沒有 lint、沒有 CI。這是自用小工具，維持和 RevPacker / SchemaDiff 一樣的輕量。
