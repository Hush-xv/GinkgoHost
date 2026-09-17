using System.IO;
using System.Text.Json;
using GinkgoHost.Models;
using GinkgoHost.Native;

namespace GinkgoHost.Services;

/// <summary>
/// 扩展页 profile：寄存器表 + 初始化序列，按名字存取 JSON。
/// 存放于 %APPDATA%\GinkgoHost\profiles\，按器件可建多个（如 sensor-v2）。
/// </summary>
public static class ProfileService
{
    public sealed record Profile(string Name, List<RegRow> RegTable, List<RegRow> InitSequence);

    public static bool IsValidName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name is not "." and not ".." &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        !name.Contains(Path.DirectorySeparatorChar) && !name.Contains(Path.AltDirectorySeparatorChar);

    private static string Dir()
    {
        string d = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GinkgoHost", "profiles");
        Directory.CreateDirectory(d);
        return d;
    }

    private static string PathFor(string name)
    {
        if (!IsValidName(name))
            throw new ArgumentException("Profile 名不合法", nameof(name));
        return Path.Combine(Dir(), name + ".json");
    }

    public static IReadOnlyList<string> List()
    {
        try
        {
            return Directory.GetFiles(Dir(), "*.json")
                            .Select(p => Path.GetFileNameWithoutExtension(p)!)
                            .OrderBy(n => n).ToList();
        }
        catch (Exception ex)
        {
#if DEBUG
            Dbg.Log($"ProfileService.List: failed={ex.Message}");
#else
            _ = ex;
#endif
            return [];
        }
    }

    public static void Save(string name, IReadOnlyList<RegRow> regTable, IReadOnlyList<RegRow> initSeq)
    {
        string? tempPath = null;
        try
        {
            string path = PathFor(name);
            tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var profile = new Profile(name, [.. regTable], [.. initSeq]);
            File.WriteAllText(tempPath,
                JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tempPath, path, overwrite: true);
            tempPath = null;
#if DEBUG
            Dbg.Log($"ProfileService.Save: saved name={name} registers={regTable.Count} init={initSeq.Count}");
#endif
        }
        finally
        {
            if (tempPath is not null)
            {
                try { File.Delete(tempPath); }
                catch { /* 临时文件清理失败不影响原 Profile。 */ }
            }
        }
    }

    public static Profile? Load(string name)
    {
        string path = PathFor(name);
        if (!File.Exists(path)) return null;
        Profile? profile = JsonSerializer.Deserialize<Profile>(File.ReadAllText(path));
        if (profile is null || profile.RegTable is null)
        {
            Dbg.Log($"ProfileService.Load: invalid profile data name={name}");
            throw new InvalidDataException("Profile 缺少寄存器表数据");
        }
        profile = profile with { InitSequence = profile.InitSequence ?? [] };
#if DEBUG
        Dbg.Log($"ProfileService.Load: loaded name={name} registers={profile.RegTable.Count} init={profile.InitSequence.Count}");
#endif
        return profile;
    }

    public static void Delete(string name)
    {
        string path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);
    }
}
