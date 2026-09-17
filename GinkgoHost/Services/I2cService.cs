using System.Diagnostics;
using GinkgoHost.Native;

namespace GinkgoHost.Services;

/// <summary>单次总线操作结果。Ret 为驱动错误码，0=成功。</summary>
public sealed record OpResult(int Ret, byte[]? Data, double Ms)
{
    public bool Ok => Ret == 0;
}

/// <summary>连接链路内读取的适配器身份快照；页面只渲染快照，不直接并发调用原生驱动。</summary>
public sealed record AdapterIdentity(string SerialNumber, string FirmwareVersion, string? NativeProductName);

/// <summary>
/// Ginkgo USB-I2C 总线服务。所有 DLL 调用在后台线程经 SemaphoreSlim 串行化，
/// UI 线程只接触结果。驱动配置是粘性状态（SubAddrWidth 等），所以每个操作前
/// 都按需重新 InitI2C，扫描/原始模式结束后恢复 1 字节子地址模式。
/// </summary>
public sealed class I2cService : IDisposable
{
    /// <summary>界面、控制台和驱动层共用的单次传输上限，避免异常输入分配大数组或截断为 ushort。</summary>
    public const int MaxTransferLength = 256;

    private readonly SemaphoreSlim _bus = new(1, 1);
    private int _channel;
    private uint _clockHz = 400_000;
    private byte _controlMode = GinkgoDriver.VII_HCTL_MODE;
    private bool _timeConfigured; // 软件 I2C 时序每个开 Session 只配一次

    // 最近一次成功下发给驱动的 InitI2C 配置。所有驱动调用都在 _bus 串行化下进行，
    // 配置未变化时跳过 VII_InitI2C 的 USB 往返（周期读写场景每 tick 省一次）。
    // 任一操作失败即置空：驱动状态未知，下一次操作强制重新初始化（保留自愈能力）。
    private bool _initValid;
    private int _initChannel;
    private uint _initClockHz;
    private byte _initControlMode;
    private byte _initSubAddrWidth;

    public bool IsOpen { get; private set; }
    public int AdapterCount { get; private set; }
    public AdapterIdentity? AdapterInfo { get; private set; }
    public int Channel => _channel;
    public uint ClockHz => _clockHz;
    public byte ControlMode => _controlMode;

    /// <summary>IsOpen 变化后触发（连接/断开），供状态栏等订阅。</summary>
    public event Action? StateChanged;

