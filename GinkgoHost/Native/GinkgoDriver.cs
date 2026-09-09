using System.Diagnostics;
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

/// <summary>调试日志。DEBUG 构建输出到 DebugView/IDE，Release 零开销。</summary>
public static class Dbg
{
    [Conditional("DEBUG")]
    public static void Log(string msg) =>
        Debug.WriteLine($"[GinkgoHost {DateTime.Now:HH:mm:ss.fff}] {msg}");
}
