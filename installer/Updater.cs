using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace OpenShapeInstaller;

public sealed record ReleaseInfo(string Version, string AssetName, string AssetUrl, long Size, string Notes);

/// <summary>
/// Streaming installer logic (like Autodesk's "Fusion Client Downloader"): finds the newest
/// OpenShape release on GitHub, downloads its .deb with progress and installs it with pkexec.
/// The repository is private, so the GitHub CLI login on this machine (or GH_TOKEN) is used.
/// </summary>
public static class Updater
{
    public const string Repo = "a71143055/openshape";

    private static string? RunCapture(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            if (p == null) return null;
            var o = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            return p.ExitCode == 0 ? o.Trim() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>GH_TOKEN / GITHUB_TOKEN, or the token of the gh CLI login (PATH or ~/.local/bin).</summary>
    public static string? Token()
    {
        foreach (var v in new[] { "GH_TOKEN", "GITHUB_TOKEN" })
            if (Environment.GetEnvironmentVariable(v) is { Length: > 0 } t) return t;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var gh in new[] { "gh", Path.Combine(home, ".local", "bin", "gh"), "/usr/bin/gh", "/snap/bin/gh" })
            if (RunCapture(gh, "auth token") is { Length: > 0 } t) return t;
        return null;
    }

    private static HttpClient Client(string? token)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OpenShape-Client-Downloader/1.1");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        if (token != null) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    public static async Task<ReleaseInfo> Latest(CancellationToken ct = default)
    {
        var token = Token();
        using var http = Client(token);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException(token == null
                ? "GitHub 레포에 접근할 수 없습니다. 비공개 레포이므로 'gh auth login'으로 로그인하거나 GH_TOKEN을 설정하세요."
                : "릴리스를 찾을 수 없습니다.");
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"GitHub 응답 오류 ({(int)res.StatusCode})");
        var o = JsonNode.Parse(body)!;
        var tag = o["tag_name"]!.GetValue<string>().TrimStart('v');
        var asset = (o["assets"] as JsonArray)?.FirstOrDefault(a => a!["name"]!.GetValue<string>().EndsWith("_amd64.deb"))
                    ?? throw new InvalidOperationException("릴리스에 .deb 파일이 없습니다.");
        return new ReleaseInfo(tag, asset["name"]!.GetValue<string>(), asset["url"]!.GetValue<string>(), asset["size"]!.GetValue<long>(), o["body"]?.GetValue<string>() ?? "");
    }

    /// <summary>Installed version (dpkg), or null.</summary>
    public static string? Installed() => RunCapture("dpkg-query", "-W -f=${Version} openshape") is { Length: > 0 } v ? v : null;

    public static bool IsNewer(string latest, string? installed)
    {
        if (installed == null) return true;
        return Version.TryParse(latest, out var a) && Version.TryParse(installed, out var b) ? a > b : latest != installed;
    }

    /// <summary>Download the release .deb; progress reports (bytes, total).</summary>
    public static async Task<string> Download(ReleaseInfo r, string dir, IProgress<(long, long)>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, r.AssetName);
        using var http = Client(Token());
        using var req = new HttpRequestMessage(HttpMethod.Get, r.AssetUrl);
        req.Headers.Accept.ParseAdd("application/octet-stream");
        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"다운로드 실패 ({(int)res.StatusCode})");
        long total = res.Content.Headers.ContentLength ?? r.Size;
        await using var src = await res.Content.ReadAsStreamAsync(ct);
        await using (var dst = File.Create(path + ".part"))
        {
            var buf = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                progress?.Report((done, total));
            }
        }
        File.Move(path + ".part", path, true);
        if (new FileInfo(path).Length != r.Size) throw new InvalidOperationException("내려받은 파일 크기가 맞지 않습니다.");
        return path;
    }

    /// <summary>Install the .deb with apt through pkexec (graphical password prompt). Returns the exit code and output.</summary>
    public static async Task<(int code, string output)> Install(string deb, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo("pkexec", $"apt-get install -y --allow-downgrades \"{deb}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["DEBIAN_FRONTEND"] = "noninteractive";
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("pkexec를 실행할 수 없습니다.");
        var output = await p.StandardOutput.ReadToEndAsync(ct) + await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, output);
    }

    public static void Launch()
    {
        try
        {
            Process.Start(new ProcessStartInfo("openshape") { UseShellExecute = false });
        }
        catch (Exception)
        {
            // not installed yet or not on PATH
        }
    }
}
