using System.Diagnostics;
using GinkgoHost.Native;

namespace GinkgoHost.Services;

/// <summary>单次总线操作结果。Ret 为驱动错误码，0=成功。</summary>
public sealed record OpResult(int Ret, byte[]? Data, double Ms)
{
    public bool Ok => Ret == 0;
}

/// <summary>
/// Ginkgo USB-I2C 总线服务。所有 DLL 调用在后台线程经 SemaphoreSlim 串行化，
/// UI 线程只接触结果。驱动配置是粘性状态（SubAddrWidth 等），所以每个操作前
/// 都按需重新 InitI2C，扫描/原始模式结束后恢复 1 字节子地址模式。
/// </summary>
public sealed class I2cService : IDisposable
{
    private readonly SemaphoreSlim _bus = new(1, 1);
    private int _channel;
    private uint _clockHz = 400_000;
    private byte _controlMode = GinkgoDriver.VII_HCTL_MODE;
    private bool _timeConfigured; // 软件 I2C 时序每个开 Session 只配一次

    public bool IsOpen { get; private set; }
    public int AdapterCount { get; private set; }
    public int Channel => _channel;
    public uint ClockHz => _clockHz;
    public byte ControlMode => _controlMode;

    /// <summary>IsOpen 变化后触发（连接/断开），供状态栏等订阅。</summary>
    public event Action? StateChanged;

