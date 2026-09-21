using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace GinkgoHost.Services;

/// <summary>
/// GitHub Releases 最新版检查。结果缓存于静态任务：启动时点一次火，
/// 设置页打开时取同一任务的结果，不重复请求。网络失败一律静默——
/// 单机调试工具不应为检查更新弹任何错误。
/// </summary>
public static class UpdateService
{
    public const string Repo = "Hush-xv/GinkgoHost";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private static Task<UpdateInfo?>? _pending;

    public sealed record UpdateInfo(string Tag, string Url);

    /// <summary>当前版本（InformationalVersion，如 0.9.7-beta；去掉 SDK 附加的 +提交哈希尾巴）。</summary>
    public static string CurrentVersion
    {
        get
        {
            string? v = Assembly.GetEntryAssembly()
                ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return v?.Split('+')[0] ?? "?";
        }
    }

    /// <summary>启动时调用一次；重复调用返回同一任务。</summary>
    public static Task<UpdateInfo?> GetOrStartCheckAsync() =>
        _pending ??= CheckCoreAsync();

    /// <summary>用户点“检查更新”：绕过缓存强制查一次。</summary>
    public static Task<UpdateInfo?> ForceCheckAsync()
    {
        _pending = CheckCoreAsync();
        return _pending;
    }

    /// <summary>latest 是否比 current 新；tag/版本允许带 -beta 后缀。</summary>
    public static bool IsNewer(string tag)
    {
        string current = CurrentVersion;
        if (!Version.TryParse(tag.TrimStart('v', 'V').Split('-')[0], out var latest)) return false;
        if (!Version.TryParse(current.Split('-')[0], out var cur)) return false;
        return latest > cur;
    }

    private static async Task<UpdateInfo?> CheckCoreAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/{Repo}/releases/latest");
            req.Headers.UserAgent.ParseAdd("GinkgoHost-UpdateCheck");
            using var res = await Http.SendAsync(req);
            if (!res.IsSuccessStatusCode) return null;
            await using var stream = await res.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            string url = doc.RootElement.GetProperty("html_url").GetString() ?? "";
            if (tag.Length == 0 || !IsNewer(tag)) return null;
#if DEBUG
            Native.Dbg.Log($"UpdateService: latest={tag} current={CurrentVersion}");
#endif
            return new UpdateInfo(tag, url);
        }
        catch
        {
            return null; // 离线/代理未开/限流：静默
        }
    }
}
