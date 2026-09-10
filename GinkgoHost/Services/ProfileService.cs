using System.IO;
using System.Text.Json;
using GinkgoHost.Models;

namespace GinkgoHost.Services;

/// <summary>
/// 扩展页 profile：寄存器表 + 初始化序列，按名字存取 JSON。
/// 存放于 %APPDATA%\GinkgoHost\profiles\，按器件可建多个（如 sensor-v2）。
/// </summary>
public static class ProfileService
{
    public sealed record Profile(string Name, List<RegRow> RegTable, List<RegRow> InitSequence);

    private static string Dir()
    {
        string d = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GinkgoHost", "profiles");
        Directory.CreateDirectory(d);
        return d;
    }

    public static IReadOnlyList<string> List() =>
        Directory.GetFiles(Dir(), "*.json")
                 .Select(p => Path.GetFileNameWithoutExtension(p)!)
                 .OrderBy(n => n).ToList();

    public static void Save(string name, IReadOnlyList<RegRow> regTable, IReadOnlyList<RegRow> initSeq)
    {
        var p = new Profile(name, [.. regTable], [.. initSeq]);
        File.WriteAllText(Path.Combine(Dir(), name + ".json"),
            JsonSerializer.Serialize(p, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static Profile? Load(string name)
    {
        string path = Path.Combine(Dir(), name + ".json");
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<Profile>(File.ReadAllText(path));
    }

    public static void Delete(string name)
    {
        string path = Path.Combine(Dir(), name + ".json");
        if (File.Exists(path)) File.Delete(path);
    }
}
