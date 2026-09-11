using System.Runtime.InteropServices;

namespace GinkgoHost.Native;

/// <summary>
/// Total Phase Aardvark USB-I2C/SPI 适配器 P/Invoke 封装（I²C 主机子集）。
/// 签名来源：官方 SDK aardvark-api-windows-x86_64-v6.00/csharp/aardvark.cs（Total Phase, Inc.）。
/// 依赖 libs/x64/aardvark.dll 随程序分发；进程必须 x64（与 Ginkgo_Driver.dll 要求一致）。
/// 注意：v6.00 的 aardvark.dll 是 CPython+C 混合模块，C# 可用导出带 net_aa_ 前缀
/// （另有 c_aa_* 供 C 调用、PyInit_aardvark 供 Python 导入）。
/// </summary>
public static class AardvarkI2c
{
    public const int AA_OK = 0;

    /// <summary>aa_configure 的 I²C 配置值（AA_CONFIG_I2C_MASK）。</summary>
    public const int AA_CONFIG_I2C = 2;
    /// <summary>目标供电：关（板载上拉/供电由 aa_i2c_pullup 单独控制）。</summary>
    public const byte TargetPowerNone = 0x00;
    /// <summary>I²C 上拉：SDA+SCL 都启用（AA_I2C_PULLUP_BOTH）。</summary>
    public const byte PullupBoth = 0x03;

    // ── 原生入口（v6.00 DLL 的 .NET 接口导出带 net_aa_ 前缀）──

    [DllImport("aardvark", EntryPoint = "net_aa_find_devices")]
    private static extern int aa_find_devices(int num, [Out] ushort[] ports);

    [DllImport("aardvark", EntryPoint = "net_aa_open")]
    private static extern int aa_open(int port);

    [DllImport("aardvark", EntryPoint = "net_aa_close")]
    private static extern int aa_close(int handle);

    [DllImport("aardvark", EntryPoint = "net_aa_configure")]
    private static extern int aa_configure(int handle, int config);

    [DllImport("aardvark", EntryPoint = "net_aa_i2c_bitrate")]
    private static extern int aa_i2c_bitrate(int handle, int kHz);

    [DllImport("aardvark", EntryPoint = "net_aa_i2c_pullup")]
    private static extern int aa_i2c_pullup(int handle, byte mask);

    [DllImport("aardvark", EntryPoint = "net_aa_target_power")]
    private static extern int aa_target_power(int handle, byte mask);

    [DllImport("aardvark", EntryPoint = "net_aa_i2c_free_bus")]
    private static extern int aa_i2c_free_bus(int handle);

    [DllImport("aardvark", EntryPoint = "net_aa_i2c_write")]
    private static extern int aa_i2c_write(int handle, ushort addr8, byte[] data, ushort len);

    [DllImport("aardvark", EntryPoint = "net_aa_i2c_read")]
    private static extern int aa_i2c_read(int handle, ushort addr8, ushort len, [Out] byte[] data);

    [DllImport("aardvark", EntryPoint = "net_aa_status_string")]
    private static extern IntPtr aa_status_string(int status);

    [DllImport("aardvark", EntryPoint = "net_aa_sleep_ms")]
    private static extern uint aa_sleep_ms(uint ms);

    // ── 项目风格包装 ──

    /// <summary>枚举在线的 Aardvark 端口号。无需硬件也能调用（返回空数组）。占用中的端口（OR 0x8000）被过滤。</summary>
    public static int[] FindDevices()
    {
        const int capacity = 16;
        var ports = new ushort[capacity];
        int found = aa_find_devices(capacity, ports); // 返回值 = 设备数；负值才是错误
        if (found < 0) return [];
        var result = new List<int>(found);
        for (int i = 0; i < found; i++)
            if ((ports[i] & 0x8000) == 0) result.Add(ports[i]); // 排除 AA_PORT_NOT_FREE
        return [.. result];
    }

    /// <summary>打开端口并配置为 I²C 模式（含上拉启用）。返回句柄；负值为错误码。</summary>
    public static int Open(int port, int bitrateKHz)
    {
        int handle = aa_open(port);
        if (handle < 0) return handle;
        int ret = aa_configure(handle, AA_CONFIG_I2C);
        if (ret != AA_OK) { aa_close(handle); return ret; }
        ret = aa_i2c_pullup(handle, PullupBoth);
        if (ret != AA_OK) Dbg.Log($"AardvarkI2c.Open: pullup ret={ret}");
        ret = aa_i2c_bitrate(handle, bitrateKHz);
        if (ret != AA_OK) { aa_close(handle); return ret; }
        return handle;
    }

    public static int Close(int handle) => aa_close(handle);

    public static int SetBitrate(int handle, int kHz) => aa_i2c_bitrate(handle, kHz);

    /// <summary>释放总线（对从机 NACK 后恢复总线有用）。</summary>
    public static int FreeBus(int handle) => aa_i2c_free_bus(handle);

    /// <summary>
    /// I²C 写。addr8 为 8 位合成地址（7 位 &lt;&lt; 1，与 Ginkgo 封装约定一致）。
    /// 返回：≥0 实际写入字节数；&lt;0 错误码。
    /// </summary>
    public static int Write(int handle, byte addr8, byte[] data) =>
        aa_i2c_write(handle, (ushort)(addr8 << 1), data, (ushort)data.Length);

    /// <summary>
    /// I²C 读。addr8 为 8 位合成地址。
    /// 返回：≥0 实际读取字节数；&lt;0 错误码（AA_I2C_READ_ERROR 等）。
    /// </summary>
    public static int Read(int handle, byte addr8, byte[] buffer) =>
        aa_i2c_read(handle, (ushort)(addr8 << 1), (ushort)buffer.Length, buffer);

    public static void SleepMs(uint ms) => aa_sleep_ms(ms);

    /// <summary>错误码转人类可读文本（借官方 aa_status_string）。</summary>
    public static string ErrorName(int status)
    {
        if (status == AA_OK) return "OK";
        try
        {
            IntPtr ptr = aa_status_string(status);
            return ptr != IntPtr.Zero
                ? System.Runtime.InteropServices.Marshal.PtrToStringAnsi(ptr) ?? $"ERROR {status}"
                : $"ERROR {status}";
        }
        catch
        {
            return $"ERROR {status}";
        }
    }
}
