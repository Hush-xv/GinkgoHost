using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using GinkgoHost.Native;
using GinkgoHost.Services;

namespace GinkgoHost.Views;

/// <summary>
/// I²C 从机模拟页：适配器以 VII_SLAVE 模式应答外部主机。
/// 与主机模式互斥——从机会话期间 I2cService.ConnectAsync 一律拒绝（-20）。
/// 两种应答模式：
///  预载数据——把字节追加进驱动发送队列，主机按 FIFO 顺序读走（EEPROM 式）；
///  寄存器映射——主机先写指针（1/2 字节大端），应用感知后把寄存器表从指针处追加入队；
///    感知依赖轮询，主机写指针到读之间须间隔 ≥1 个轮询周期。
/// </summary>
public partial class SlavePage : UserControl
{
    private const int MaxRxLines = 200;
    private const int PollIntervalMs = 20;
    private const int QueueChunk = 64;   // 寄存器模式每次追加的应答字节数

    private static SlavePage? _instance;

    private CancellationTokenSource? _pollCts;
    private byte _addr7 = 0x50;
    private byte _channel;
    private int _fails;
    private int _lastErrLogged;
    private int _rxCount;

    // 寄存器映射状态
    private bool _registerMode;
    private int _ptrWidth = 1;                       // 指针字节数 1/2
    private byte[] _map = new byte[256];             // 寄存器表（16-bit 指针时 65536）
    private int _pointer;                            // 下一次追加应答的起始寄存器
    private ushort _lastRemain;                      // 上次看到的发送队列余量

    public sealed record SlaveRxLine(string Time, string Data);
    public sealed class SlaveRegRow
    {
        public string Reg { get; set; } = "00";
        public string Value { get; set; } = "00";
    }

    private readonly ObservableCollection<SlaveRxLine> _rx = [];
    private readonly ObservableCollection<SlaveRegRow> _regRows = [];

    public SlavePage()
    {
        InitializeComponent();
        _instance = this;
        LstSlaveRx.ItemsSource = _rx;
        GridSlaveReg.ItemsSource = _regRows;
        _regRows.Add(new SlaveRegRow { Reg = "00", Value = "00" });
    }

    public static bool SessionActive => _instance?._pollCts is not null;

    /// <summary>App.OnExit / 窗口关闭兜底：释放从机会话占用的设备。</summary>
    public static void Shutdown() => _instance?.StopSession(announce: false);

    private void CmbSlaveMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // XAML 解析设置 SelectedIndex 时会先触发本事件，此时面板字段尚未赋值
        if (PnlPreload is null || PnlRegister is null) return;
        bool register = CmbSlaveMode.SelectedIndex == 1;
        PnlPreload.Visibility = register ? Visibility.Collapsed : Visibility.Visible;
        PnlRegister.Visibility = register ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void BtnSlaveStart_Click(object sender, RoutedEventArgs e)
    {
        if (SessionActive) return;
        if (App.Bus.IsOpen)
        {
            SetState("主机模式已连接：请先在左侧工作区断开，再启动从机", false);
            return;
        }
        if (!TryParseAddr(out _addr7))
        {
            SetState("地址无效：请输入 7 位十六进制地址 08–77", false);
            return;
        }
        _channel = (byte)CmbSlaveChannel.SelectedIndex;
        _registerMode = CmbSlaveMode.SelectedIndex == 1;
        _ptrWidth = CmbPtrWidth.SelectedIndex == 1 ? 2 : 1;

        // UI 线程先取快照，后台线程只碰驱动
        byte[] resp = ParseRespBuffer();
        _map = BuildMap(_ptrWidth == 2 ? 65536 : 256);
        _pointer = 0;
        _lastRemain = 0;

        BtnSlaveStart.IsEnabled = false;
        SetState("启动中…", true);
        var (ret, msg) = await Task.Run(() => StartCore(resp));
        if (ret != 0)
        {
            BtnSlaveStart.IsEnabled = true;
            SetState(msg, false);
            Dbg.Log($"SlavePage.Start: failed ret={ret} msg={msg}");
            return;
        }

        GinkgoDriver.SlaveSessionActive = true;
        _fails = 0;
        _pollCts = new CancellationTokenSource();
        BtnSlaveStop.IsEnabled = true;
        string modeText = _registerMode
            ? $"寄存器映射 · 指针 {_ptrWidth * 8}-bit · 主机写指针后 ≥20 ms 再读"
            : "预载数据";
        SetState($"从机运行中 · CH{_channel} · 地址 0x{_addr7:X2}（8 位 0x{_addr7 << 1:X2}）· {modeText}", true);
        Dbg.Log($"SlavePage.Start: ch={_channel} addr7=0x{_addr7:X2} mode={(_registerMode ? "reg" : "preload")} ptrW={_ptrWidth}");
        _ = PollLoopAsync(_pollCts.Token);
    }

