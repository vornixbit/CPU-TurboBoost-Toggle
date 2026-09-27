using System.Globalization;
using System.Text.Json;
namespace TurboToggle;
static class Updater
{
    const string Owner = "vornixbit";
    const string Repo = "CPU-TurboBoost-Toggle";
    static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };
    const long MaxResponseBytes = 1024 * 1024;
    static Updater()
    {
        var ver = Program.VersionText.Trim();
        var ua = string.IsNullOrEmpty(ver) ? "TurboToggle" : $"TurboToggle/{ver}";
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(ua);
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }
    public enum Status { Available, UpToDate, NoReleases, Failed }
    public sealed record Result(Status Status, string Tag, string Url);
    static bool IsGithubHttps(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);
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
        int n0 = 0, n1 = 0, n2 = 0, n3 = 0;
        if (!TryParsePart(parts[0], out n0)) return false;
        if (parts.Length > 1 && !TryParsePart(parts[1], out n1)) return false;
        if (parts.Length > 2 && !TryParsePart(parts[2], out n2)) return false;
        if (parts.Length > 3 && !TryParsePart(parts[3], out n3)) return false;
        version = new Version(n0, n1, n2, n3);
        return true;
    }
    static bool TryParsePart(string part, out int value) =>
        int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= 0;
    public static async Task<Result> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await Http.GetAsync(
                $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest",
                HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new Result(Status.NoReleases, "", "");
            if (!response.IsSuccessStatusCode)
                return new Result(Status.Failed, "", "");
            if (response.Content.Headers.ContentLength is { } length && length > MaxResponseBytes)
                return new Result(Status.Failed, "", "");
            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            long total = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > MaxResponseBytes)
                    return new Result(Status.Failed, "", "");
                body.Write(buffer, 0, read);
            }
            body.Position = 0;
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            string url = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "";
            if (!IsGithubHttps(url))
                url = $"https://github.com/{Owner}/{Repo}/releases";
            if (!TryParseTag(tag, out var latest))
                return new Result(Status.Failed, "", url);
            return latest > Program.CurrentVersion
                ? new Result(Status.Available, tag, url)
                : new Result(Status.UpToDate, tag, url);
        }
        catch (OperationCanceledException)
        {
            return new Result(Status.Failed, "", "");
        }
        catch (HttpRequestException)
        {
            return new Result(Status.Failed, "", "");
        }
        catch (ObjectDisposedException)
        {
            return new Result(Status.Failed, "", "");
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
            return new Result(Status.Failed, "", "");
        }
    }
}
