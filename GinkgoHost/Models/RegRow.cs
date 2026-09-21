using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace GinkgoHost.Models;

/// <summary>扩展页寄存器表/初始化序列共用行模型。Reg/Value 均为 hex 字符串。</summary>
public sealed class RegRow : INotifyPropertyChanged
{
    private string _reg = "00";
    private int _len = 1;
    private int _periodMs;
    private string _dir = "R";
    private string _value = "";
    private int _delayMs;
    private string _note = "";
    private string _status = "";
    private bool _polling;
    private int _pollCount;
    private bool _canExecute;
    private string _executeText = "执行";
    private string _executeToolTip = "执行此行";
    private bool _snapshotChanged;
    private string? _snapshotBaseline;
    private double? _lastDurationMs;
    private string _statusDetail = "";

    public string Reg { get => _reg; set => SetField(ref _reg, value); }
    public int Len { get => _len; set => SetField(ref _len, value); }
    /// <summary>周期轮询间隔 ms；0 = 不轮询。周期触发按此调度。</summary>
    public int PeriodMs { get => _periodMs; set => SetField(ref _periodMs, value); }
    /// <summary>R=读 W=写。初始化序列固定按写处理。</summary>
    public string Dir { get => _dir; set => SetField(ref _dir, value); }
    /// <summary>hex 字节串，空格分隔（如 "DE AD"）。</summary>
    public string Value
    {
        get => _value;
        set
        {
            if (string.Equals(_value, value, StringComparison.Ordinal)) return;
            _value = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SnapshotHint));
        }
    }
    /// <summary>初始化序列中该行执行前的延时。</summary>
    public int DelayMs { get => _delayMs; set => SetField(ref _delayMs, value); }
    public string Note { get => _note; set => SetField(ref _note, value); }

    /// <summary>最近一次执行结果：OK / ERR xx / 空。驱动行底色。</summary>
    [JsonIgnore]
    public string Status
    {
        get => _status;
        set
        {
            if (string.Equals(_status, value, StringComparison.Ordinal)) return;
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusDisplay));
            OnPropertyChanged(nameof(StatusHint));
        }
    }

    [JsonIgnore]
    public double? LastDurationMs
    {
        get => _lastDurationMs;
        set
        {
            if (_lastDurationMs == value) return;
            _lastDurationMs = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusDisplay));
            OnPropertyChanged(nameof(StatusHint));
        }
    }

    [JsonIgnore]
    public string StatusDetail
    {
        get => _statusDetail;
        set
        {
            if (string.Equals(_statusDetail, value, StringComparison.Ordinal)) return;
            _statusDetail = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusHint));
        }
    }

    [JsonIgnore]
    public string StatusDisplay => string.IsNullOrEmpty(_status)
        ? string.Empty
        : _lastDurationMs is double ms ? $"{_status} {ms:F1}" : _status;

    [JsonIgnore]
    public string StatusHint => string.Join(" · ", new[]
    {
        _status switch { "OK" => "执行成功", "ERR" => "执行失败", "INPUT" => "输入有误", _ => _status },
        _lastDurationMs is double ms ? $"耗时 {ms:F1} ms" : string.Empty,
        _statusDetail
    }.Where(part => !string.IsNullOrWhiteSpace(part)));

    /// <summary>周期轮询运行中标记。驱动行高亮；不落盘。</summary>
    [JsonIgnore]
    public bool Polling { get => _polling; set => SetField(ref _polling, value); }

    /// <summary>本轮周期轮询已执行次数，显示在执行按钮上。</summary>
    [JsonIgnore]
    public int PollCount { get => _pollCount; set => SetField(ref _pollCount, value); }

    /// <summary>行内操作可用性；轮询行在全局操作锁定时仍可点击停止。</summary>
    [JsonIgnore]
    public bool CanExecute { get => _canExecute; set => SetField(ref _canExecute, value); }

    /// <summary>行内操作文本与提示，不依赖虚拟化 DataGrid 单元格实例。</summary>
    [JsonIgnore]
    public string ExecuteText { get => _executeText; set => SetField(ref _executeText, value); }

    [JsonIgnore]
    public string ExecuteToolTip { get => _executeToolTip; set => SetField(ref _executeToolTip, value); }

    /// <summary>最近一次“读取全部”相对当前快照发生变化；只驱动界面，不落盘。</summary>
    [JsonIgnore]
    public bool SnapshotChanged
    {
        get => _snapshotChanged;
        set
        {
            if (_snapshotChanged == value) return;
            _snapshotChanged = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SnapshotHint));
        }
    }

    [JsonIgnore]
    public string? SnapshotBaseline
    {
        get => _snapshotBaseline;
        set
        {
            if (string.Equals(_snapshotBaseline, value, StringComparison.Ordinal)) return;
            _snapshotBaseline = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SnapshotHint));
        }
    }

    [JsonIgnore]
    public string SnapshotHint => string.IsNullOrWhiteSpace(_snapshotBaseline)
        ? _value
        : _snapshotChanged ? $"快照 {_snapshotBaseline}  →  当前 {_value}" : $"与快照一致 · {_value}";

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>相同值不重复通知，避免周期读取和全局可用性刷新触发无效的 DataGrid 重绘。</summary>
    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