    /// <summary>扫描→打开→从机初始化→按模式预载。返回 (错误码, 用户可读消息)。后台线程调用。</summary>
    private (int Ret, string Message) StartCore(byte[] resp)
    {
        int adapters = GinkgoDriver.VII_ScanDevice(1);
        if (adapters <= 0) return (-1, "未检测到 Ginkgo 适配器");
        int ret = GinkgoDriver.VII_OpenDevice(GinkgoDriver.VII_USBI2C, 0, 0);
        if (ret != 0) return (ret, $"打开失败：{GinkgoDriver.ErrorName(ret)}");

        var cfg = new GinkgoDriver.VII_INIT_CONFIG
        {
            MasterMode = GinkgoDriver.VII_SLAVE,
            ControlMode = GinkgoDriver.VII_HCTL_MODE,
            AddrType = GinkgoDriver.VII_ADDR_7BIT,
            SubAddrWidth = GinkgoDriver.VII_SUB_ADDR_NONE,
            Addr = (ushort)(_addr7 << 1),
            ClockSpeed = 100_000
        };
        ret = GinkgoDriver.VII_InitI2C(GinkgoDriver.VII_USBI2C, 0, 0, ref cfg);
        if (ret != 0)
        {
            _ = GinkgoDriver.VII_CloseDevice(GinkgoDriver.VII_USBI2C, 0);
            return (ret, $"进入从机模式失败：{GinkgoDriver.ErrorName(ret)}");
        }

        if (!_registerMode)
        {
            ret = GinkgoDriver.VII_SlaveWriteBytes(GinkgoDriver.VII_USBI2C, 0, _channel, resp, resp.Length);
            if (ret != 0)
            {
                // 异常关闭（杀进程/拔插）后通道可能残留卡死状态：重走一次从机初始化再试
                Dbg.Log($"SlavePage.Start: preload ret={ret}, retry after re-init");
                ret = GinkgoDriver.VII_InitI2C(GinkgoDriver.VII_USBI2C, 0, _channel, ref cfg);
                if (ret == 0) ret = GinkgoDriver.VII_SlaveWriteBytes(GinkgoDriver.VII_USBI2C, 0, _channel, resp, resp.Length);
            }
            if (ret != 0)
            {
                _ = GinkgoDriver.VII_CloseDevice(GinkgoDriver.VII_USBI2C, 0);
                return (ret, $"预载应答失败：{GinkgoDriver.ErrorName(ret)}（可停止后重试或重插适配器）");
            }
        }
        return (0, "");
    }

    private void BtnSlaveStop_Click(object sender, RoutedEventArgs e) => StopSession(announce: true);

    private void StopSession(bool announce)
    {
        if (_pollCts is null) return;
        _pollCts.Cancel();
        _pollCts = null;
        GinkgoDriver.SlaveSessionActive = false;
        int ch = _channel;
        Task.Run(() => _ = GinkgoDriver.VII_CloseDevice(GinkgoDriver.VII_USBI2C, 0));
        BtnSlaveStop.IsEnabled = false;
        BtnSlaveStart.IsEnabled = true;
        SetState(announce ? "从机已停止" : "从机会话已释放", true);
        Dbg.Log($"SlavePage.Stop: announce={announce}");
    }

