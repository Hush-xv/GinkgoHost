using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace GinkgoHost.Native;

/// <summary>
/// ViewTool Ginkgo_Driver.dll P/Invoke 封装。
/// 签名来源：官方 SDK C# 例程 ControlI2C.cs（驱动 v2.0.3.x）。
/// Addr 参数为 8 位格式（7 位地址 << 1），与 AT24C02 例程传 0xA0 一致。
/// </summary>
public static class GinkgoDriver
{
    public const int VII_USBI2C = 1;

    public const byte VII_MASTER = 1;
    public const byte VII_SLAVE = 0;
    public const byte VII_ADDR_7BIT = 7;
    public const byte VII_HCTL_MODE = 1;      // 标准硬件 I2C 模式
    public const byte VII_SCTL_MODE = 2;      // 软件 I2C（GPIO 模拟，通道 0–7，时序约 100 kHz 级）
    public const byte VII_SUB_ADDR_NONE = 0;  // 无子地址（原始读写/扫描用）
    public const byte VII_SUB_ADDR_1BYTE = 1;
    public const byte VII_SUB_ADDR_2BYTE = 2; // 16 位寄存器地址器件

    public const int SUCCESS = 0;

    /// <summary>从机模拟页持有设备期间置位：I2cService.ConnectAsync 见此标志一律拒绝，
    /// 避免主机/从机两条会话同时打开同一适配器。</summary>
    public static volatile bool SlaveSessionActive;

    [StructLayout(LayoutKind.Sequential)]
    public struct VII_INIT_CONFIG
    {
        public byte MasterMode;    // 0-从机 1-主机
        public byte ControlMode;   // 1-标准模式
        public byte AddrType;      // 7-7 位地址
        public byte SubAddrWidth;  // 0~4，0=无子地址
        public ushort Addr;        // 从机模式下本机地址
        public uint ClockSpeed;    // 时钟频率 Hz
    }

    [DllImport("Ginkgo_Driver.dll")]
    public static extern int VII_ScanDevice(byte NeedInit);

    [DllImport("Ginkgo_Driver.dll")]
    public static extern int VII_OpenDevice(int DevType, int DevIndex, int Reserved);

    [DllImport("Ginkgo_Driver.dll")]
    public static extern int VII_CloseDevice(int DevType, int DevIndex);

    [DllImport("Ginkgo_Driver.dll")]
    public static extern int VII_InitI2C(int DevType, int DevIndex, int I2CIndex, ref VII_INIT_CONFIG pInitConfig);

    [StructLayout(LayoutKind.Sequential)]
    public struct VII_TIME_CONFIG
    {
        public ushort tHD_STA;
        public ushort tSU_STA;
        public ushort tLOW;
        public ushort tHIGH;
        public ushort tSU_DAT;
        public ushort tSU_STO;
        public ushort tBuf;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public byte[] tACK;
        public ushort tStart;
        public ushort tStop;
    }

    [DllImport("Ginkgo_Driver.dll")]
    public static extern int VII_TimeConfig(int DevType, int DevIndex, int I2CIndex, ref VII_TIME_CONFIG pTimeConfig);

