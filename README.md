# SplitRoute

雙網卡分流路由管理工具。內網流量（公司網段）走網路線，其餘流量走另一張網卡（USB 網路共用、行動網路），
順便把內網 DNS 與特定 domain 的 NRPT 規則一起設好。

由 **MyRoute**（Node.js + Express + 網頁介面）改寫成 WinForms。

## 為什麼改寫

MyRoute 需要 Node.js、要開 3456 埠、要開瀏覽器、還要記得用系統管理員身分啟動，
而它做的事其實只是「呼叫 `route.exe` 和幾個 PowerShell cmdlet」。改成單一 exe 之後：

| | MyRoute | SplitRoute |
|---|---|---|
| 執行環境 | 要裝 Node.js + `npm install` | 單一 exe，自封裝 .NET 10 |
| 提權 | 靠 `start.bat` 自己 `Start-Process -Verb RunAs` | manifest 直接要求，雙擊就跳 UAC |
| 介面 | 起 HTTP 伺服器 + 開瀏覽器 | 原生視窗 |
| 列舉網卡 | 每次開 powershell.exe 跑 `Get-NetIPConfiguration` 剖析文字（約 1 秒） | `System.Net.NetworkInformation` 直接讀 |
| DNS / NRPT | 一條規則開一個 powershell.exe | 全部包成一段腳本，一次跑完 |
| 開機還原 | Node 服務常駐 | 系統匣常駐 + 登入排程 |
| 設定檔 | exe 旁 `config.json` | `%APPDATA%\SplitRoute\settings.json`（**格式相容，首次啟動自動匯入舊檔**）|

改寫時順手修掉的三個問題：

1. **PowerShell 字串內插** — 舊版把 domain 直接嵌進 `'${ns}'` 組指令。現在要傳給腳本的值一律走
   環境變數（`$env:SR_PLAN`），腳本端 `ConvertFrom-Json` 讀，沒有可以被跳脫出去的地方。
2. **`route.exe` 輸出編碼** — 舊版硬用 UTF-8 解 CP950 的輸出，中文訊息全是亂碼，只好把輸出當「有字就算資訊」。
   現在先問 `GetOEMCP()` 拿系統字碼頁再解，錯誤訊息看得懂了，也才敢用 exit code 判斷成敗。
3. **清除路由會刪掉 DHCP 的預設路由** — `route delete 0.0.0.0 mask 0.0.0.0 <gw>` 會把指向該閘道的
   **所有**預設路由刪掉，包含 DHCP 原本發的那條，清完可能整台機器對外不通而畫面上看不出來。
   現在清除後會自我檢查，沒有預設路由就在日誌明講，並提供「工具 → 重新取得外網 IP」把它要回來。

## 建置

需要 **.NET 10 SDK**（`winget install Microsoft.DotNet.SDK.10`）。

```
dotnet build -c Release
dotnet publish -c Release
```

`publish` 產出單一自封裝 exe（約 70MB，已開壓縮），發佈給別人只要給 `SplitRoute.exe` 和 `使用說明.md`。

## 架構

沒有 designer 檔，介面全部在程式碼裡建（與 RevPacker、SchemaDiff 一致）。

| 檔案 | 負責 |
|---|---|
| `Program.cs` | `AppInfo`（版本／exe 路徑）、單一實例、`/tray` 參數 |
| `Settings.cs` | 設定模型與讀寫；欄位名刻意與 MyRoute `config.json` 相同，舊檔直接反序列化就能匯入 |
| `NetInfo.cs` | 列舉網卡、CIDR ↔ 遮罩、on-link 判斷、NRPT namespace 正規化 |
| `ProcessRunner.cs` | 跑 `route.exe` / `ipconfig` / `schtasks` / PowerShell，處理編碼與參數傳遞 |
| `RouteEngine.cs` | 套用／清除／開機還原的實際流程，以及路由表與 NRPT 查詢 |
| `AutoStart.cs` | 登入自動啟動（`schtasks`，`/rl highest`） |
| `MainForm.cs` | 視窗、系統匣、日誌 |

### 幾個容易忘記的點

* **`route add` 沒有加 `-p`**，重開機路由就沒了；但 DNS 與 NRPT 是寫進登錄檔的，重開機還在。
  兩者一不同步就會「名稱解析得到但連不上」。所以上次狀態是 `applied` 時，登入後要自己補回來
  （`RouteEngine.RestoreAsync`）。
* **等網路就緒是事件驅動的，不是固定次數的輪詢**（`NetworkWatcher`）。USB 網路共用要等使用者
  解鎖手機、插上線，沒有一個猜得準的秒數，所以聽 `NetworkAddressChanged`，網卡一上線兩秒內就醒，
  而且預設不設放棄時限 —— 反正程式本來就常駐系統匣。
* **外網只能走外網網卡**：套用時內網網卡設 `IgnoreDefaultRoutes = Enabled`（IPv4／IPv6、持久），
  否則外網網卡一拔，內網 DHCP 的預設路由就接手。清除時改回來並 `ipconfig /renew` 把預設路由要回來。
  內網預設路由被擋之後 `GatewayAddresses` 看不到內網閘道，所以 `NetInfo` 會改讀登錄檔裡 DHCP 給的閘道。
* **套用之後 `RouteGuard` 會留在背景看著網路**，閘道換掉或外網網卡拔插之後自動重套。
* **`route.exe` 不管成敗都回傳 exit code 0**，成敗只寫在輸出文字裡（而且是 OEM 字碼頁的中文），
  所以 `RouteEngine.Classify` 是看內容判斷的。
* **偵測到的閘道只有 on-link 時才採用**。Windows 會把我們自己加的預設路由算回某張網卡頭上，
  於是那張網卡會回報一個它根本走不到的閘道；照單全收會用不通的位址覆蓋掉好好的設定。
* **`/tray` 啟動時視窗不顯示，`Load` 事件不會發生**，所以啟動流程是從 `SetVisibleCore` 排下去的。
* **`SplitContainer` 的 `SplitterDistance` / `Panel*MinSize` 是拿目前寬度驗證的**，
  要先給 `Size` 再設，否則建構當場丟例外（而且會表現成「程式按了沒反應」）。
* **高 DPI 設定寫在 `Program.Main` 的 `Application.SetHighDpiMode`**，不是 `app.manifest` ——
  寫在 manifest 會被 WinForms 以 `WFO0003` 警告要求搬走。

## 沒有的東西

沒有測試、沒有 lint、沒有 CI。這是自用小工具，維持和 RevPacker / SchemaDiff 一樣的輕量。
