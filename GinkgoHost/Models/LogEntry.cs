using System.Collections.ObjectModel;

namespace GinkgoHost.Models;

/// <summary>一条总线事务记录，同时驱动日志面板和 CSV 导出。</summary>
/// <param name="Dir">方向：RX=读 TX=写 SYS=系统事件，用于方向配色。</param>
public sealed record LogEntry(DateTime Ts, string Dir, string Op, string Addr, int Ret, double Ms, byte[]? Data)
{
    public string Time => Ts.ToString("HH:mm:ss.fff");
    public bool Ok => Ret == 0;
    public string RetText => Ret == 0 ? "OK" : $"ERR {Ret}";
    public string Hex => Data is { Length: > 0 } ? Convert.ToHexString(Data) : string.Empty;
    // Mission Control 事务格式：0xFF.DE.AD 分组
    public string HexGrouped => Data is not { Length: > 0 }
        ? "—"
        : "0x" + string.Join(".", Convert.ToHexString(Data).Chunk(2).Select(c => new string(c)));
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
