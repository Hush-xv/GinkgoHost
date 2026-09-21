using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using GinkgoHost.Native;

namespace GinkgoHost.Models;

/// <summary>一条总线事务记录，同时驱动日志面板和 CSV 导出。</summary>
/// <param name="Dir">方向：RX=读 TX=写 SYS=系统事件，用于方向配色。</param>
public sealed record LogEntry(DateTime Ts, string Dir, string Op, string Addr, int Ret, double Ms, byte[]? Data)
{
    // 展示属性被 WPF 双绑定（Text + ToolTip）、排序与 CSV 导出反复读取，一律懒计算一次。
    private string? _time;
    private string? _retText;
    private string? _hex;
    private string? _hexGrouped;
    private string? _dataDisplay;

    public string Time => _time ??= Ts.ToString("HH:mm:ss.fff");
    public bool Ok => Ret == 0;
    public string RetText => _retText ??= Ret == 0 ? "OK" : $"ERR {Ret}";
    public string Hex => _hex ??= Data is { Length: > 0 } ? Convert.ToHexString(Data) : string.Empty;
    // 工程惯例：FF FF FF 空格分隔；进制由列名与输入框标注，不在每条记录加 0x 前缀
    public string HexGrouped => _hexGrouped ??= Data is not { Length: > 0 }
        ? "—"
        : string.Join(" ", Convert.ToHexString(Data).Chunk(2).Select(c => new string(c)));
    // SYS 事件的 Data 约定携带 UTF-8 人类可读详情（如 CH1 · 400 kHz、命中地址列表）。
    // 含控制字节时（历史路径可能塞原始地址字节）回退 hex 展示，不显示乱码菱形。
    public string DataDisplay => _dataDisplay ??= DecodeDisplay();

    private string DecodeDisplay()
    {
        if (Dir != "SYS" || Data is not { Length: > 0 }) return HexGrouped;
        string text = System.Text.Encoding.UTF8.GetString(Data);
        bool printable = !text.Any(char.IsControl);
        return printable ? text : HexGrouped;
    }
}

public static class LogCollectionExtensions
{
    // 环形上限：超出丢最旧记录，防止长时间监控内存无限增长
    public const int Cap = 5000;

    public static void AddCapped(this ObservableCollection<LogEntry> log, LogEntry e)
    {
        log.Add(e);
        if (log.Count > Cap)
            log.RemoveAt(0);
    }
}

/// <summary>
/// 应用主事务流使用的分批淘汰集合。数据上限不变，但不再在满容量后为每条记录发送一次 Remove 通知。
/// </summary>
public sealed class CappedLogCollection : ObservableCollection<LogEntry>
{
    private const int TrimBatch = 128;
#if DEBUG
    private int _trimCount;
#endif

    public void AddCapped(LogEntry entry)
    {
        Add(entry);
        if (Count <= LogCollectionExtensions.Cap) return;

        int removeCount = Math.Min(TrimBatch, Count);
        for (int i = 0; i < removeCount; i++) Items.RemoveAt(0);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
#if DEBUG
        _trimCount++;
        if ((_trimCount & 0x0F) == 1)
            Dbg.Log($"CappedLogCollection.AddCapped: batchTrim={removeCount} retained={Count} trims={_trimCount}");
#endif
    }
}