    /// <summary>驱动读取包装：out 参数无法进 lambda，收拢成元组。</summary>
    private static (int Ret, int Got) ReadSlave(byte[] buf, int ch)
    {
        int ret = GinkgoDriver.VII_SlaveReadBytes(GinkgoDriver.VII_USBI2C, 0, ch, buf, buf.Length, out int got);
        return (ret, ret == 0 ? got : 0);
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        var buf = new byte[64];
        int ch = _channel;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                (int ret, int got) = await Task.Run(() => ReadSlave(buf, ch), ct);
                if (ct.IsCancellationRequested) break;
                if (ret == 0 && got > 0)
                {
                    _fails = 0;
                    var data = new byte[got];
                    Array.Copy(buf, data, got);
                    if (_registerMode)
                    {
                        var (desc, refillFrom) = ApplyMasterWrite(data);
                        Dispatcher.BeginInvoke(() => AddRxLine(desc));
                        if (refillFrom >= 0) ArmQueue(refillFrom, ch);
                    }
                    else
                    {
                        Dispatcher.BeginInvoke(() => AddRxLine($"{data.Length} B · {GinkgoDriver.Hex(data)}"));
                    }
                    Dbg.Log($"SlavePage.RX: {got} B from master");
                }
                else if (ret == -7)
                {
                    _fails = 0; // 无数据是从机常态
                }
                else
                {
                    // 非"无数据"的错误：逐条记录，自动停止时能对上原因
                    if (ret != _lastErrLogged)
                    {
                        _lastErrLogged = ret;
                        Dbg.Log($"SlavePage.PollLoop: SlaveReadBytes ret={ret} ({GinkgoDriver.ErrorName(ret)})");
                    }
                    _fails++;
                }

                if (_fails >= 5)
                {
                    int lastRet = ret;
                    Dbg.Log($"SlavePage.PollLoop: auto-stop after {_fails} consecutive errors, last ret={lastRet}");
                    Dispatcher.BeginInvoke(() =>
                    {
                        StopSession(announce: false);
                        SetState($"连续 {_fails} 次驱动错误，已自动停止（{GinkgoDriver.ErrorName(lastRet)}）· 详见调试日志", false);
                    });
                    return;
                }

                // 寄存器模式：跟踪主机读走的字节数（remain 下降 = 指针前进），队列排空则按当前指针补一段
                if (_registerMode)
                {
                    ushort remain = 0;
                    int rr = await Task.Run(() => GinkgoDriver.VII_SlaveWriteRemain(GinkgoDriver.VII_USBI2C, 0, ch, out remain), ct);
                    if (rr == 0)
                    {
                        if (remain < _lastRemain) _pointer = Math.Min(_pointer + (_lastRemain - remain), _map.Length);
                        _lastRemain = remain;
                        if (remain == 0) ArmQueue(_pointer, ch);
                    }
                }

                await Task.Delay(PollIntervalMs, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>把寄存器表从 from 开始的一段追加进发送队列（仅在队列余量为 0 时调用，避免旧数据混入）。</summary>
    private static void ArmQueue(int from, int ch)
    {
        if (_instance is null) return;
        int len = Math.Min(QueueChunk, _instance._map.Length - from);
        if (len <= 0) return;
        var chunk = new byte[len];
        Array.Copy(_instance._map, from, chunk, 0, len);
        int ret = GinkgoDriver.VII_SlaveWriteBytes(GinkgoDriver.VII_USBI2C, 0, ch, chunk, len);
        if (ret == 0) _instance._lastRemain = (ushort)len;
        Dbg.Log($"SlavePage.ArmQueue: from=0x{from:X2} len={len} ret={ret}");
    }

    /// <summary>
    /// 寄存器模式处理一帧主机写入：前 1/2 字节为指针（大端），其后为按指针自增写入的数据。
    /// 返回（RX 显示文本, 需要补队列的起始寄存器；-1 表示不补）。
    /// </summary>
    private (string Desc, int RefillFrom) ApplyMasterWrite(byte[] frame)
    {
        lock (_map)
        {
            if (frame.Length < _ptrWidth)
                return ($"{frame.Length} B · {GinkgoDriver.Hex(frame)}（不足指针宽度）", -1);
            int ptr = 0;
            for (int i = 0; i < _ptrWidth; i++) ptr = (ptr << 8) | frame[i];
            ptr %= _map.Length;
            int dataLen = frame.Length - _ptrWidth;
            for (int i = 0; i < dataLen; i++)
                _map[(ptr + i) % _map.Length] = frame[_ptrWidth + i];
            _pointer = (ptr + dataLen) % _map.Length;
            return dataLen > 0
                ? ($"指针 0x{ptr:X2} · 写 {dataLen} B", ptr)
                : ($"指针 → 0x{ptr:X2}", ptr);
        }
    }

    private void AddRxLine(string text)
    {
        _rxCount++;
        TxtRxCount.Text = $" · {_rxCount} 次";
        _rx.Add(new SlaveRxLine(DateTime.Now.ToString("HH:mm:ss.fff"), text));
        if (_rx.Count > MaxRxLines) _rx.RemoveAt(0);
        if (_rx.Count > 0) LstSlaveRx.ScrollIntoView(LstSlaveRx.Items[^1]);
    }

    private void BtnLoadResp_Click(object sender, RoutedEventArgs e)
    {
        byte[] resp = ParseRespBuffer();
        int ret = Task.Run(() => GinkgoDriver.VII_SlaveWriteBytes(GinkgoDriver.VII_USBI2C, 0, _channel, resp, resp.Length)).Result;
        if (SessionActive)
        {
            TxtRespState.Text = ret == 0
                ? $"已追加 {resp.Length} B 到发送队列"
                : $"装载失败：{GinkgoDriver.ErrorName(ret)}";
        }
        else
        {
            TxtRespState.Text = $"已解析 {resp.Length} B；启动从机时将自动装载";
        }
        Dbg.Log($"SlavePage.LoadResp: len={resp.Length} ret={ret} active={SessionActive}");
    }

    private void BtnRespZero_Click(object sender, RoutedEventArgs e)
    {
        TxtRespData.Text = "00";
        BtnLoadResp_Click(sender, e);
    }

    private void BtnRegAdd_Click(object sender, RoutedEventArgs e)
    {
        int next = _regRows.Count == 0 ? 0 : NextFreeAddr();
        _regRows.Add(new SlaveRegRow { Reg = $"{next & 0xFF:X2}", Value = "00" });
    }

    private int NextFreeAddr()
    {
        var used = new HashSet<byte>(_regRows.Select(r => HexByte(r.Reg)));
        for (int a = 0; a <= 0xFF; a++)
            if (!used.Contains((byte)a)) return a;
        return 0;
    }

    private void BtnRegDel_Click(object sender, RoutedEventArgs e)
    {
        if (GridSlaveReg.SelectedItem is SlaveRegRow row) _regRows.Remove(row);
    }

    private void BtnRegClear_Click(object sender, RoutedEventArgs e) => _regRows.Clear();

    private void BtnClearRx_Click(object sender, RoutedEventArgs e)
    {
        _rx.Clear();
        _rxCount = 0;
        TxtRxCount.Text = " · 0 次";
    }

    private bool TryParseAddr(out byte addr7)
    {
        addr7 = 0;
        string text = TxtSlaveAddr.Text.Trim();
        if (text.Length == 0) return false;
        if (!byte.TryParse(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text,
                NumberStyles.HexNumber, CultureInfo.InvariantCulture, out addr7)) return false;
        return addr7 is >= 0x08 and <= 0x77;
    }

    private static byte HexByte(string s)
    {
        s = s.Trim().StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s.Trim()[2..] : s.Trim();
        return byte.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    /// <summary>应答数据缓冲：HEX 文本转字节；空文本回退为单字节 0x00，保证从机始终有可应答内容。</summary>
    private byte[] ParseRespBuffer()
    {
        var bytes = new List<byte>();
        foreach (string token in TxtRespData.Text.Split([' ', ',', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (byte.TryParse(token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? token[2..] : token,
                    NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                bytes.Add(b);
            if (bytes.Count >= 256) break;
        }
        if (bytes.Count == 0) bytes.Add(0x00);
        return [.. bytes];
    }

    /// <summary>把寄存器表行解析进映像；行解析失败或越界跳过，未覆盖地址保持 0x00。</summary>
    private byte[] BuildMap(int size)
    {
        var map = new byte[size];
        foreach (var row in _regRows)
        {
            try
            {
                int addr = HexByte(row.Reg) % size;
                map[addr] = HexByte(row.Value);
            }
            catch { /* 单行格式错误跳过，不阻塞启动 */ }
        }
        return map;
    }

    private void SetState(string text, bool ok)
    {
        TxtSlaveState.Text = text;
        TxtSlaveState.SetResourceReference(TextBlock.ForegroundProperty,
            ok ? "StatusSuccessBrush" : "StatusErrorBrush");
    }
}
