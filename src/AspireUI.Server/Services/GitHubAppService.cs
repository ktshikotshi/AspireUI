using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AspireUI.Server.Services;

/// <summary>What is known about the GitHub App this instance created. The keys live in the secret store.</summary>
public record GitHubAppInfo(long AppId, string Slug, string ClientId, string Owner, string HtmlUrl,
    string BaseUrl, bool WebhookActive, string? CreatedAt = null)
{
    public string WebhookUrl => BaseUrl.TrimEnd('/') + "/api/github/webhook";
    public string CallbackUrl => BaseUrl.TrimEnd('/') + "/api/auth/github/callback";
    public string InstallUrl => HtmlUrl.TrimEnd('/') + "/installations/new";
}

public record GitHubRepo(string FullName, string CloneUrl, bool Private, string DefaultBranch);

public record GitHubPush(string FullName, string Branch, string DefaultBranch, string After);

/// <summary>
/// AspireUI as a GitHub App: one app per instance, created from a manifest so nobody copies a private
/// key around, and installed on the repositories it may deploy. It replaces the personal access token
/// pasted into every Git import with installation tokens minted per clone (an hour long, scoped to
/// what the installation grants), receives push events to redeploy, and signs people in.
/// </summary>
public class GitHubAppService(SettingsStore settings, SecretStore secrets)
{
    public const string Api = "https://api.github.com";
    private const string InfoKey = "GitHubApp", PemKey = "GitHubAppPem", ClientSecretKey = "GitHubAppClientSecret",
        WebhookSecretKey = "GitHubAppWebhookSecret", LoginKey = "GitHubLoginEnabled", PollKey = "GitHubPollMinutes",
        StateKey = "GitHubAppState";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Installation tokens last an hour; one is reused until ten minutes before it runs out. The
    // installation a repository belongs to is remembered for a while too, because every clone,
    // branch listing and poll would otherwise ask again.
    private static readonly ConcurrentDictionary<long, (string Token, DateTimeOffset Expires)> Tokens = new();
    private static readonly ConcurrentDictionary<string, (long? Installation, DateTimeOffset At)> RepoInstallations = new(StringComparer.OrdinalIgnoreCase);

    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;

    public GitHubAppInfo? Info() =>
        settings.GetValue(InfoKey) is { Length: > 0 } raw ? JsonSerializer.Deserialize<GitHubAppInfo>(raw, Json) : null;

    public bool Configured => Info() is not null && PrivateKey() is { Length: > 0 };
    public string? PrivateKey() => secrets.Resolve(settings.GetValue(PemKey));
    public string? ClientSecret() => secrets.Resolve(settings.GetValue(ClientSecretKey));
    public string? WebhookSecret() => secrets.Resolve(settings.GetValue(WebhookSecretKey));

    public bool LoginEnabled
    {
        get => Configured && (settings.GetValue(LoginKey) ?? "false") == "true";
        set => settings.SetValue(LoginKey, value ? "true" : "false");
    }

    /// <summary>How often apps deployed from GitHub are checked for new commits; 0 turns it off.</summary>
    public int PollMinutes
    {
        get => int.TryParse(settings.GetValue(PollKey), out var m) ? Math.Clamp(m, 0, 1440) : 5;
        set => settings.SetValue(PollKey, Math.Clamp(value, 0, 1440).ToString());
    }

    public void Save(GitHubAppInfo info, string pem, string? clientSecret, string? webhookSecret)
    {
        settings.SetValue(InfoKey, JsonSerializer.Serialize(info, Json));
        settings.SetValue(PemKey, secrets.Replace(settings.GetValue(PemKey), pem, "github app private key"));
        settings.SetValue(ClientSecretKey, secrets.Replace(settings.GetValue(ClientSecretKey), clientSecret, "github app client secret"));
        settings.SetValue(WebhookSecretKey, secrets.Replace(settings.GetValue(WebhookSecretKey), webhookSecret, "github app webhook secret"));
        Tokens.Clear(); RepoInstallations.Clear();
    }

    /// <summary>Forgets the app here. The app itself stays on GitHub until its owner deletes it there.</summary>
    public void Forget()
    {
        foreach (var key in new[] { PemKey, ClientSecretKey, WebhookSecretKey })
        {
            secrets.Delete(settings.GetValue(key));
            settings.SetValue(key, null);
        }
        settings.SetValue(InfoKey, null);
        settings.SetValue(LoginKey, null);
        Tokens.Clear(); RepoInstallations.Clear();
    }