    /// <summary>适配器身份信息（官方 VII_BOARD_INFO）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct VII_BOARD_INFO
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] ProductName;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public byte[] FirmwareVersion;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public byte[] HardwareVersion;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 12)] public byte[] SerialNumber;
    }

    /// <summary>读取适配器身份（型号/固件/硬件版本/序列号）。DevIndex 从 0 起。</summary>
    [DllImport("Ginkgo_Driver.dll")]
    public static extern int VII_ReadBoardInfo(int DevType, int DevIndex, ref VII_BOARD_INFO pBoardInfo);

    /// <summary>两参变体（部分固件按此签名导出，DevIndex, ref BoardInfo）。</summary>
    [DllImport("Ginkgo_Driver.dll", EntryPoint = "VII_ReadBoardInfo")]
    private static extern int VII_ReadBoardInfo2(int DevIndex, ref VII_BOARD_INFO pBoardInfo);

    /// <summary>按 DevIndex 读 BoardInfo（自动适配两参/三参导出）。
    /// 注意：v2.0.3.6 DLL 只导出一个 VII_ReadBoardInfo，实测为两参形态；三参声明指向同一
    /// 导出、按 x64 寄存器传参多余实参被忽略（无栈损坏），仅作换驱动版本的兜底。</summary>
    public static int ReadBoardInfo(int devIndex, ref VII_BOARD_INFO info)
    {
        int ret = VII_ReadBoardInfo2(devIndex, ref info);
        if (ret == -8) // 部分版本仅导出三参形态
        {
            var info3 = new VII_BOARD_INFO
            {
                ProductName = new byte[32],
                FirmwareVersion = new byte[4],
                HardwareVersion = new byte[4],
                SerialNumber = new byte[12]
            };
            ret = VII_ReadBoardInfo(VII_USBI2C, devIndex, ref info3);
            info = info3;
        }
        return ret;
    }

    /// <summary>把定长字节数组转成截至 NUL 的 ASCII 文本。</summary>
    public static string Ascii(byte[] bytes)
    {
        int end = Array.IndexOf(bytes, (byte)0);
        if (end < 0) end = bytes.Length;
        string text = System.Text.Encoding.ASCII.GetString(bytes, 0, end);
        // 部分固件会在定长序列号尾部留控制字节；UI 身份字段只显示可打印 ASCII。
        return string.Concat(text.Where(c => c is >= ' ' and <= '~')).Trim();
    }

    public static string Hex(byte[] bytes) => Convert.ToHexString(bytes);

    [DllImport("Ginkgo_Driver.dll")]
    public static extern int VII_WriteBytes(int DevType, int DevIndex, int I2CIndex, ushort Addr, uint SubAddr, byte[] pWriteData, ushort Len);

    [DllImport("Ginkgo_Driver.dll")]
    public static extern int VII_ReadBytes(int DevType, int DevIndex, int I2CIndex, ushort Addr, uint SubAddr, byte[] pReadData, ushort Len);

    // ── I²C 从机（InitI2C 用 MasterMode=VII_SLAVE + Addr=本机 8 位地址后生效）──
    // 签名经真机探针确认：无数据时 SlaveReadBytes 返回 -7 (READ_NO_DATA)。

    /// <summary>取出外部主机写入的数据。无数据返回 -7，pRetLen 收实际字节数。</summary>
    [DllImport("Ginkgo_Driver.dll")]
    public static extern int VII_SlaveReadBytes(int DevType, int DevIndex, int I2CIndex, byte[] pReadData, int Len, out int RetLen);

    /// <summary>预载主机读取本机时返回的数据。</summary>
    [DllImport("Ginkgo_Driver.dll")]
    public static extern int VII_SlaveWriteBytes(int DevType, int DevIndex, int I2CIndex, byte[] pWriteData, int Len);

    /// <summary>驱动错误码转可读文本，仅覆盖常见值。</summary>
    public static string ErrorName(int ret) => ret switch
    {
        0 => "SUCCESS",
        -1 => "PARAMETER_NULL",
        -4 => "INPUT_DATA_ILLEGALITY",
        -7 => "READ_NO_DATA",
        -8 => "OPEN_DEVICE_FAILD",
        -10 => "EXECUTE_CMD_FAILD",
        -13 => "DEVICE_NOTOPEN",
        -15 => "DEVICE_NOTEXIST",
        -20 => "SLAVE_SESSION_ACTIVE",
        _ => $"ERROR_{ret}"
    };
}

/// <summary>
/// 运行时文件日志：%APPDATA%\GinkgoHost\logs\GinkgoHost_yyyyMMdd.log。
/// 按天滚动，保留 7 天。调用方只做一次 TryAdd，落盘由后台泵线程按批完成——
/// 周期读写路径每条事务都会写两三行，同步刷盘会把文件 I/O 压到 UI 线程的 await 续接点上。
/// 队列有界，满则丢弃并计数，绝不阻塞业务线程；IO 异常一律吞掉，日志永不影响主流程。
/// 未处理异常与应用退出前用 Flush/Shutdown 把队尾落盘，保留崩溃现场。
/// </summary>
public static class Dbg
{
    /// <summary>待写条目。Line 为 null 的是 Flush 屏障，只负责刷盘并放行等待方。</summary>
    sealed record Item(string? Line, ManualResetEventSlim? Barrier);

    const int QueueCapacity = 100_000;

    static readonly BlockingCollection<Item> _queue = new(new ConcurrentQueue<Item>(), QueueCapacity);
    static readonly Thread _pump;
    static volatile bool _pumpDead; // 泵线程已退出或不可用，Flush 直接放弃等待

    static string _cleanedDate = "";
    static StreamWriter? _file;
    static string _fileDate = "";
    static int _dropped;

    static Dbg()
    {
        _pump = new Thread(PumpLoop) { IsBackground = true, Name = "GinkgoHost.DbgLog" };
        _pump.Start();
    }

    public static string LogDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GinkgoHost", "logs");

    public static void Log(string msg)
    {
        if (_pumpDead) return;
        try
        {
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
            // 超时 0：满了就丢，业务线程一次系统调用都不付出
            if (!_queue.TryAdd(new Item(line, null), TimeSpan.Zero))
                Interlocked.Increment(ref _dropped);
        }
        catch { /* 队列已完成或极端异常：日志失败不影响主流程 */ }
    }