    /// <summary>连接保持时修改通道/速率（重新 InitI2C），未连接则仅记忆参数。</summary>
    public async Task<int> ApplyConfigAsync(int channel, uint clockHz, byte controlMode)
    {
        ValidateConfig(channel, clockHz, controlMode);
        await _bus.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                int oldChannel = _channel;
                uint oldClockHz = _clockHz;
                byte oldControlMode = _controlMode;
                bool oldTimeConfigured = _timeConfigured;

                _channel = channel;
                _clockHz = clockHz;
                if (_controlMode != controlMode) _timeConfigured = false; // 模式切换需重配软件时序
                _controlMode = controlMode;
                if (!IsOpen) return 0;

                int ret = ReinitLocked(GinkgoDriver.VII_SUB_ADDR_1BYTE);
                if (ret == 0)
                {
                    StateChanged?.Invoke(); // 速率/通道变了，状态栏同步刷新
                    Dbg.Log($"ApplyConfigAsync ch={channel} clk={clockHz} mode={controlMode} -> {ret}");
                    return ret;
                }

                // 新配置未初始化成功时，内存状态和驱动状态必须一起还原，避免界面配置与实际总线脱节。
                _channel = oldChannel;
                _clockHz = oldClockHz;
                _controlMode = oldControlMode;
                _timeConfigured = oldTimeConfigured;
                int restoreRet = ReinitLocked(GinkgoDriver.VII_SUB_ADDR_1BYTE);
#if DEBUG
                Dbg.Log($"I2cService.ApplyConfigAsync: candidate failed ret={ret}; restored ch={oldChannel} clk={oldClockHz} mode={oldControlMode}, restoreRet={restoreRet}");
#endif
                return ret;
            });
        }
        finally { _bus.Release(); }
    }

    /// <summary>扫描并打开第一个适配器。返回 (适配器数, 最后一步错误码)。</summary>
    public async Task<(int Count, int Ret)> ConnectAsync(int channel, uint clockHz, byte controlMode)
    {
        ValidateConfig(channel, clockHz, controlMode);
        await _bus.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                _channel = channel;
                _clockHz = clockHz;
                _controlMode = controlMode;
                _timeConfigured = false;
                _initValid = false; // 新会话驱动配置未知
                AdapterInfo = null;
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
                if (IsOpen) AdapterInfo = ReadAdapterInfoLocked();
                if (ret != 0)
                {
                    // OpenDevice 成功而 InitI2C 失败时释放句柄，保证下一次连接可从干净状态开始。
                    int closeRet = GinkgoDriver.VII_CloseDevice(GinkgoDriver.VII_USBI2C, 0);
#if DEBUG
                    Dbg.Log($"I2cService.ConnectAsync: init failed ret={ret}, cleanup CloseDevice -> {closeRet}");
#endif
                }
                Dbg.Log($"初始化 I2C -> {ret}，IsOpen={IsOpen}");
                StateChanged?.Invoke();
                return (count, ret);
            });
        }
        finally { _bus.Release(); }
    }

    /// <summary>只刷新 USB 适配器资源，不打开 I²C 会话；已连接时返回当前会话计数，避免干扰总线。</summary>
    public async Task<int> ScanAdaptersAsync()
    {
        await _bus.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                if (IsOpen) return AdapterCount;
                // 离线扫描无法确认仍是同一硬件，避免拔插后继续显示旧序列号。
                AdapterInfo = null;
                int count = GinkgoDriver.VII_ScanDevice(1);
                AdapterCount = count;
                StateChanged?.Invoke();
#if DEBUG
                Dbg.Log($"I2cService.ScanAdaptersAsync: count={count}, identityCleared=true");
#endif
                return count;
            });
        }
        finally { _bus.Release(); }
    }

    /// <summary>只在已持有 _bus 的连接链路中读取身份，避免与 Scan/Open/Init 并发进入 DLL。</summary>
    private static AdapterIdentity? ReadAdapterInfoLocked()
    {
        try
        {
            var info = new GinkgoDriver.VII_BOARD_INFO
            {
                ProductName = new byte[32],
                FirmwareVersion = new byte[4],
                HardwareVersion = new byte[4],
                SerialNumber = new byte[12]
            };
            int ret = GinkgoDriver.ReadBoardInfo(0, ref info);
            if (ret != 0)
            {
#if DEBUG
                Dbg.Log($"I2cService.ReadAdapterInfoLocked: ret={ret} ({GinkgoDriver.ErrorName(ret)})");
#endif
                return null;
            }

            string serial = GinkgoDriver.Ascii(info.SerialNumber);
            string firmware = $"v{info.FirmwareVersion[0]}.{info.FirmwareVersion[1]}";
            string nativeName = GinkgoDriver.Ascii(info.ProductName);
#if DEBUG
            Dbg.Log($"I2cService.ReadAdapterInfoLocked: serial={serial} firmware={firmware}");
#endif
            return new AdapterIdentity(serial, firmware, nativeName.Length > 0 ? nativeName : null);
        }
        catch (Exception ex)
        {
#if DEBUG
            Dbg.Log($"I2cService.ReadAdapterInfoLocked: failed={ex.Message}");
#else
            _ = ex;
#endif
            return null;
        }
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
                _initValid = false; // 设备已关，下次连接必须重新初始化
                StateChanged?.Invoke();
                return ret;
            });
        }
        finally { _bus.Release(); }
    }

    /// <summary>扫描总线 0x08–0x77。progress(已探测数,总数) 与 hit(地址) 在后台线程回调，供 UI 流式刷新。
    /// channel 缺省用当前配置通道。ct 取消时立即返回已命中的部分结果。</summary>
    public async Task<List<byte>> ScanBusAsync(Action<int, int>? progress = null, Action<byte>? hit = null, int? channel = null,
        CancellationToken ct = default)
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
                if (ret != 0) throw new InvalidOperationException($"扫描初始化失败：{GinkgoDriver.ErrorName(ret)} ({ret})");

                try
                {
                    const byte first = 0x08, last = 0x77;
                    int total = last - first + 1, probed = 0;
                    var buf = new byte[1]; // 112 次探测复用同一缓冲
                    for (byte addr = first; addr <= last; addr++, probed++)
                    {
                        if (ct.IsCancellationRequested)
                        {
                            Dbg.Log($"ScanBusAsync: cancelled at 0x{addr:X2}, hits={found.Count}");
                            break;
                        }
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

                    Dbg.Log($"ScanBusAsync 完成，命中 {found.Count} 个地址");
                    return found;
                }
                finally
                {
                    int restoreRet = ReinitLocked(GinkgoDriver.VII_SUB_ADDR_1BYTE); // 扫描异常也必须恢复寄存器模式
                    Dbg.Log($"ScanBusAsync: restore register mode -> {restoreRet}");
                }
            });
        }
        finally { _bus.Release(); }
    }

    /// <summary>写寄存器：1 字节子地址（寄存器号）+ 数据。</summary>
    public async Task<OpResult> WriteRegisterAsync(byte addr7, byte reg, byte[] data)
    {
        ValidateAddress(addr7);
        ValidateData(data);
        await _bus.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                EnsureOpen();
                int initRet = ReinitLocked(GinkgoDriver.VII_SUB_ADDR_1BYTE);
                if (initRet != 0) return new OpResult(initRet, null, 0);
                var sw = Stopwatch.StartNew();
                int ret = GinkgoDriver.VII_WriteBytes(GinkgoDriver.VII_USBI2C, 0, _channel,
                    (ushort)(addr7 << 1), reg, data, (ushort)data.Length);
                sw.Stop();
                Dbg.Log($"VII_WriteBytes addr=0x{addr7:X2} reg=0x{reg:X2} len={data.Length} -> {ret} ({GinkgoDriver.ErrorName(ret)})");
                if (ret != 0) _initValid = false; // 失败后驱动状态未知，下笔强制重新初始化
                return new OpResult(ret, null, sw.Elapsed.TotalMilliseconds);
            });
        }
        finally { _bus.Release(); }
    }

    /// <summary>读寄存器：写 1 字节子地址后重启总线读 len 字节。</summary>
    public async Task<OpResult> ReadRegisterAsync(byte addr7, byte reg, int len)
    {
        ValidateAddress(addr7);
        ValidateLength(len);
        await _bus.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                EnsureOpen();
                int initRet = ReinitLocked(GinkgoDriver.VII_SUB_ADDR_1BYTE);
                if (initRet != 0) return new OpResult(initRet, null, 0);
                var buf = new byte[len];
                var sw = Stopwatch.StartNew();
                int ret = GinkgoDriver.VII_ReadBytes(GinkgoDriver.VII_USBI2C, 0, _channel,
                    (ushort)(addr7 << 1), reg, buf, (ushort)len);
                sw.Stop();
                Dbg.Log($"VII_ReadBytes addr=0x{addr7:X2} reg=0x{reg:X2} len={len} -> {ret} ({GinkgoDriver.ErrorName(ret)})");
                if (ret != 0) _initValid = false; // 失败后驱动状态未知，下笔强制重新初始化
                return new OpResult(ret, ret == 0 ? buf : null, sw.Elapsed.TotalMilliseconds);
            });
        }
        finally { _bus.Release(); }
    }

    /// <summary>原始写：无子地址，整段数据直接发出。</summary>
    public async Task<OpResult> RawWriteAsync(byte addr7, byte[] data)
    {
        ValidateAddress(addr7);
        ValidateData(data);
        await _bus.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                EnsureOpen();
                int initRet = ReinitLocked(GinkgoDriver.VII_SUB_ADDR_NONE);
                if (initRet != 0) return new OpResult(initRet, null, 0);
                var sw = Stopwatch.StartNew();
                int ret = GinkgoDriver.VII_WriteBytes(GinkgoDriver.VII_USBI2C, 0, _channel,
                    (ushort)(addr7 << 1), 0, data, (ushort)data.Length);
                sw.Stop();
                Dbg.Log($"VII_WriteBytes(原始) addr=0x{addr7:X2} len={data.Length} -> {ret}");
                if (ret != 0) _initValid = false; // 失败后驱动状态未知，下笔强制重新初始化
                return new OpResult(ret, null, sw.Elapsed.TotalMilliseconds);
            });
        }
        finally { _bus.Release(); }
    }

    /// <summary>原始读：无子地址，直接读 len 字节。</summary>
    public async Task<OpResult> RawReadAsync(byte addr7, int len)
    {
        ValidateAddress(addr7);
        ValidateLength(len);
        await _bus.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                EnsureOpen();
                int initRet = ReinitLocked(GinkgoDriver.VII_SUB_ADDR_NONE);
                if (initRet != 0) return new OpResult(initRet, null, 0);
                var buf = new byte[len];
                var sw = Stopwatch.StartNew();
                int ret = GinkgoDriver.VII_ReadBytes(GinkgoDriver.VII_USBI2C, 0, _channel,
                    (ushort)(addr7 << 1), 0, buf, (ushort)len);
                sw.Stop();
                Dbg.Log($"VII_ReadBytes(原始) addr=0x{addr7:X2} len={len} -> {ret}");
                if (ret != 0) _initValid = false; // 失败后驱动状态未知，下笔强制重新初始化
                return new OpResult(ret, ret == 0 ? buf : null, sw.Elapsed.TotalMilliseconds);
            });
        }
        finally { _bus.Release(); }
    }

    /// <summary>任意宽度子地址读：subAddrWidth=1/2 字节（16 位寄存器地址器件用 2）。</summary>
    public async Task<OpResult> ReadSubAddrAsync(byte addr7, uint subAddr, int len, byte subAddrWidth)
    {
        ValidateAddress(addr7);
        ValidateLength(len);
        ValidateSubAddrWidth(subAddrWidth);
        await _bus.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                EnsureOpen();
                int initRet = ReinitLocked(subAddrWidth);
                if (initRet != 0) return new OpResult(initRet, null, 0);
                var buf = new byte[len];
                var sw = Stopwatch.StartNew();
                int ret = GinkgoDriver.VII_ReadBytes(GinkgoDriver.VII_USBI2C, 0, _channel,
                    (ushort)(addr7 << 1), subAddr, buf, (ushort)len);
                sw.Stop();
                Dbg.Log($"VII_ReadBytes addr=0x{addr7:X2} sub=0x{subAddr:X4} w={subAddrWidth} len={len} -> {ret}");
                if (ret != 0) _initValid = false; // 失败后驱动状态未知，下笔强制重新初始化
                return new OpResult(ret, ret == 0 ? buf : null, sw.Elapsed.TotalMilliseconds);
            });
        }
        finally { _bus.Release(); }
    }

    /// <summary>任意宽度子地址写。</summary>
    public async Task<OpResult> WriteSubAddrAsync(byte addr7, uint subAddr, byte[] data, byte subAddrWidth)
    {
        ValidateAddress(addr7);
        ValidateData(data);
        ValidateSubAddrWidth(subAddrWidth);
        await _bus.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                EnsureOpen();
                int initRet = ReinitLocked(subAddrWidth);
                if (initRet != 0) return new OpResult(initRet, null, 0);
                var sw = Stopwatch.StartNew();
                int ret = GinkgoDriver.VII_WriteBytes(GinkgoDriver.VII_USBI2C, 0, _channel,
                    (ushort)(addr7 << 1), subAddr, data, (ushort)data.Length);
                sw.Stop();
                Dbg.Log($"VII_WriteBytes addr=0x{addr7:X2} sub=0x{subAddr:X4} w={subAddrWidth} len={data.Length} -> {ret}");
                if (ret != 0) _initValid = false; // 失败后驱动状态未知，下笔强制重新初始化
                return new OpResult(ret, null, sw.Elapsed.TotalMilliseconds);
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

    private void EnsureOpen()
    {
        if (!IsOpen)
            throw new InvalidOperationException("适配器未连接，请先点击“连接适配器”");
    }

    private static void ValidateConfig(int channel, uint clockHz, byte controlMode)
    {
        if (controlMode is not GinkgoDriver.VII_HCTL_MODE and not GinkgoDriver.VII_SCTL_MODE)
            throw new ArgumentOutOfRangeException(nameof(controlMode), "控制模式无效");
        int maxChannel = controlMode == GinkgoDriver.VII_SCTL_MODE ? 7 : 1;
        if (channel is < 0 or > 7 || channel > maxChannel)
            throw new ArgumentOutOfRangeException(nameof(channel), $"当前控制模式的通道范围为 0–{maxChannel}");
        if (clockHz is < 1_000 or > 2_000_000)
            throw new ArgumentOutOfRangeException(nameof(clockHz), "I²C 速率须在 1 kHz–2 MHz 之间");
    }

    private static void ValidateAddress(byte addr7)
    {
        if (addr7 > 0x7F)
            throw new ArgumentOutOfRangeException(nameof(addr7), "从机地址须为 7-bit 地址（00–7F）");
    }

    private static void ValidateLength(int len)
    {
        if (len is < 1 or > MaxTransferLength)
            throw new ArgumentOutOfRangeException(nameof(len), $"传输长度须在 1–{MaxTransferLength} 字节之间");
    }

    private static void ValidateData(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        ValidateLength(data.Length);
    }

    private static void ValidateSubAddrWidth(byte subAddrWidth)
    {
        if (subAddrWidth is not GinkgoDriver.VII_SUB_ADDR_1BYTE and not GinkgoDriver.VII_SUB_ADDR_2BYTE)
            throw new ArgumentOutOfRangeException(nameof(subAddrWidth), "子地址宽度仅支持 1 或 2 字节");
    }

    private int ReinitLocked(byte subAddrWidth, int? channel = null)
    {
        int ch = channel ?? _channel;
        if (_initValid && _initChannel == ch && _initClockHz == _clockHz &&
            _initControlMode == _controlMode && _initSubAddrWidth == subAddrWidth)
            return 0; // 驱动已处于该配置（粘性状态），跳过 USB 往返

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
        if (ret == 0)
        {
            _initValid = true;
            _initChannel = ch;
            _initClockHz = _clockHz;
            _initControlMode = _controlMode;
            _initSubAddrWidth = subAddrWidth;
        }
        else
        {
            _initValid = false; // 失败后驱动配置未知，下次操作强制重新初始化
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
