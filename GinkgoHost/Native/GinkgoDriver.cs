using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

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

    /// <summary>按 DevIndex 读 BoardInfo（自动适配两参/三参导出）。</summary>
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
        return System.Text.Encoding.ASCII.GetString(bytes, 0, end).Trim();
    }

    public static string Hex(byte[] bytes) => Convert.ToHexString(bytes);

    [DllImport("Ginkgo_Driver.dll")]
    public static extern int VII_WriteBytes(int DevType, int DevIndex, int I2CIndex, ushort Addr, uint SubAddr, byte[] pWriteData, ushort Len);

    [DllImport("Ginkgo_Driver.dll")]
    public static extern int VII_ReadBytes(int DevType, int DevIndex, int I2CIndex, ushort Addr, uint SubAddr, byte[] pReadData, ushort Len);

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
        _ => $"ERROR_{ret}"
    };
}

/// <summary>
/// 运行时文件日志：%APPDATA%\GinkgoHost\logs\GinkgoHost_yyyyMMdd.log。
/// 按天滚动，保留 7 天；每行即写即刷，崩溃不丢末尾；IO 异常一律吞掉，日志永不影响主流程。
/// </summary>
public static class Dbg
{
    static readonly object _lock = new();
    static string _cleanedDate = "";

    public static string LogDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GinkgoHost", "logs");

    public static void Log(string msg)
    {
        try
        {
            var now = DateTime.Now;
            string date = now.ToString("yyyyMMdd");
            lock (_lock)
            {
                Directory.CreateDirectory(LogDir);
                if (_cleanedDate != date)
                {
                    _cleanedDate = date;
                    foreach (string f in Directory.GetFiles(LogDir, "GinkgoHost_*.log"))
                        if (File.GetLastWriteTime(f) < now.AddDays(-7))
                            File.Delete(f);
                }
                File.AppendAllText(Path.Combine(LogDir, $"GinkgoHost_{date}.log"),
                    $"[{now:HH:mm:ss.fff}] {msg}\r\n");
            }
        }
        catch { /* 日志失败不影响主流程 */ }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr ShellExecuteW(IntPtr hwnd, string verb, string file, string args, string dir, int showCmd);

    /// <summary>在资源管理器中打开日志目录（ShellExecute 交给系统解析，无命令行拼接）。</summary>
    public static void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            ShellExecuteW(IntPtr.Zero, "open", LogDir, "", "", 1 /* SW_SHOWNORMAL */);
        }
        catch { /* 打开失败不影响主流程 */ }
    }
}
