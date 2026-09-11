using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TurboToggle;

static class Updater
{
    const string Owner = "vornixbit";
    const string Repo = "CPU-TurboBoost-Toggle";

    static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    static readonly Version _currentVersion = ComputeCurrentVersion();

    static Updater()
    {
        var ver = Program.VersionText.Trim();
        var ua = string.IsNullOrEmpty(ver) ? "TurboToggle" : $"TurboToggle/{ver}";
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(ua);
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public enum Status { Available, UpToDate, NoReleases, Failed }

    public sealed record Result(Status Status, string Tag, string Url);

    public static Version CurrentVersion => _currentVersion;

    static Version ComputeCurrentVersion()
    {
        try
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            if (v is null)
                return new Version(1, 0);
            return new Version(
                Math.Max(v.Major, 0),
                Math.Max(v.Minor, 0),
                Math.Max(v.Build, 0),
                Math.Max(v.Revision, 0));
        }
        catch
        {
            return new Version(1, 0);
        }
    }

    public static bool TryParseTag(string? tag, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(tag))
            return false;
        string s = tag.Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
            s = s[1..];
        s = s.Split('-', '+')[0];
        var parts = s.Split('.');
        if (parts.Length > 4)
            return false;
        int[] nums = { 0, 0, 0, 0 };
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out nums[i]) || nums[i] < 0)
                return false;
        }
        version = new Version(nums[0], nums[1], nums[2], nums[3]);
        return true;
    }

    public static async Task<Result> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await Http.GetAsync(
                $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest", ct).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new Result(Status.NoReleases, "", "");
            if (!response.IsSuccessStatusCode)
                return new Result(Status.Failed, "", "");

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = doc.RootElement;
            string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            string url = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(url))
                url = $"https://github.com/{Owner}/{Repo}/releases";

            if (!TryParseTag(tag, out var latest))
                return new Result(Status.Failed, "", url);
            return latest > CurrentVersion
                ? new Result(Status.Available, tag, url)
                : new Result(Status.UpToDate, tag, url);
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
            return new Result(Status.Failed, "", "");
        }
    }
}