    // --- creating the app from a manifest ---------------------------------------------------------

    /// <summary>
    /// The manifest GitHub turns into an app. Read access to code is all a deployment needs; the push
    /// event is what redeploys. Without a public address the webhook is created inactive — GitHub
    /// would only collect failed deliveries — and polling does the job instead.
    /// </summary>
    public static string Manifest(string name, string baseUrl, bool publicWebhook)
    {
        var root = baseUrl.TrimEnd('/');
        return new JsonObject
        {
            ["name"] = name,
            ["url"] = root,
            ["hook_attributes"] = new JsonObject { ["url"] = root + "/api/github/webhook", ["active"] = publicWebhook },
            ["redirect_url"] = root + "/api/github/app/created",
            ["callback_urls"] = new JsonArray(root + "/api/auth/github/callback"),
            ["setup_url"] = root + "/settings",
            ["public"] = false,
            ["default_permissions"] = new JsonObject { ["contents"] = "read", ["metadata"] = "read" },
            ["default_events"] = new JsonArray("push"),
        }.ToJsonString();
    }

    /// <summary>
    /// A one-time value tying GitHub's redirect back to the request that started it, with what that
    /// request asked for: the address the app was registered under and whether its webhook is live.
    /// </summary>
    public string NewState(string baseUrl, bool webhookActive)
    {
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        settings.SetValue(StateKey, JsonSerializer.Serialize(new PendingApp(state, Clock().ToUnixTimeSeconds(), baseUrl, webhookActive), Json));
        return state;
    }

    public record PendingApp(string State, long At, string BaseUrl, bool WebhookActive);