    /// <summary>连接保持时修改通道/速率（重新 InitI2C），未连接则仅记忆参数。</summary>
    public async Task<int> ApplyConfigAsync(int channel, uint clockHz, byte controlMode)
    {
        await _bus.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                _channel = channel;
                _clockHz = clockHz;
                if (_controlMode != controlMode) _timeConfigured = false; // 模式切换需重配软件时序
                _controlMode = controlMode;
                if (!IsOpen) return 0;
                int ret = ReinitLocked(GinkgoDriver.VII_SUB_ADDR_1BYTE);
                if (ret == 0) StateChanged?.Invoke(); // 速率/通道变了，状态栏同步刷新
                Dbg.Log($"ApplyConfigAsync ch={channel} clk={clockHz} mode={controlMode} -> {ret}");
                return ret;
            });
        }
        finally { _bus.Release(); }
    }

    /// <summary>扫描并打开第一个适配器。返回 (适配器数, 最后一步错误码)。</summary>
    public async Task<(int Count, int Ret)> ConnectAsync(int channel, uint clockHz, byte controlMode)
    {
        await _bus.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                _channel = channel;
                _clockHz = clockHz;
                _controlMode = controlMode;
                _timeConfigured = false;
                Dbg.Log($"ConnectAsync: 通道={channel} 速率={clockHz}Hz，扫描适配器");
                int count = GinkgoDriver.VII_ScanDevice(1);
                AdapterCount = count;
                Dbg.Log($"VII_ScanDevice(1) -> {count}");
                if (count <= 0)
                {
                    IsOpen = false;
                    StateChanged?.Invoke();
                    return (count, count);
                }

                int ret = GinkgoDriver.VII_OpenDevice(GinkgoDriver.VII_USBI2C, 0, 0);
                Dbg.Log($"VII_OpenDevice(I2C, 0, 0) -> {ret} ({GinkgoDriver.ErrorName(ret)})");
                if (ret != 0)
                {
                    IsOpen = false;
                    StateChanged?.Invoke();
                    return (count, ret);
                }

                ret = ReinitLocked(GinkgoDriver.VII_SUB_ADDR_1BYTE);
                IsOpen = ret == 0;
                Dbg.Log($"初始化 I2C -> {ret}，IsOpen={IsOpen}");
                StateChanged?.Invoke();
                return (count, ret);
            });
        }
        finally { _bus.Release(); }
    }

    public async Task<int> CloseAsync()
    {
        await _bus.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                if (!IsOpen) return 0;
                int ret = GinkgoDriver.VII_CloseDevice(GinkgoDriver.VII_USBI2C, 0);
                Dbg.Log($"VII_CloseDevice -> {ret}");
                IsOpen = false;
                StateChanged?.Invoke();
                return ret;
            });
        }
        finally { _bus.Release(); }
    }

    /// <summary>扫描总线 0x08–0x77。progress(已探测数,总数) 与 hit(地址) 在后台线程回调，供 UI 流式刷新。
    /// channel 缺省用当前配置通道。</summary>
    public async Task<List<byte>> ScanBusAsync(Action<int, int>? progress = null, Action<byte>? hit = null, int? channel = null)
    {
        await _bus.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                EnsureOpen();
                int ch = channel ?? _channel;
                var found = new List<byte>();
                int ret = ReinitLocked(GinkgoDriver.VII_SUB_ADDR_NONE, ch);
                Dbg.Log($"ScanBusAsync: 通道 {ch} 切换无子地址模式 -> {ret}");

                const byte first = 0x08, last = 0x77;
                int total = last - first + 1, probed = 0;
                for (byte addr = first; addr <= last; addr++, probed++)
                {
                    var buf = new byte[1];
                    ret = GinkgoDriver.VII_ReadBytes(GinkgoDriver.VII_USBI2C, 0, ch,
                        (ushort)(addr << 1), 0, buf, 1);
                    progress?.Invoke(probed + 1, total);
                    if (ret == 0)
                    {
                        found.Add(addr);
                        hit?.Invoke(addr);
                        Dbg.Log($"扫描命中 0x{addr:X2}");
                    }
                }

                ReinitLocked(GinkgoDriver.VII_SUB_ADDR_1BYTE); // 恢复寄存器读写模式
                Dbg.Log($"ScanBusAsync 完成，命中 {found.Count} 个地址");
                return found;
            });
        }
        finally { _bus.Release(); }
    }

    /// <summary>写寄存器：1 字节子地址（寄存器号）+ 数据。</summary>
    public async Task<OpResult> WriteRegisterAsync(byte addr7, byte reg, byte[] data)
    {
        await _bus.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                EnsureOpen();
                ReinitLocked(GinkgoDriver.VII_SUB_ADDR_1BYTE);
                var sw = Stopwatch.StartNew();
                int ret = GinkgoDriver.VII_WriteBytes(GinkgoDriver.VII_USBI2C, 0, _channel,
                    (ushort)(addr7 << 1), reg, data, (ushort)data.Length);
                sw.Stop();
                Dbg.Log($"VII_WriteBytes addr=0x{addr7:X2} reg=0x{reg:X2} len={data.Length} -> {ret} ({GinkgoDriver.ErrorName(ret)})");
                return new OpResult(ret, null, sw.Elapsed.TotalMilliseconds);
            });
        }
        finally { _bus.Release(); }
    }

    /// <summary>读寄存器：写 1 字节子地址后重启总线读 len 字节。</summary>
    public async Task<OpResult> ReadRegisterAsync(byte addr7, byte reg, int len)
    {
        await _bus.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                EnsureOpen();
                ReinitLocked(GinkgoDriver.VII_SUB_ADDR_1BYTE);
                var buf = new byte[len];
                var sw = Stopwatch.StartNew();
                int ret = GinkgoDriver.VII_ReadBytes(GinkgoDriver.VII_USBI2C, 0, _channel,
                    (ushort)(addr7 << 1), reg, buf, (ushort)len);
                sw.Stop();
                Dbg.Log($"VII_ReadBytes addr=0x{addr7:X2} reg=0x{reg:X2} len={len} -> {ret} ({GinkgoDriver.ErrorName(ret)})");
                return new OpResult(ret, ret == 0 ? buf : null, sw.Elapsed.TotalMilliseconds);
            });
        }
        finally { _bus.Release(); }
    }

    /// <summary>原始写：无子地址，整段数据直接发出。</summary>
    public async Task<OpResult> RawWriteAsync(byte addr7, byte[] data)
    {
        await _bus.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                EnsureOpen();
                ReinitLocked(GinkgoDriver.VII_SUB_ADDR_NONE);
                var sw = Stopwatch.StartNew();
                int ret = GinkgoDriver.VII_WriteBytes(GinkgoDriver.VII_USBI2C, 0, _channel,
                    (ushort)(addr7 << 1), 0, data, (ushort)data.Length);
                sw.Stop();
                Dbg.Log($"VII_WriteBytes(原始) addr=0x{addr7:X2} len={data.Length} -> {ret}");
                return new OpResult(ret, null, sw.Elapsed.TotalMilliseconds);
            });
        }
        finally { _bus.Release(); }
    }

    /// <summary>原始读：无子地址，直接读 len 字节。</summary>
    public async Task<OpResult> RawReadAsync(byte addr7, int len)
    {
        await _bus.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                EnsureOpen();
                ReinitLocked(GinkgoDriver.VII_SUB_ADDR_NONE);
                var buf = new byte[len];
                var sw = Stopwatch.StartNew();
                int ret = GinkgoDriver.VII_ReadBytes(GinkgoDriver.VII_USBI2C, 0, _channel,
                    (ushort)(addr7 << 1), 0, buf, (ushort)len);
                sw.Stop();
                Dbg.Log($"VII_ReadBytes(原始) addr=0x{addr7:X2} len={len} -> {ret}");
                return new OpResult(ret, ret == 0 ? buf : null, sw.Elapsed.TotalMilliseconds);
            });
        }
        finally { _bus.Release(); }
    }

    /// <summary>周期读：按间隔重复执行读操作。onTick 在后台线程回调（含失败结果）。
    /// 连续 5 次失败自动停止。返回 (完成次数, 停止原因)。最小间隔 10 ms。</summary>
    public async Task<(int Done, string? StopReason)> PeriodicReadAsync(int intervalMs, byte addr7, byte? reg, int len,
        Action<OpResult> onTick, CancellationToken ct)
    {
        int done = 0, fails = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(10, intervalMs)));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var r = reg.HasValue
                    ? await ReadRegisterAsync(addr7, reg.Value, len)
                    : await RawReadAsync(addr7, len);
                onTick(r);
                done++;
                if (r.Ok) fails = 0;
                else if (++fails >= 5)
                    return (done, $"连续 {fails} 次失败（{GinkgoDriver.ErrorName(r.Ret)}），已自动停止");
            }
        }
        catch (OperationCanceledException) { } // 手动停止：正常返回计数
        return (done, null);
    }

    /// <summary>周期写：按间隔重复执行写操作。自动停止策略与周期读相同。最小间隔 10 ms。</summary>
    public async Task<(int Done, string? StopReason)> PeriodicWriteAsync(int intervalMs, byte addr7, byte? reg,
        byte[] data, Action<OpResult> onTick, CancellationToken ct)
    {
        int done = 0, fails = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(10, intervalMs)));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var r = reg.HasValue
                    ? await WriteRegisterAsync(addr7, reg.Value, data)
                    : await RawWriteAsync(addr7, data);
                onTick(r);
                done++;
                if (r.Ok) fails = 0;
                else if (++fails >= 5)
                    return (done, $"连续 {fails} 次失败（{GinkgoDriver.ErrorName(r.Ret)}），已自动停止");
            }
        }
        catch (OperationCanceledException) { } // 手动停止：正常返回计数
        return (done, null);
    }

    private void EnsureOpen()
    {
        if (!IsOpen)
            throw new InvalidOperationException("适配器未连接，请先点击“连接适配器”");
    }

    private int ReinitLocked(byte subAddrWidth, int? channel = null)
    {
        int ch = channel ?? _channel;
        var cfg = new GinkgoDriver.VII_INIT_CONFIG
        {
            MasterMode = GinkgoDriver.VII_MASTER,
            ControlMode = _controlMode,
            AddrType = GinkgoDriver.VII_ADDR_7BIT,
            SubAddrWidth = subAddrWidth,
            Addr = 0,
            ClockSpeed = _clockHz
        };
        int ret = GinkgoDriver.VII_InitI2C(GinkgoDriver.VII_USBI2C, 0, ch, ref cfg);
        // 软件 I2C 需先配 GPIO 时序（SCTL 例程默认值），硬件模式跳过
        if (ret == 0 && _controlMode == GinkgoDriver.VII_SCTL_MODE && !_timeConfigured)
        {
            var t = new GinkgoDriver.VII_TIME_CONFIG
            {
                tHD_STA = 4, tSU_STA = 5, tLOW = 5, tHIGH = 5,
                tSU_DAT = 1, tSU_STO = 4, tBuf = 5,
                tACK = new byte[4]
            };
            ret = GinkgoDriver.VII_TimeConfig(GinkgoDriver.VII_USBI2C, 0, ch, ref t);
            if (ret == 0) _timeConfigured = true;
            Dbg.Log($"VII_TimeConfig ch={ch} -> {ret}");
        }
        Dbg.Log($"VII_InitI2C ch={ch} clk={_clockHz} mode={_controlMode} subW={subAddrWidth} -> {ret}");
        return ret;
    }

    public void Dispose()
    {
        if (IsOpen)
            _ = GinkgoDriver.VII_CloseDevice(GinkgoDriver.VII_USBI2C, 0); // 进程退出兜底，忽略错误码
        _bus.Dispose();
    }
}
