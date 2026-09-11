using System.IO;
using System.Text.Json;
using GinkgoHost.Models;

namespace GinkgoHost.Services;

/// <summary>用户设置，持久化到 %APPDATA%\GinkgoHost\settings.json。</summary>
public sealed class SettingsService
{
    public string Theme { get; set; } = "Dark";
    public int Channel { get; set; } = 0;
    public uint ClockHz { get; set; } = 400_000;
    public string LastAddr { get; set; } = "50";
    public string LastSubAddr { get; set; } = "00";
    public int AddrFmt { get; set; } = 0; // 0=7-bit 1=8-bit，与地址框一起持久化
    public int ControlMode { get; set; } = 1; // 1=硬件 I2C 2=软件 I2C（GPIO）
    public double ExtPanelWidth { get; set; } = 480;
    public double LogPanelRatio { get; set; } = 0.65;
    public int LastPage { get; set; } = 1;
    public bool NavOpen { get; set; } = true;

    /// <summary>上次关闭时的窗口位置与尺寸；NaN/0 表示首次启动，走系统居中。</summary>
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 0;
    public double WindowHeight { get; set; } = 0;
    public bool WindowMaximized { get; set; } = false;

    public List<I2cTarget> I2cTargets { get; set; } = [];

    private static string FilePath()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GinkgoHost");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "settings.json");
    }

    public static SettingsService Load()
    {
        try
        {
            string path = FilePath();
            if (!File.Exists(path)) return new SettingsService();
            return JsonSerializer.Deserialize<SettingsService>(File.ReadAllText(path)) ?? new SettingsService();
        }
        catch
        {
            return new SettingsService(); // 设置文件损坏不阻塞启动
        }
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath(),
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 保存失败仅丢失记忆参数，不影响功能
        }
    }
}
