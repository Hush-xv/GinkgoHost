using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using GinkgoHost.Native;
using GinkgoHost.Services;

namespace GinkgoHost.Views;

/// <summary>
/// I²C 从机模拟页：适配器以 VII_SLAVE 模式应答外部主机。
/// 与主机模式互斥——从机会话期间 I2cService.ConnectAsync 一律拒绝（-20）。
/// </summary>
public partial class SlavePage : UserControl
{
    private const int MaxRxLines = 200;
    private const int PollIntervalMs = 20;

    private static SlavePage? _instance;

    private CancellationTokenSource? _pollCts;
    private byte _addr7 = 0x50;
    private int _fails;
    private int _rxCount;

    public sealed record SlaveRxLine(string Time, string Data);
    private readonly ObservableCollection<SlaveRxLine> _rx = [];

    public SlavePage()
    {
        InitializeComponent();
        _instance = this;
        LstSlaveRx.ItemsSource = _rx;
        Unloaded += (_, _) =>
        {
            // 切页不停止会话（后台继续应答），仅在窗口关闭时由 App.OnExit 兜底
        };
    }

    public static bool SessionActive => _instance?._pollCts is not null;

    /// <summary>App.OnExit / 窗口关闭兜底：释放从机会话占用的设备。</summary>
    public static void Shutdown() => _instance?.StopSession(announce: false);

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

        BtnSlaveStart.IsEnabled = false;
        SetState("启动中…", true);
        byte[] resp = ParseRespBuffer(); // UI 线程取文本，后台线程只碰驱动
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
        SetState($"从机运行中 · 地址 0x{_addr7:X2}（8 位 0x{_addr7 << 1:X2}）· 主机写入会出现在下方", true);
        Dbg.Log($"SlavePage.Start: addr7=0x{_addr7:X2} respLoaded");
        _ = PollLoopAsync(_pollCts.Token);
    }

    /// <summary>扫描→打开→从机初始化→预载应答数据。返回 (错误码, 用户可读消息)。后台线程调用。</summary>
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

        ret = GinkgoDriver.VII_SlaveWriteBytes(GinkgoDriver.VII_USBI2C, 0, 0, resp, resp.Length);
        if (ret != 0)
            Dbg.Log($"SlavePage.Start: preload resp ret={ret} ({GinkgoDriver.ErrorName(ret)})");
        return (0, "");
    }

    private void BtnSlaveStop_Click(object sender, RoutedEventArgs e) => StopSession(announce: true);

    private void StopSession(bool announce)
    {
        if (_pollCts is null) return;
        _pollCts.Cancel();
        _pollCts = null;
        GinkgoDriver.SlaveSessionActive = false;
        Task.Run(() => _ = GinkgoDriver.VII_CloseDevice(GinkgoDriver.VII_USBI2C, 0));
        BtnSlaveStop.IsEnabled = false;
        BtnSlaveStart.IsEnabled = true;
        SetState(announce ? "从机已停止" : "从机会话已释放", true);
        Dbg.Log($"SlavePage.Stop: announce={announce}");
    }

    /// <summary>驱动读取包装：out 参数无法进 lambda，收拢成元组。</summary>
    private static (int Ret, int Got) ReadSlave(byte[] buf)
    {
        int ret = GinkgoDriver.VII_SlaveReadBytes(GinkgoDriver.VII_USBI2C, 0, 0, buf, buf.Length, out int got);
        return (ret, ret == 0 ? got : 0);
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        var buf = new byte[64];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                (int ret, int got) = await Task.Run(() => ReadSlave(buf), ct);
                if (ct.IsCancellationRequested) break;
                if (ret == 0 && got > 0)
                {
                    _fails = 0;
                    var data = new byte[got];
                    Array.Copy(buf, data, got);
                    Dispatcher.BeginInvoke(() => AddRxLine(data));
                    Dbg.Log($"SlavePage.RX: {got} B from master");
                }
                else if (ret == -7)
                {
                    _fails = 0; // 无数据是从机常态
                }
                else if (++_fails >= 5)
                {
                    int lastRet = ret;
                    Dispatcher.BeginInvoke(() =>
                    {
                        SetState($"连续 {_fails} 次失败，从机已自动停止（{GinkgoDriver.ErrorName(lastRet)}）", false);
                        StopSession(announce: false);
                    });
                    return;
                }
                await Task.Delay(PollIntervalMs, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void AddRxLine(byte[] data)
    {
        _rxCount++;
        TxtRxCount.Text = $" · {_rxCount} 次";
        _rx.Add(new SlaveRxLine(DateTime.Now.ToString("HH:mm:ss.fff"),
            $"{data.Length} B · {GinkgoDriver.Hex(data)}"));
        if (_rx.Count > MaxRxLines) _rx.RemoveAt(0);
        if (_rx.Count > 0) LstSlaveRx.ScrollIntoView(LstSlaveRx.Items[^1]);
    }

    private async void BtnLoadResp_Click(object sender, RoutedEventArgs e)
    {
        byte[] resp = ParseRespBuffer();
        int ret = await Task.Run(() => GinkgoDriver.VII_SlaveWriteBytes(GinkgoDriver.VII_USBI2C, 0, 0, resp, resp.Length));
        if (SessionActive)
        {
            TxtRespState.Text = ret == 0
                ? $"已装载 {resp.Length} B 到从机缓冲"
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

    private void SetState(string text, bool ok)
    {
        TxtSlaveState.Text = text;
        TxtSlaveState.SetResourceReference(TextBlock.ForegroundProperty,
            ok ? "StatusSuccessBrush" : "StatusErrorBrush");
    }
}
