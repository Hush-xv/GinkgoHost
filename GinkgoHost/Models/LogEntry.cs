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
    // 工程惯例：FF FF FF 空格分隔；进制由列名与输入框标注，不在每条记录加 0x 前缀
    public string HexGrouped => Data is not { Length: > 0 }
        ? "—"
        : string.Join(" ", Convert.ToHexString(Data).Chunk(2).Select(c => new string(c)));
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
