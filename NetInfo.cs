using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Win32;

namespace SplitRoute;

/// <summary>畫面上看到的一張網卡。</summary>
internal sealed class NetAdapter
{
    public int Index;
    public string Name = "";
    public string Description = "";
    public string Ip = "";
    public string Gateway = "";
    public int PrefixLength;

    /// <summary>
    /// 這張網卡回報的閘道是不是真的在它自己的網段上。
    /// Windows 的路由表會把我們自己加的預設路由算回某張網卡頭上，
    /// 於是一張網卡可能「回報」一個它根本連不到的閘道 —— 那種值不能拿來套用路由。
    /// </summary>
    public bool GatewayOnLink => NetInfo.SameSubnet(Ip, Gateway, PrefixLength);

    /// <summary>下拉選單顯示用。</summary>
    public override string ToString()
    {
        string gw = string.IsNullOrEmpty(Gateway) ? "無" : Gateway;
        return $"{Name} ({(string.IsNullOrEmpty(Ip) ? "?" : Ip)}) [GW: {gw}]";
    }
}

internal static class NetInfo
{
    /// <summary>
    /// 列出目前有 IPv4 位址的網卡。
    ///
    /// 舊版 MyRoute 是開一個 powershell.exe 跑 Get-NetIPConfiguration 再剖析文字，
    /// 每次重新偵測都要等將近一秒。這裡改用 System.Net.NetworkInformation 直接讀，
    /// 是同一份系統資料，但不用起行程，也不用處理 Big5 輸出。
    /// </summary>
    public static List<NetAdapter> List()
    {
        var result = new List<NetAdapter>();

        foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (ni.OperationalStatus != OperationalStatus.Up) continue;

            IPInterfaceProperties props;
            IPv4InterfaceProperties v4;
            try
            {
                props = ni.GetIPProperties();
                v4 = props.GetIPv4Properties();   // 純 IPv6 的介面會在這裡丟例外
            }
            catch { continue; }

            if (v4 == null) continue;

            UnicastIPAddressInformation? uni = props.UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
            if (uni == null) continue;

            string ip = uni.Address.ToString();
            int prefix = PrefixOf(uni);

            List<string> fromRoutes = props.GatewayAddresses
                .Select(g => g.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any))
                .Select(a => a.ToString())
                .ToList();

            // 路由表推出來的閘道優先；不在同網段（或根本沒有）時，改用網卡設定裡的閘道。
            // 不在同網段的值還是留著當最後手段 —— 畫面要能顯示「（非同網段，不採用）」。
            string gw = fromRoutes.Concat(ConfiguredGateways(ni.Id))
                            .FirstOrDefault(g => SameSubnet(ip, g, prefix))
                        ?? fromRoutes.FirstOrDefault()
                        ?? "";

