using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace SettingsSync;

public sealed class GitHubException : Exception
{
    public HttpStatusCode Status { get; }
    public GitHubException(HttpStatusCode status, string message) : base(message) => Status = status;
}

public sealed record RemoteFile(string Path, string Sha, string Mode);
public sealed record RemoteHead(string CommitSha, string TreeSha, List<RemoteFile> Files);
public sealed record CommitInfo(string Sha, string Message, DateTime When)
{
    /// <summary>The PC that pushed it ("Push from Desktop: ..."), or null for restores and other commits.</summary>
    public string? PushedBy => Message.StartsWith("Push from ", StringComparison.Ordinal) && Message.IndexOf(':') is var c and > 10
        ? Message[10..c] : null;
}
public sealed record DeviceCode(string Code, string Url, string DeviceCodeValue, int IntervalSeconds, DateTime Expires);

/// <summary>Just the GitHub REST calls the plugin needs (device sign-in and the git data API).</summary>
public sealed class GitHub : IDisposable
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public GitHub()
    {
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SettingsSync-Dalamud", "1.0"));
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public void Dispose() => http.Dispose();

    // ------------------------------------------------------------------ device sign-in

    public async Task<DeviceCode> StartDeviceFlow(string clientId, CancellationToken ct)
    {
        var j = await PostForm("https://github.com/login/device/code", new() { ["client_id"] = clientId }, ct);
        if (j["error"] != null) throw new GitHubException(HttpStatusCode.BadRequest, Describe(j));
        return new DeviceCode(
            (string)j["user_code"]!, (string?)j["verification_uri"] ?? "https://github.com/login/device",
            (string)j["device_code"]!, (int?)j["interval"] ?? 5,
            DateTime.UtcNow.AddSeconds((int?)j["expires_in"] ?? 900));
    }

    /// <summary>Polls until you enter the code on github.com. Returns the access token.</summary>
    public async Task<string> FinishDeviceFlow(string clientId, DeviceCode code, CancellationToken ct)
    {
        var interval = Math.Max(code.IntervalSeconds, 5);
        while (DateTime.UtcNow < code.Expires)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);
            var j = await PostForm("https://github.com/login/oauth/access_token", new()
            {
                ["client_id"] = clientId,
                ["device_code"] = code.DeviceCodeValue,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            }, ct);
            var err = (string?)j["error"];
            if (err == null) return (string)j["access_token"]!;
            if (err == "authorization_pending") continue;
            if (err == "slow_down") { interval = ((int?)j["interval"]) ?? interval + 5; continue; }
            throw new GitHubException(HttpStatusCode.Unauthorized, Describe(j));
        }
        throw new GitHubException(HttpStatusCode.RequestTimeout, "The code expired. Start the sign-in again.");
    }

    private static string Describe(JsonNode j) => err((string?)j["error"]) + (j["error_description"] is { } d ? $" ({d})" : "");
    private static string err(string? e) => e switch
    {
        "access_denied" => "Sign-in was cancelled on GitHub.",
        "expired_token" => "The code expired. Start the sign-in again.",
        "device_flow_disabled" => "Device sign-in is off: tick 'Enable Device Flow' in the GitHub App's settings.",
        "incorrect_client_credentials" => "GitHub doesn't know that Client ID.",
        _ => e ?? "Unknown error",
    };

    private async Task<JsonNode> PostForm(string url, Dictionary<string, string> form, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        return JsonNode.Parse(body) ?? new JsonObject();
    }

    // ------------------------------------------------------------------ git data API

    public string Token { get; set; } = "";
    public string Repo { get; set; } = "";

    private async Task<JsonNode?> Api(HttpMethod method, string path, object? body, CancellationToken ct, bool allow404 = false)
    {
        using var req = new HttpRequestMessage(method, $"https://api.github.com/repos/{Repo}/{path}");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (allow404 && (res.StatusCode == HttpStatusCode.NotFound || res.StatusCode == HttpStatusCode.Conflict)) return null;
        if (!res.IsSuccessStatusCode)
        {
            var msg = (string?)(JsonNode.Parse(text.Length > 0 ? text : "{}")?["message"]) ?? res.ReasonPhrase ?? "";
            throw new GitHubException(res.StatusCode, res.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "GitHub sign-in is no longer valid. Sign in again.",
                HttpStatusCode.NotFound => $"GitHub can't see {Repo}. Check the repository name and that the GitHub App is installed on it.",
                HttpStatusCode.Forbidden => $"GitHub refused ({msg}). The GitHub App needs Contents: Read and write.",
                _ => $"GitHub error {(int)res.StatusCode}: {msg}",
            });
        }
        return text.Length == 0 ? null : JsonNode.Parse(text);
    }

    /// <summary>The branch head and every file in it, or null if the repository has no commits yet.</summary>
    public async Task<RemoteHead?> GetHead(string branch, CancellationToken ct)
    {
        var refJ = await Api(HttpMethod.Get, $"git/ref/heads/{branch}", null, ct, allow404: true);
        if (refJ == null) return null;
        var commitSha = (string)refJ["object"]!["sha"]!;
        var commit = await Api(HttpMethod.Get, $"git/commits/{commitSha}", null, ct);
        var treeSha = (string)commit!["tree"]!["sha"]!;
        return new RemoteHead(commitSha, treeSha, await GetTreeFiles(treeSha, ct));
    }

    /// <summary>An empty repository can't take blobs yet: give it a first commit (a README) on the branch.</summary>
    public async Task InitEmptyRepo(string branch, CancellationToken ct)
    {
        var text = "Plugin settings backed up by Settings Sync. Files live under pluginConfigs/.\n";
        await Api(HttpMethod.Put, "contents/README.md", new
        {
            message = "Start Settings Sync", branch,
            content = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)),
        }, ct);
    }

    public async Task<List<RemoteFile>> GetTreeFiles(string treeSha, CancellationToken ct)
    {
        var tree = await Api(HttpMethod.Get, $"git/trees/{treeSha}?recursive=1", null, ct);
        return tree!["tree"]!.AsArray()
            .Where(e => (string?)e!["type"] == "blob")
            .Select(e => new RemoteFile((string)e!["path"]!, (string)e["sha"]!, (string)e["mode"]!))
            .ToList();
    }

    public async Task<string> GetCommitTree(string commitSha, CancellationToken ct)
    {
        var c = await Api(HttpMethod.Get, $"git/commits/{commitSha}", null, ct);
        return (string)c!["tree"]!["sha"]!;
    }

    public async Task<byte[]> GetBlob(string sha, CancellationToken ct)
    {
        var b = await Api(HttpMethod.Get, $"git/blobs/{sha}", null, ct);
        return Convert.FromBase64String(((string)b!["content"]!).Replace("\n", ""));
    }

    public async Task<string> CreateBlob(byte[] data, CancellationToken ct)
    {
        var b = await Api(HttpMethod.Post, "git/blobs", new { content = Convert.ToBase64String(data), encoding = "base64" }, ct);
        return (string)b!["sha"]!;
    }

    /// <summary>Entries: path -> blob sha, or null to delete that path from the base tree.</summary>
    public Task<string> CreateTree(string? baseTree, IEnumerable<KeyValuePair<string, string?>> entries, CancellationToken ct) =>
        CreateTreeRaw(baseTree, entries.Select(e => new Dictionary<string, object?>
        {
            ["path"] = e.Key, ["mode"] = "100644", ["type"] = "blob", ["sha"] = e.Value,
        }), ct);

    /// <summary>
    /// Text files go inside the tree request itself (GitHub makes the blobs), in batches of a few MB, so
    /// a first push of hundreds of files is a handful of requests instead of one per file (GitHub
    /// throttles content creation to about 80 requests a minute).
    /// </summary>
    public async Task<string> CreateTreeWithText(string? baseTree, IReadOnlyList<KeyValuePair<string, string>> textFiles,
        IEnumerable<KeyValuePair<string, string?>> shaEntries, CancellationToken ct)
    {
        var tree = baseTree;
        var batch = new List<Dictionary<string, object?>>();
        long size = 0;
        foreach (var e in shaEntries)
            batch.Add(new() { ["path"] = e.Key, ["mode"] = "100644", ["type"] = "blob", ["sha"] = e.Value });
        foreach (var (path, text) in textFiles)
        {
            if (size > 4_000_000 && batch.Count > 0) { tree = await CreateTreeRaw(tree, batch, ct); batch.Clear(); size = 0; }
            batch.Add(new() { ["path"] = path, ["mode"] = "100644", ["type"] = "blob", ["content"] = text });
            size += text.Length;
        }
        if (batch.Count > 0 || tree == null) tree = await CreateTreeRaw(tree, batch, ct);
        return tree!;
    }

    private async Task<string> CreateTreeRaw(string? baseTree, IEnumerable<Dictionary<string, object?>> entries, CancellationToken ct)
    {
        var list = entries.ToList();
        object body = baseTree == null ? new { tree = list } : new { base_tree = baseTree, tree = list };
        var t = await Api(HttpMethod.Post, "git/trees", body, ct);
        return (string)t!["sha"]!;
    }

    /// <summary>
    /// Downloads the whole commit as one archive and returns the wanted files (repo paths). Used for big
    /// pulls, e.g. the first sync on a new PC, instead of one request per file.
    /// </summary>
    public async Task<Dictionary<string, byte[]>> GetFilesFromArchive(string commitSha, ISet<string> wanted, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/tarball/{commitSha}");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode) throw new GitHubException(res.StatusCode, $"GitHub archive download failed ({(int)res.StatusCode}).");
        await using var gz = new System.IO.Compression.GZipStream(await res.Content.ReadAsStreamAsync(ct), System.IO.Compression.CompressionMode.Decompress);
        using var tar = new System.Formats.Tar.TarReader(gz);
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        while (await tar.GetNextEntryAsync(copyData: false, ct) is { } entry)
        {
            if (entry.EntryType is not (System.Formats.Tar.TarEntryType.RegularFile or System.Formats.Tar.TarEntryType.V7RegularFile)) continue;
            var slash = entry.Name.IndexOf('/');                  // archive paths start with "owner-repo-sha/"
            if (slash < 0) continue;
            var path = entry.Name[(slash + 1)..];
            if (!wanted.Contains(path) || entry.DataStream == null) continue;
            using var ms = new System.IO.MemoryStream();
            await entry.DataStream.CopyToAsync(ms, ct);
            result[path] = ms.ToArray();
        }
        return result;
    }

    public async Task<string> CreateCommit(string message, string tree, string? parent, CancellationToken ct)
    {
        var c = await Api(HttpMethod.Post, "git/commits",
            new { message, tree, parents = parent == null ? Array.Empty<string>() : new[] { parent } }, ct);
        return (string)c!["sha"]!;
    }

    /// <summary>Moves the branch to the new commit. False if someone else pushed first (not a fast-forward).</summary>
    public async Task<bool> MoveBranch(string branch, string commitSha, bool create, CancellationToken ct)
    {
        try
        {
            if (create) await Api(HttpMethod.Post, "git/refs", new { @ref = $"refs/heads/{branch}", sha = commitSha }, ct);
            else await Api(HttpMethod.Patch, $"git/refs/heads/{branch}", new { sha = commitSha, force = false }, ct);
            return true;
        }
        catch (GitHubException e) when (e.Status == HttpStatusCode.UnprocessableEntity) { return false; }
    }

    public async Task<List<CommitInfo>> RecentCommits(string branch, int count, CancellationToken ct)
    {
        var arr = await Api(HttpMethod.Get, $"commits?sha={branch}&per_page={count}", null, ct, allow404: true);
        if (arr == null) return new();
        return arr.AsArray().Select(c => new CommitInfo(
            (string)c!["sha"]!,
            (string)c["commit"]!["message"]!,
            ((DateTime?)c["commit"]!["committer"]!["date"] ?? DateTime.UtcNow).ToLocalTime())).ToList();
    }
}
