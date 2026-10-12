using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenShape;

/// <summary>
/// Autodesk Platform Services: converts a local Fusion design (.f3d) to STEP with the Model Derivative
/// service, using the APS app already configured for the Fusion cloud sync
/// (~/fusion-webviewer/cloud_sync/config.json). Translating .f3d may use Autodesk Flex tokens.
/// </summary>
public static class Aps
{
    private const string Base = "https://developer.api.autodesk.com";
    public static string ConfigFile => Path.Combine(IsaacLink.Home, "fusion-webviewer", "cloud_sync", "config.json");

    public static bool Configured
    {
        get
        {
            try { return Credentials() is { } c && c.id.Length > 0 && !c.id.StartsWith('<'); }
            catch { return false; }
        }
    }

    private static (string id, string secret)? Credentials()
    {
        if (!File.Exists(ConfigFile)) return null;
        var o = JsonNode.Parse(File.ReadAllText(ConfigFile));
        var id = o?["client_id"]?.GetValue<string>();
        var secret = o?["client_secret"]?.GetValue<string>();
        return id == null || secret == null ? null : (id, secret);
    }

    public static async Task<string> Token(HttpClient http, CancellationToken ct = default)
    {
        var (id, secret) = Credentials() ?? throw new InvalidOperationException($"APS 설정이 없습니다: {ConfigFile}");
        using var req = new HttpRequestMessage(HttpMethod.Post, Base + "/authentication/v2/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["scope"] = "data:read data:write data:create bucket:create bucket:read",
            }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{id}:{secret}")));
        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"APS 인증 실패 ({(int)res.StatusCode}): {body}");
        return JsonNode.Parse(body)!["access_token"]!.GetValue<string>();
    }

    private static async Task<JsonNode> Json(HttpClient http, HttpMethod m, string url, string token, object? payload = null, CancellationToken ct = default, bool allowConflict = false)
    {
        using var req = new HttpRequestMessage(m, url.StartsWith("http") ? url : Base + url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (payload != null) req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (allowConflict && res.StatusCode == HttpStatusCode.Conflict) return new JsonObject();
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"APS 요청 실패 ({(int)res.StatusCode} {url}): {body}");
        return JsonNode.Parse(body.Length == 0 ? "{}" : body)!;
    }

    private static string? FindStep(JsonNode? node)
    {
        if (node == null) return null;
        var urn = node["urn"]?.GetValue<string>();
        if (urn != null && (urn.EndsWith(".step", StringComparison.OrdinalIgnoreCase) || urn.EndsWith(".stp", StringComparison.OrdinalIgnoreCase))) return urn;
        foreach (var key in new[] { "derivatives", "children" })
            if (node[key] is JsonArray arr)
                foreach (var c in arr)
                    if (FindStep(c) is { } u) return u;
        return null;
    }

    /// <summary>Upload the .f3d, translate it to STEP and download the result next to the cloud designs.</summary>
    public static async Task<string> F3dToStep(string f3d, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        progress?.Report("Autodesk 인증 중...");
        var token = await Token(http, ct);
        var (id, _) = Credentials()!.Value;
        var bucket = "openshape-" + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(id)))[..16].ToLowerInvariant();
        await Json(http, HttpMethod.Post, "/oss/v2/buckets", token, new { bucketKey = bucket, policyKey = "transient" }, ct, allowConflict: true);

        progress?.Report("파일 올리는 중...");
        var key = Uri.EscapeDataString(Path.GetFileName(f3d));
        var signed = await Json(http, HttpMethod.Get, $"/oss/v2/buckets/{bucket}/objects/{key}/signeds3upload", token, ct: ct);
        var uploadUrl = signed["urls"]![0]!.GetValue<string>();
        using (var put = new ByteArrayContent(await File.ReadAllBytesAsync(f3d, ct)))
        using (var res = await http.PutAsync(uploadUrl, put, ct))
            if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"업로드 실패 ({(int)res.StatusCode})");
        var done = await Json(http, HttpMethod.Post, $"/oss/v2/buckets/{bucket}/objects/{key}/signeds3upload", token, new { uploadKey = signed["uploadKey"]!.GetValue<string>() }, ct);
        var objectId = done["objectId"]!.GetValue<string>();
        var urn = Convert.ToBase64String(Encoding.UTF8.GetBytes(objectId)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        progress?.Report("STEP으로 변환 중 (몇 분 걸릴 수 있습니다)...");
        await Json(http, HttpMethod.Post, "/modelderivative/v2/designdata/job", token,
            new { input = new { urn }, output = new { formats = new[] { new { type = "step" } } } }, ct);
        string? stepUrn = null;
        var until = DateTime.UtcNow.AddMinutes(15);
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(5000, ct);
            var manifest = await Json(http, HttpMethod.Get, $"/modelderivative/v2/designdata/{urn}/manifest", token, ct: ct);
            var status = manifest["status"]?.GetValue<string>();
            progress?.Report($"STEP으로 변환 중... {manifest["progress"]}");
            if (status == "failed") throw new InvalidOperationException("Autodesk에서 변환에 실패했습니다.");
            stepUrn = FindStep(manifest);
            if (status == "success" && stepUrn != null) break;
        }
        if (stepUrn == null) throw new InvalidOperationException("변환 시간이 초과되었습니다.");

        progress?.Report("STEP 내려받는 중...");
        using var cookieReq = new HttpRequestMessage(HttpMethod.Get, $"{Base}/modelderivative/v2/designdata/{urn}/manifest/{Uri.EscapeDataString(stepUrn)}/signedcookies");
        cookieReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var cookieRes = await http.SendAsync(cookieReq, ct);
        var cookieBody = await cookieRes.Content.ReadAsStringAsync(ct);
        if (!cookieRes.IsSuccessStatusCode) throw new InvalidOperationException($"내려받기 준비 실패: {cookieBody}");
        var url = JsonNode.Parse(cookieBody)!["url"]!.GetValue<string>();
        using var get = new HttpRequestMessage(HttpMethod.Get, url);
        if (cookieRes.Headers.TryGetValues("Set-Cookie", out var cookies))
            get.Headers.Add("Cookie", string.Join("; ", cookies.Select(c => c.Split(';')[0])));
        using var file = await http.SendAsync(get, ct);
        if (!file.IsSuccessStatusCode) throw new InvalidOperationException($"내려받기 실패 ({(int)file.StatusCode})");
        var name = IsaacLink.SafeName(Path.GetFileNameWithoutExtension(f3d));
        var dir = Path.Combine(IsaacLink.Exchange, "cloud", name);
        Directory.CreateDirectory(dir);
        var outPath = Path.Combine(dir, name + ".step");
        await File.WriteAllBytesAsync(outPath, await file.Content.ReadAsByteArrayAsync(ct), ct);
        return outPath;
    }
}