    /// <summary>把队尾日志刷到磁盘。未处理异常和退出路径用；超时即返回，不拖住进程退出。</summary>
    public static void Flush(TimeSpan? timeout = null)
    {
        if (_pumpDead) return;
        try
        {
            var done = new ManualResetEventSlim(false);
            if (!_queue.TryAdd(new Item(null, done), TimeSpan.FromSeconds(1))) return;
            done.Wait(timeout ?? TimeSpan.FromSeconds(2));
            // 故意不 Dispose：超时返回后泵线程仍会 Set() 同一个屏障。
            // 此处只用 Wait(int) 快路径，不访问 WaitHandle，因此没有内核句柄需要释放。
        }
        catch { /* 关闭竞态：日志已尽力落盘 */ }
    }

    /// <summary>停止接收、排空并关闭写入端。仅在应用 OnExit 调用。</summary>
    public static void Shutdown()
    {
        Flush(TimeSpan.FromSeconds(3));
        if (_pumpDead) return;
        try { _queue.CompleteAdding(); }
        catch { return; }
        _pump.Join(TimeSpan.FromSeconds(2));
    }

    /// <summary>泵线程：取一条后把当前可用的一批全部落盘，再统一 Flush 一次文件。</summary>
    static void PumpLoop()
    {
        try
        {
            foreach (Item first in _queue.GetConsumingEnumerable())
            {
                Handle(first);
                while (_queue.TryTake(out Item? next) && next is not null) Handle(next);
                try { _file?.Flush(); } catch { }
            }
        }
        catch { /* 意外异常：与正常排空走同一条收尾路径，别把写入端连同句柄留在原地 */ }

        _pumpDead = true;
        try { _file?.Flush(); } catch { }
        CloseFile();
    }

    static void Handle(Item item)
    {
        if (item.Line is not null)
        {
            try
            {
                EnsureWriter();
                _file!.WriteLine(item.Line);
            }
            catch
            {
                // 单次 IO 故障（磁盘满/文件被占用）不该终结整个进程的日志：
                // 丢掉这个写入端，下一条日志会经 EnsureWriter 重新打开，故障消失即自愈。
                CloseFile();
                return;
            }
        }

        if (item.Barrier is null) return;
        // 屏障：先把文件刷干净再放行，否则调用方可能在对端仍在缓冲时返回
        try { _file?.Flush(); } catch { }
        item.Barrier.Set();
    }

    static void CloseFile()
    {
        try { _file?.Dispose(); } catch { }
        _file = null;
        _fileDate = "";
    }

    /// <summary>按需创建/滚动当日写入端；跨天时先清理过期日志再换文件。仅在泵线程上调用。</summary>
    static void EnsureWriter()
    {
        DateTime now = DateTime.Now;
        string date = now.ToString("yyyyMMdd");
        if (_file is not null && _fileDate == date)
        {
            ReportDropped();
            return;
        }
        Directory.CreateDirectory(LogDir);
        if (_cleanedDate != date)
        {
            _cleanedDate = date;
            foreach (string f in Directory.GetFiles(LogDir, "GinkgoHost_*.log"))
                if (File.GetLastWriteTime(f) < now.AddDays(-7))
                    File.Delete(f);
        }
        _file?.Dispose();
        // FileShare.ReadWrite：写入端会活一整个进程周期，默认的 FileShare.Read 会让用户
        // 在程序运行时打不开当日日志。单实例互斥锁保证只有本进程在追加，放开共享写不会撞车。
        var stream = new FileStream(Path.Combine(LogDir, $"GinkgoHost_{date}.log"),
            FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _file = new StreamWriter(stream);
        _fileDate = date;
        Interlocked.Exchange(ref _dropped, 0); // 新文件重新计数
    }

    /// <summary>队列溢出过的行数只在下一行前提示一次，避免丢弃变成静默的数据缺失。</summary>
    static void ReportDropped()
    {
        int n = Interlocked.Exchange(ref _dropped, 0);
        if (n > 0) _file!.WriteLine($"[log] 队列已满，丢弃 {n} 行");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr ShellExecuteW(IntPtr hwnd, string verb, string file, string args, string dir, int showCmd);

    /// <summary>在资源管理器中打开日志目录（ShellExecute 交给系统解析，无命令行拼接）。</summary>
    public static bool OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            Flush(); // 让刚产生的日志在资源管理器里立刻可见
            return ShellExecuteW(IntPtr.Zero, "open", LogDir, "", "", 1 /* SW_SHOWNORMAL */).ToInt64() > 32;
        }
        catch { return false; /* 打开失败不影响主流程 */ }
    }

    /// <summary>用系统默认浏览器打开 URL（调用方保证地址为编译期常量，不接用户/API 输入）。</summary>
    public static bool OpenUrl(string url)
    {
        try
        {
            return ShellExecuteW(IntPtr.Zero, "open", url, "", "", 1 /* SW_SHOWNORMAL */).ToInt64() > 32;
        }
        catch { return false; }
    }
}
