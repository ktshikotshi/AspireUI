using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AspireUI.Server.Services;

// The GitHub App's pieces that do not need GitHub: the JWT it signs, the webhook signature it checks,
// the push it reads, and which deployed app a push belongs to.
public class GitHubAppTests
{
    [Fact]
    public void The_app_jwt_verifies_with_the_apps_public_key()
    {
        using var rsa = RSA.Create(2048);
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

        var jwt = GitHubAppService.CreateJwt(rsa.ExportRSAPrivateKeyPem(), "Iv1.abc", now);

        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);
        Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), FromB64Url(parts[2]),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        using var payload = JsonDocument.Parse(FromB64Url(parts[1]));
        Assert.Equal("Iv1.abc", payload.RootElement.GetProperty("iss").GetString());
        var iat = payload.RootElement.GetProperty("iat").GetInt64();
        var exp = payload.RootElement.GetProperty("exp").GetInt64();
        Assert.True(iat < now.ToUnixTimeSeconds());
        Assert.True(exp - iat <= 600);                               // GitHub refuses anything longer
    }

    [Fact]
    public void Only_a_delivery_signed_with_the_secret_passes()
    {
        var body = Encoding.UTF8.GetBytes("""{"zen":"Keep it logically awesome."}""");
        var good = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("s3cret"), body)).ToLowerInvariant();

        Assert.True(GitHubAppService.VerifySignature(body, good, "s3cret"));
        Assert.False(GitHubAppService.VerifySignature(body, good, "other"));
        Assert.False(GitHubAppService.VerifySignature(Encoding.UTF8.GetBytes("{}"), good, "s3cret"));
        Assert.False(GitHubAppService.VerifySignature(body, null, "s3cret"));
        Assert.False(GitHubAppService.VerifySignature(body, good, null));   // no secret, no trust
    }

    [Fact]
    public void A_push_to_a_branch_is_read_and_a_tag_or_deletion_is_not()
    {
        var push = GitHubAppService.ParsePush("""
            {"ref":"refs/heads/main","after":"abc123","repository":{"full_name":"ktshikotshi/personal-budget","default_branch":"main"}}
            """);
        Assert.Equal(new GitHubPush("ktshikotshi/personal-budget", "main", "main", "abc123"), push);

        Assert.Null(GitHubAppService.ParsePush("""{"ref":"refs/tags/v1","repository":{"full_name":"a/b"}}"""));
        Assert.Null(GitHubAppService.ParsePush("""{"ref":"refs/heads/x","deleted":true,"repository":{"full_name":"a/b"}}"""));
        Assert.Null(GitHubAppService.ParsePush("not json"));
    }

    [Theory]
    [InlineData("https://github.com/ktshikotshi/personal-budget", "ktshikotshi/personal-budget")]
    [InlineData("https://github.com/ktshikotshi/personal-budget.git", "ktshikotshi/personal-budget")]
    [InlineData("https://github.com/ktshikotshi/personal-budget/tree/main", "ktshikotshi/personal-budget")]
    [InlineData("git@github.com:ktshikotshi/personal-budget.git", "ktshikotshi/personal-budget")]
    [InlineData("https://gitlab.com/a/b", null)]
    [InlineData("https://github.com/only-owner", null)]
    [InlineData("", null)]
    public void RepoOf_reads_github_urls_only(string url, string? expected) =>
        Assert.Equal(expected, GitHubAppService.RepoOf(url));

    [Fact]
    public void A_push_redeploys_the_apps_on_that_branch()
    {
        var push = new GitHubPush("Owner/Repo", "main", "main", "abc");

        Assert.True(GitHubAppService.Tracks("https://github.com/owner/repo", null, push));       // default branch
        Assert.True(GitHubAppService.Tracks("https://github.com/owner/repo.git", "main", push));
        Assert.False(GitHubAppService.Tracks("https://github.com/owner/repo", "develop", push));
        Assert.False(GitHubAppService.Tracks("https://github.com/owner/other", null, push));
    }

    [Fact]
    public void The_manifest_asks_for_read_access_and_push_events()
    {
        using var doc = JsonDocument.Parse(GitHubAppService.Manifest("AspireUI tower", "http://tower.local:8080/", publicWebhook: false));
        var m = doc.RootElement;
        Assert.Equal("http://tower.local:8080/api/github/webhook", m.GetProperty("hook_attributes").GetProperty("url").GetString());
        Assert.False(m.GetProperty("hook_attributes").GetProperty("active").GetBoolean());
        Assert.Equal("http://tower.local:8080/api/github/app/created", m.GetProperty("redirect_url").GetString());
        Assert.Equal("read", m.GetProperty("default_permissions").GetProperty("contents").GetString());
        Assert.Equal(["push"], m.GetProperty("default_events").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.False(m.GetProperty("public").GetBoolean());
    }

    [Fact]
    public void The_creation_state_is_one_time_and_carries_the_address()
    {
        var db = Path.Combine(Path.GetTempPath(), "aspireui-gh-" + Guid.NewGuid().ToString("n") + ".db");
        var gh = new GitHubAppService(new SettingsStore(db), new SecretStore(db));

        var state = gh.NewState("https://aspire.example.com", webhookActive: true);

        Assert.Null(gh.TakeState("wrong"));
        state = gh.NewState("https://aspire.example.com", webhookActive: true);
        var pending = gh.TakeState(state);
        Assert.Equal("https://aspire.example.com", pending?.BaseUrl);
        Assert.True(pending?.WebhookActive);
        Assert.Null(gh.TakeState(state));                            // used up
    }

    [Fact]
    public void A_linked_account_maps_to_one_user_and_relinking_moves_it()
    {
        var db = Path.Combine(Path.GetTempPath(), "aspireui-gh-" + Guid.NewGuid().ToString("n") + ".db");
        var gh = new GitHubAppService(new SettingsStore(db), new SecretStore(db));

        gh.Link(17251746, "ktshikotshi", "u1");
        Assert.Equal("u1", gh.LinkedUser(17251746));
        Assert.Equal("ktshikotshi", gh.LinkedLogin("u1"));

        gh.Link(42, "other", "u1");                                   // a user has one GitHub account
        Assert.Null(gh.LinkedUser(17251746));
        Assert.Equal("u1", gh.LinkedUser(42));

        gh.Unlink("u1");
        Assert.Null(gh.LinkedUser(42));
        Assert.Null(gh.LinkedLogin("u1"));
    }

    private static byte[] FromB64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}

[Collection("ServerIntegration")]
public class GitHubEndpointTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _f;
    public GitHubEndpointTests(TestWebAppFactory f) => _f = f;

    [Fact]
    public async Task An_unsigned_webhook_is_refused()
    {
        var r = await _f.CreateClient().PostAsync("/api/github/webhook",
            new StringContent("""{"ref":"refs/heads/main"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Without_an_app_nothing_offers_github_sign_in()
    {
        var c = _f.CreateClient();
        var login = await c.GetFromJsonAsync<JsonElement>("/api/auth/github");
        Assert.False(login.GetProperty("enabled").GetBoolean());
        var app = await c.GetFromJsonAsync<JsonElement>("/api/github/app");
        Assert.False(app.GetProperty("configured").GetBoolean());
        var start = await c.GetAsync("/api/auth/github/start");
        Assert.Equal(HttpStatusCode.BadRequest, start.StatusCode);
    }
}
