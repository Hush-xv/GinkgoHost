using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace GinkgoHost.Models;

/// <summary>一个可复用的 I²C 从机目标。地址以 7-bit 保存。</summary>
public sealed class I2cTarget : INotifyPropertyChanged
{
    public byte Address7 { get; set; }
    public string Name { get; set; } = "";

    private string _display = "";
    [JsonIgnore]
    public string Display { get => _display; set { _display = value; OnPropertyChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