            result.Add(new NetAdapter
            {
                Index = v4.Index,
                Name = ni.Name,
                Description = ni.Description,
                Ip = ip,
                Gateway = gw,
                PrefixLength = prefix,
            });
        }

        return result.OrderBy(a => a.Index).ToList();
    }

    /// <summary>
    /// 網卡「設定上」的閘道：DHCP 發下來的，或手動填的固定閘道。
    ///
    /// <see cref="IPInterfaceProperties.GatewayAddresses"/> 是從路由表推出來的，不是網卡設定。
    /// 套用路由時我們會讓內網網卡忽略預設路由（外網只能走外網網卡），它的預設路由一拿掉，
    /// GatewayAddresses 就再也看不到內網閘道 —— 內網 DHCP 換了閘道也就無從得知。
    /// 所以退一步直接讀 Tcpip 在登錄檔裡記的值，它不受路由表影響。
    /// </summary>
    private static IEnumerable<string> ConfiguredGateways(string interfaceId)
    {
        string[] found = [];
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + interfaceId);
            if (key == null) return found;

            // 從 DHCP 改成固定 IP 之後，DhcpDefaultGateway 會留著舊值，所以看 EnableDHCP 決定讀哪個
            bool dhcp = key.GetValue("EnableDHCP") is int e && e != 0;
            object? value = key.GetValue(dhcp ? "DhcpDefaultGateway" : "DefaultGateway");

            found = value switch
            {
                string[] many => many,
                string one => [one],
                _ => [],
            };
        }
        catch { /* 讀不到就只靠路由表 */ }

        return found.Where(IsValidIp).Where(g => g != "0.0.0.0");
    }

    /// <summary>
    /// 遮罩優先、PrefixLength 次之：Windows 上 IPv4Mask 一定有值，
    /// 而 PrefixLength 在某些虛擬介面上會回報 0。
    /// </summary>
    private static int PrefixOf(UnicastIPAddressInformation uni)
    {
        try
        {
            if (uni.IPv4Mask != null && !uni.IPv4Mask.Equals(IPAddress.Any))
            {
                uint mask = ToUInt(uni.IPv4Mask.ToString()) ?? 0;
                int bits = 0;
                while (mask != 0) { bits += (int)(mask & 1); mask >>= 1; }
                return bits;
            }
        }
        catch { /* 落到下面用 PrefixLength */ }

        try { return uni.PrefixLength; } catch { return 0; }
    }

    // ------------------------------------------------------------------
    // IP 計算
    // ------------------------------------------------------------------

    public static uint? ToUInt(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return null;
        string[] parts = ip.Split('.');
        if (parts.Length != 4) return null;

        uint n = 0;
        foreach (string p in parts)
        {
            if (!int.TryParse(p, out int v) || v < 0 || v > 255) return null;
            n = (n << 8) | (uint)v;
        }
        return n;
    }

    /// <summary>閘道是否可以從 ip/prefix 直接走到（同網段、on-link）。</summary>
    public static bool SameSubnet(string? ip, string? gateway, int prefixLength)
    {
        uint? a = ToUInt(ip);
        uint? b = ToUInt(gateway);
        if (a == null || b == null) return false;
        if (prefixLength < 1 || prefixLength > 32) return false;

        uint mask = prefixLength == 32 ? uint.MaxValue : ~(uint.MaxValue >> prefixLength);
        return (a.Value & mask) == (b.Value & mask);
    }

    public static string CidrToMask(int bits)
    {
        if (bits <= 0) return "0.0.0.0";
        if (bits > 32) bits = 32;
        uint mask = bits == 32 ? uint.MaxValue : ~(uint.MaxValue >> bits);
        return $"{(mask >> 24) & 0xff}.{(mask >> 16) & 0xff}.{(mask >> 8) & 0xff}.{mask & 0xff}";
    }

    /// <summary>把 "10.0.0.0/8" 拆成目的地與遮罩。沒帶 /n 時視為 /32。</summary>
    public static bool TryParseCidr(string? range, out string destination, out string mask)
    {
        destination = "";
        mask = "";
        if (string.IsNullOrWhiteSpace(range)) return false;

        string[] parts = range.Trim().Split('/');
        if (ToUInt(parts[0]) == null) return false;

        int bits = 32;
        if (parts.Length > 1 && (!int.TryParse(parts[1], out bits) || bits < 0 || bits > 32)) return false;

        destination = parts[0];
        mask = CidrToMask(bits);
        return true;
    }

    public static bool IsValidIp(string? text) => ToUInt(text) != null;

    public static bool IsValidCidr(string? text) => TryParseCidr(text, out _, out _)
                                                    && (text ?? "").Contains('/');

    /// <summary>
    /// NRPT 的 namespace 要以 "." 開頭才會比對子網域。
    /// 順便把使用者貼上整串網址的情況（https://a.b.c/path）清乾淨。
    /// </summary>
    public static string NormalizeNamespace(string? ns)
    {
        if (string.IsNullOrWhiteSpace(ns)) return "";
        string s = ns.Trim().ToLowerInvariant();

        int scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) s = s[(scheme + 3)..];
        int slash = s.IndexOf('/');
        if (slash >= 0) s = s[..slash];
        s = s.Trim();

        if (s.Length == 0) return "";
        return s.StartsWith('.') ? s : "." + s;
    }

    /// <summary>目前路由表裡是否還有預設路由（0.0.0.0/0）。清除路由後要拿來自我檢查。</summary>
    public static bool HasDefaultRoute()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up)
                .Any(ni => ni.GetIPProperties().GatewayAddresses
                    .Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                              && !g.Address.Equals(IPAddress.Any)));
        }
        catch { return true; }   // 讀不到就別亂警告
    }
}
