namespace GinkgoHost.Models;

/// <summary>
/// 事务日志的派生计数：总数、成功/失败、方向分布、末尾连续失败区间，以及各筛选值的命中条数。
/// 从 LogPanel 抽出来有两个原因：筛选谓词与命中计数必须共用同一处定义（分成两份表达式迟早漂移），
/// 而这段增量维护是日志面板热路径的正确性所在，留在 WPF 控件里就没法在无 Dispatcher 的环境下验证。
/// 只在 UI 线程上访问，不做线程安全处理。
/// </summary>
public sealed class LogCounters
{
    public const string FilterRead = "read";
    public const string FilterWrite = "write";
    public const string FilterSystem = "system";
    public const string FilterError = "error";

    public int Total { get; private set; }
    public int OkCount { get; private set; }
    public int ErrCount { get; private set; }
    public int RxCount { get; private set; }
    public int TxCount { get; private set; }
    public int SysCount { get; private set; }

    /// <summary>日志末尾连续失败的距离，驱动「连续失败 N」提示。</summary>
    public int ConsecutiveFailures { get; private set; }
    public string? LatestFailure { get; private set; }

    /// <summary>记录是否属于该筛选值。视图谓词与命中计数都走这里。</summary>
    public static bool Matches(string filter, LogEntry entry) => filter switch
    {
        FilterRead => entry.Dir == "RX",
        FilterWrite => entry.Dir == "TX",
        FilterSystem => entry.Dir == "SYS",
        FilterError => !entry.Ok,
        _ => true,
    };

    /// <summary>某筛选值下的可见条数，O(1)。</summary>
    public int CountFor(string filter) => filter switch
    {
        FilterRead => RxCount,
        FilterWrite => TxCount,
        FilterSystem => SysCount,
        FilterError => ErrCount,
        _ => Total,
    };

    public void Add(LogEntry entry)
    {
        Total++;
        CountDirection(entry, +1);
        if (entry.Ok)
        {
            OkCount++;
            // 连续区间被打断，区间内的「最近错误」一并失效；不清会让增量路径与
            // RecomputeTail 对同一份日志给出不同表示（界面只在 CF>0 时读，所以平时看不出来）。
            ConsecutiveFailures = 0;
            LatestFailure = null;
        }
        else
        {
            ErrCount++;
            ConsecutiveFailures++;
            LatestFailure = entry.RetText;
        }
    }

    /// <summary>
    /// 回退一条记录。方向与成败计数按条目本身回退，不依赖位置；
    /// 这次删除是否碰到了末尾连续失败区间由调用方按索引判断，再用 <see cref="RecomputeTail"/> 重建。
    /// </summary>
    public void Remove(LogEntry entry)
    {
        Total--;
        CountDirection(entry, -1);
        if (entry.Ok) OkCount--;
        else ErrCount--;
    }

    public void Clear()
    {
        Total = OkCount = ErrCount = RxCount = TxCount = SysCount = 0;
        ConsecutiveFailures = 0;
        LatestFailure = null;
    }

    /// <summary>全表重算。只用于初始化和集合被整体重置这类确实需要重建的场合。</summary>
    public void Rebuild(IEnumerable<LogEntry> log)
    {
        Clear();
        foreach (LogEntry entry in log) Add(entry);
    }

    /// <summary>重扫末尾连续失败区间。删除碰到该区间时由调用方触发。</summary>
    public void RecomputeTail(IEnumerable<LogEntry> log)
    {
        ConsecutiveFailures = 0;
        LatestFailure = null;
        foreach (LogEntry entry in log)
        {
            if (entry.Ok)
            {
                ConsecutiveFailures = 0;
                LatestFailure = null;
            }
            else
            {
                ConsecutiveFailures++;
                LatestFailure = entry.RetText;
            }
        }
    }

    void CountDirection(LogEntry entry, int delta)
    {
        switch (entry.Dir)
        {
            case "RX": RxCount += delta; break;
            case "TX": TxCount += delta; break;
            case "SYS": SysCount += delta; break;
        }
    }
}