    public PendingApp? TakeState(string? state)
    {
        var raw = settings.GetValue(StateKey);
        settings.SetValue(StateKey, null);
        if (raw is null || string.IsNullOrEmpty(state)) return null;
        var p = JsonSerializer.Deserialize<PendingApp>(raw, Json);
        return p is not null
            && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(p.State), Encoding.ASCII.GetBytes(state))
            && Clock().ToUnixTimeSeconds() - p.At < 3600 ? p : null;
    }

    /// <summary>GitHub's half of the manifest flow: the code it redirected with, for the app's keys.</summary>
    public async Task<string?> CompleteAsync(string code, PendingApp pending)
    {
        try
        {
            using var req = Request(HttpMethod.Post, $"{Api}/app-manifests/{Uri.EscapeDataString(code)}/conversions", null);
            using var res = await Http.SendAsync(req);
            var text = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) return $"GitHub refused the code ({(int)res.StatusCode}): {Short(text)}";
            using var doc = JsonDocument.Parse(text);
            var r = doc.RootElement;
            var info = new GitHubAppInfo(r.GetProperty("id").GetInt64(), r.GetProperty("slug").GetString()!,
                r.GetProperty("client_id").GetString()!, r.GetProperty("owner").GetProperty("login").GetString()!,
                r.GetProperty("html_url").GetString()!, pending.BaseUrl.TrimEnd('/'), pending.WebhookActive, Clock().ToString("O"));
            Save(info, r.GetProperty("pem").GetString()!, Str(r, "client_secret"), Str(r, "webhook_secret"));
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    // --- tokens -------------------------------------------------------------------------------------

    /// <summary>The app's own credential: a JWT signed with its private key, good for ten minutes at most.</summary>
    public static string CreateJwt(string pem, string issuer, DateTimeOffset now)
    {
        static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = B64(Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT"}"""));
        // Backdated a minute for a clock that runs slightly ahead of GitHub's.
        var payload = B64(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["iat"] = now.AddSeconds(-60).ToUnixTimeSeconds(),
            ["exp"] = now.AddMinutes(9).ToUnixTimeSeconds(),
            ["iss"] = issuer,
        }));
        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        var sig = rsa.SignData(Encoding.ASCII.GetBytes($"{header}.{payload}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{header}.{payload}.{B64(sig)}";
    }

    private string? AppJwt() => Info() is { } i && PrivateKey() is { Length: > 0 } pem
        ? CreateJwt(pem, i.ClientId.Length > 0 ? i.ClientId : i.AppId.ToString(), Clock()) : null;

    /// <summary>
    /// A token git can clone with, for a github.com repository the app is installed on — or null, and
    /// whatever token the import was given is used as before.
    /// </summary>
    public string? CloneTokenFor(string url) =>
        RepoOf(url) is { } repo && InstallationTokenAsync(repo).GetAwaiter().GetResult() is { } token
            ? "x-access-token:" + token : null;

    public async Task<string?> InstallationTokenAsync(string fullName)
    {
        if (!Configured) return null;
        if (await InstallationOfAsync(fullName) is not { } installation) return null;
        return await TokenForInstallationAsync(installation);
    }

    private async Task<long?> InstallationOfAsync(string fullName)
    {
        if (RepoInstallations.TryGetValue(fullName, out var known) && Clock() - known.At < TimeSpan.FromMinutes(10))
            return known.Installation;
        if (AppJwt() is not { } jwt) return null;
        try
        {
            using var res = await Http.SendAsync(Request(HttpMethod.Get, $"{Api}/repos/{fullName}/installation", jwt));
            long? id = res.IsSuccessStatusCode
                ? JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt64()
                : null;
            RepoInstallations[fullName] = (id, Clock());
            return id;
        }
        catch { return null; }
    }

    private async Task<string?> TokenForInstallationAsync(long installation)
    {
        if (Tokens.TryGetValue(installation, out var t) && t.Expires - Clock() > TimeSpan.FromMinutes(10)) return t.Token;
        if (AppJwt() is not { } jwt) return null;
        try
        {
            using var res = await Http.SendAsync(Request(HttpMethod.Post, $"{Api}/app/installations/{installation}/access_tokens", jwt));
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var token = doc.RootElement.GetProperty("token").GetString()!;
            var expires = doc.RootElement.TryGetProperty("expires_at", out var e) && e.TryGetDateTimeOffset(out var at) ? at : Clock().AddMinutes(50);
            Tokens[installation] = (token, expires);
            return token;
        }
        catch { return null; }
    }

    public record Installation(long Id, string Account, string HtmlUrl);

    public async Task<(List<Installation>? installations, string? error)> InstallationsAsync()
    {
        if (AppJwt() is not { } jwt) return (null, "no GitHub App is set up");
        try
        {
            using var res = await Http.SendAsync(Request(HttpMethod.Get, $"{Api}/app/installations?per_page=100", jwt));
            var text = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) return (null, $"GitHub said {(int)res.StatusCode}: {Short(text)}");
            return (JsonDocument.Parse(text).RootElement.EnumerateArray().Select(i => new Installation(
                i.GetProperty("id").GetInt64(), i.GetProperty("account").GetProperty("login").GetString() ?? "",
                Str(i, "html_url") ?? "")).ToList(), null);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    /// <summary>Every repository the app's installations can read, for the import picker.</summary>
    public async Task<(List<GitHubRepo>? repos, string? error)> ReposAsync()
    {
        var (installations, error) = await InstallationsAsync();
        if (installations is null) return (null, error);
        var repos = new List<GitHubRepo>();
        foreach (var inst in installations)
        {
            if (await TokenForInstallationAsync(inst.Id) is not { } token) continue;
            for (var page = 1; page <= 10; page++)
            {
                using var req = Request(HttpMethod.Get, $"{Api}/installation/repositories?per_page=100&page={page}", null);
                req.Headers.Authorization = new AuthenticationHeaderValue("token", token);
                using var res = await Http.SendAsync(req);
                if (!res.IsSuccessStatusCode) break;
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
                var list = doc.RootElement.GetProperty("repositories").EnumerateArray().ToList();
                foreach (var r in list)
                {
                    var full = r.GetProperty("full_name").GetString()!;
                    RepoInstallations[full] = (inst.Id, Clock());
                    repos.Add(new GitHubRepo(full, Str(r, "clone_url") ?? $"https://github.com/{full}.git",
                        r.TryGetProperty("private", out var p) && p.GetBoolean(), Str(r, "default_branch") ?? "main"));
                }
                if (list.Count < 100) break;
            }
        }
        return (repos.OrderBy(r => r.FullName, StringComparer.OrdinalIgnoreCase).ToList(), null);
    }

    // --- webhook ------------------------------------------------------------------------------------

    /// <summary>GitHub signs every delivery with the webhook secret; anything else is not GitHub.</summary>
    public static bool VerifySignature(byte[] body, string? header, string? secret)
    {
        if (string.IsNullOrEmpty(secret) || header is null || !header.StartsWith("sha256=", StringComparison.Ordinal)) return false;
        var expected = Encoding.ASCII.GetBytes("sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(header));
    }

    /// <summary>A push to a branch, or null for anything else (a tag, a deleted branch).</summary>
    public static GitHubPush? ParsePush(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var gitRef = Str(r, "ref") ?? "";
            if (!gitRef.StartsWith("refs/heads/", StringComparison.Ordinal)) return null;
            if (r.TryGetProperty("deleted", out var d) && d.ValueKind == JsonValueKind.True) return null;
            var repo = r.GetProperty("repository");
            return new GitHubPush(repo.GetProperty("full_name").GetString()!, gitRef["refs/heads/".Length..],
                Str(repo, "default_branch") ?? "main", Str(r, "after") ?? "");
        }
        catch { return null; }
    }

    /// <summary>owner/name of a github.com repository url, or null for anything else.</summary>
    public static string? RepoOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var u = url.Trim();
        if (u.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase)) u = "https://github.com/" + u[15..];
        if (!Uri.TryCreate(u, UriKind.Absolute, out var uri) || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0) return null;
        var name = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
        return $"{parts[0]}/{name}";
    }

    /// <summary>Whether an app imported from <paramref name="url"/> at <paramref name="branch"/> runs what was pushed.</summary>
    public static bool Tracks(string url, string? branch, GitHubPush push) =>
        string.Equals(RepoOf(url), push.FullName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(string.IsNullOrWhiteSpace(branch) ? push.DefaultBranch : branch!.Trim(), push.Branch, StringComparison.Ordinal);

    // --- signing in -----------------------------------------------------------------------------------

    public string AuthorizeUrl(string state) =>
        "https://github.com/login/oauth/authorize?" + string.Join("&", new Dictionary<string, string>
        {
            ["client_id"] = Info()?.ClientId ?? "",
            ["redirect_uri"] = Info()?.CallbackUrl ?? "",
            ["state"] = state,
            ["allow_signup"] = "false",
        }.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));

    /// <summary>The signed-in GitHub account behind an OAuth code: its numeric id (stable) and login (renamable).</summary>
    public async Task<(long id, string? login, string? error)> UserForCodeAsync(string code)
    {
        try
        {
            using var tokenReq = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = Info()?.ClientId ?? "",
                    ["client_secret"] = ClientSecret() ?? "",
                    ["code"] = code,
                    ["redirect_uri"] = Info()?.CallbackUrl ?? "",
                }),
            };
            tokenReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var tokenRes = await Http.SendAsync(tokenReq);
            using var tokenDoc = JsonDocument.Parse(await tokenRes.Content.ReadAsStringAsync());
            if (Str(tokenDoc.RootElement, "access_token") is not { Length: > 0 } access)
                return (0, null, Str(tokenDoc.RootElement, "error_description") ?? "GitHub returned no access token");

            using var userReq = Request(HttpMethod.Get, $"{Api}/user", null);
            userReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            using var userRes = await Http.SendAsync(userReq);
            var text = await userRes.Content.ReadAsStringAsync();
            if (!userRes.IsSuccessStatusCode) return (0, null, $"GitHub said {(int)userRes.StatusCode}: {Short(text)}");
            using var user = JsonDocument.Parse(text);
            return (user.RootElement.GetProperty("id").GetInt64(), Str(user.RootElement, "login"), null);
        }
        catch (Exception ex) { return (0, null, ex.Message); }
    }

    /// <summary>The AspireUI user a GitHub account was linked to, by GitHub's numeric id.</summary>
    public string? LinkedUser(long githubId) => settings.GetValue($"ghuser:{githubId}");
    public void Link(long githubId, string login, string userId)
    {
        foreach (var (key, value) in settings.All())
            if (key.StartsWith("ghuser:", StringComparison.Ordinal) && value == userId) settings.SetValue(key, null);
        settings.SetValue($"ghuser:{githubId}", userId);
        settings.SetValue($"ghlogin:{userId}", login);
    }
    public string? LinkedLogin(string userId) => settings.GetValue($"ghlogin:{userId}");
    public void Unlink(string userId)
    {
        foreach (var (key, value) in settings.All())
            if (key.StartsWith("ghuser:", StringComparison.Ordinal) && value == userId) settings.SetValue(key, null);
        settings.SetValue($"ghlogin:{userId}", null);
    }

    // --- helpers ------------------------------------------------------------------------------------

    private static HttpRequestMessage Request(HttpMethod method, string url, string? jwt)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.UserAgent.ParseAdd("AspireUI");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (jwt is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        return req;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Short(string s) => s.Length <= 200 ? s : s[..200] + "…";
}
