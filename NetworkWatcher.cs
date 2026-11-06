using System.Net.NetworkInformation;

namespace SplitRoute;

/// <summary>
/// 把 Windows 的網路變化事件變成一個「可以 await 的訊號」。
///
/// 開機還原最麻煩的地方是不知道要等多久：USB 網路共用可能要等使用者解鎖手機，
/// 十秒或三分鐘都有可能。固定次數的輪詢對這件事沒有正確答案 —— 調短了不夠等，
/// 調長了只是把同一句「尚未就緒」洗更久。改成聽 NetworkAddressChanged，網卡一
/// 拿到位址就在兩秒內醒過來，等再久也不必把間隔調大。
///
/// 事件不是唯一的醒來理由：另外還留一個慢速的保險輪詢，防的是事件沒發出來
/// （某些虛擬網卡，或位址早就在、只有閘道晚到）的情況。<see cref="NetInfo.List"/>
/// 走的是 System.Net.NetworkInformation 不開行程，所以這個輪詢幾乎沒有成本。
/// </summary>
internal sealed class NetworkWatcher : IDisposable
{
    /// <summary>
    /// 事件是一陣一陣來的（位址先到、閘道後到、IPv6 再來一輪），收到第一個之後
    /// 要等它安靜下來再回報，否則會在「有 IP 但還沒有閘道」的半就緒狀態下白跑一次。
    /// </summary>
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromSeconds(2);

    // maxCount 1：訊號的意思只是「剛剛有事發生」，重複的沒有資訊，累積起來反而
    // 會讓後面幾輪立刻醒來空轉。滿了就丟掉。
    private readonly SemaphoreSlim _signal = new(0, 1);
    private volatile bool _disposed;

    public NetworkWatcher()
    {
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
    }

    private void OnAddressChanged(object? sender, EventArgs e) => Pulse();

    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => Pulse();

    private void Pulse()
    {
        if (_disposed) return;
        try { _signal.Release(); }
        catch (SemaphoreFullException) { /* 已經有一個沒被領走的訊號，夠了 */ }
        catch (ObjectDisposedException) { /* 正在收尾 */ }
    }

    /// <summary>
    /// 等到網路有變化，或等滿 <paramref name="pollInterval"/>（保險輪詢）為止。
    /// 醒來之後「算不算好了」由呼叫端自己判斷 —— 這裡不知道什麼叫就緒。
    /// </summary>
    public async Task WaitForChangeAsync(
        TimeSpan pollInterval, TimeSpan debounce, CancellationToken ct = default)
    {
        // 逾時（保險輪詢）就直接回去重新判斷，不必去抖：這段期間本來就夠安靜了。
        if (!await _signal.WaitAsync(pollInterval, ct).ConfigureAwait(false)) return;

        await Task.Delay(debounce, ct).ConfigureAwait(false);

        // 去抖期間又進來的訊號要吃掉，否則下一輪會立刻醒來空轉一次
        await _signal.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 一定要退訂：NetworkChange 是靜態事件，常駐系統匣的程式漏掉這一步，
        // 每開一次 watcher 就多留一個永遠不會被回收的 handler。
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
        _signal.Dispose();
    }
}
