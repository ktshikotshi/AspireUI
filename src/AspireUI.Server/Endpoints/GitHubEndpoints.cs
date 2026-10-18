using System.Net;
using System.Security.Claims;
using AspireUI.Server.Models;
using AspireUI.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;

namespace AspireUI.Server.Endpoints;

/// <summary>
/// AspireUI's GitHub App: creating it from a manifest, its status and settings, the repositories it
/// can reach, and signing in with a GitHub account linked to an AspireUI user. The push webhook sits
/// with the stack endpoints, because a push is a redeploy.
/// </summary>
public static class GitHubEndpoints
{
    private const string FlowCookie = "aspireui.github";

    public static void MapGitHubEndpoints(this WebApplication app)
    {
        var users = app.Services.GetRequiredService<UserStore>();
        var settings = app.Services.GetRequiredService<SettingsStore>();
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AspireUI");
        var dbPath = Environment.GetEnvironmentVariable("DB_PATH") ?? Path.Combine(dataDir, "aspireui.db");
        var github = new GitHubAppService(settings, new SecretStore(dbPath, dataDir));
        var flow = app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("aspireui.github");
        var api = app.MapGroup("/api");

        api.MapGet("/github/app", async () =>
        {
            if (github.Info() is not { } i || !github.Configured)
                return Results.Ok(new { configured = false, loginEnabled = false, pollMinutes = github.PollMinutes });
            var (installations, error) = await github.InstallationsAsync();
            return Results.Ok(new
            {
                configured = true, i.AppId, i.Slug, i.Owner, i.HtmlUrl, i.InstallUrl, i.BaseUrl, i.WebhookUrl, i.WebhookActive,
                callbackUrl = i.CallbackUrl, loginEnabled = github.LoginEnabled, pollMinutes = github.PollMinutes,
                installations = installations?.Select(x => new { x.Id, x.Account, x.HtmlUrl }), error,
            });
        }).RequireAuthorization().RequirePerm(Perm.Settings);

        api.MapPut("/github/settings", (GitHubSettingsRequest b) =>
        {
            if (b.LoginEnabled is { } login) github.LoginEnabled = login;
            if (b.PollMinutes is { } poll) github.PollMinutes = poll;
            return Results.NoContent();
        }).RequireAuthorization().RequirePerm(Perm.Settings);

        api.MapDelete("/github/app", () => { github.Forget(); return Results.NoContent(); })
            .RequireAuthorization().RequirePerm(Perm.Settings);

        // The manifest flow starts with a form POSTed by the browser to GitHub; GitHub creates the app
        // under whoever is signed in there and comes back to /created with a code for its keys.
        api.MapGet("/github/app/new", (HttpContext ctx, string? baseUrl, string? name, string? org, bool? webhook) =>
        {
            var root = (string.IsNullOrWhiteSpace(baseUrl) ? $"{ctx.Request.Scheme}://{ctx.Request.Host}" : baseUrl!).Trim().TrimEnd('/');
            if (!Uri.TryCreate(root, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
                return Results.BadRequest(new { message = "the address must be an http(s) url" });
            var appName = string.IsNullOrWhiteSpace(name) ? $"AspireUI {u.Host}" : name!.Trim();
            var state = github.NewState(root, webhook ?? false);
            var target = string.IsNullOrWhiteSpace(org)
                ? "https://github.com/settings/apps/new"
                : $"https://github.com/organizations/{Uri.EscapeDataString(org!.Trim())}/settings/apps/new";
            var manifest = GitHubAppService.Manifest(appName, root, webhook ?? false);
            var html = $"""
                <!doctype html><html><head><meta charset="utf-8"><title>Creating the GitHub App…</title></head>
                <body style="font-family:system-ui;padding:2rem">
                <p>Sending you to GitHub to create the app…</p>
                <form id="f" method="post" action="{WebUtility.HtmlEncode(target)}?state={state}">
                <input type="hidden" name="manifest" value="{WebUtility.HtmlEncode(manifest)}">
                <button type="submit">Continue to GitHub</button></form>
                <script>document.getElementById('f').submit()</script></body></html>
                """;
            return Results.Content(html, "text/html");
        }).RequireAuthorization().RequirePerm(Perm.Settings);

        api.MapGet("/github/app/created", async (string? code, string? state) =>
        {
            if (string.IsNullOrWhiteSpace(code)) return Results.Redirect("/settings?github=" + Uri.EscapeDataString("GitHub sent no code"));
            if (github.TakeState(state) is not { } pending)
                return Results.Redirect("/settings?github=" + Uri.EscapeDataString("that app creation did not start here"));
            if (await github.CompleteAsync(code!, pending) is { } error)
                return Results.Redirect("/settings?github=" + Uri.EscapeDataString(error));
            // Created, not yet installed: pick the repositories it may deploy next.
            return Results.Redirect(github.Info()!.InstallUrl);
        }).RequireAuthorization().RequirePerm(Perm.Settings);

        api.MapGet("/github/repos", async () =>
        {
            if (!github.Configured) return Results.Ok(new { configured = false, repos = Array.Empty<GitHubRepo>() });
            var (repos, error) = await github.ReposAsync();
            return repos is null
                ? Results.UnprocessableEntity(new { message = error })
                : Results.Ok(new { configured = true, repos });
        }).RequireAuthorization().RequirePerm(Perm.OpenEditor);

        // --- signing in with GitHub ---------------------------------------------------------------------

        api.MapGet("/auth/github", () => Results.Ok(new { enabled = github.LoginEnabled }));

        api.MapGet("/auth/github/start", (HttpContext ctx, string? next, bool? link) =>
        {
            if (!github.Configured) return Results.BadRequest(new { message = "no GitHub App is set up" });
            var linking = link == true;
            if (linking && ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
            if (!linking && !github.LoginEnabled) return Results.BadRequest(new { message = "signing in with GitHub is off" });
            var state = Guid.NewGuid().ToString("n");
            ctx.Response.Cookies.Append(FlowCookie, flow.Protect($"{state}|{(linking ? "link" : "login")}|{next ?? "/"}"), new CookieOptions
            {
                HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = ctx.Request.IsHttps,
                MaxAge = TimeSpan.FromMinutes(10), Path = "/",
            });
            return Results.Redirect(github.AuthorizeUrl(state));
        });

        api.MapGet("/auth/github/callback", async (HttpContext ctx, string? code, string? state, string? error) =>
        {
            if (!string.IsNullOrWhiteSpace(error)) return Fail(error!);
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state)) return Fail("GitHub sent no code");
            if (!ctx.Request.Cookies.TryGetValue(FlowCookie, out var sealedFlow)) return Fail("that sign-in took too long");
            ctx.Response.Cookies.Delete(FlowCookie);
            string[] parts;
            try { parts = flow.Unprotect(sealedFlow).Split('|', 3); }
            catch { return Fail("that sign-in did not start here"); }
            if (parts.Length != 3 || parts[0] != state) return Fail("that sign-in did not start here");

            var (githubId, login, userError) = await github.UserForCodeAsync(code!);
            if (githubId == 0 || login is null) return Fail(userError ?? "GitHub did not say who you are");

            if (parts[1] == "link")
            {
                var uid = ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(uid) || users.Get(uid) is null) return Results.Redirect("/login");
                github.Link(githubId, login, uid);
                return Results.Redirect("/profile?github=linked");
            }

            if (!github.LoginEnabled) return Fail("signing in with GitHub is off");
            // Only an account somebody linked on purpose: a GitHub login is not proof of anything here.
            if (github.LinkedUser(githubId) is not { } userId || users.Get(userId) is not { } user)
                return Fail($"the GitHub account {login} is not linked to anyone here — sign in and link it on your profile");
            if (user.Disabled) return Fail("that account is disabled");

            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id),
                new Claim(ClaimTypes.Name, user.Username),
                .. user.IsAdmin ? new[] { new Claim(ClaimTypes.Role, "Admin") } : [],
            ], CookieAuthenticationDefaults.AuthenticationScheme);
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
            return Results.Redirect(parts[2].StartsWith('/') && !parts[2].StartsWith("//") ? parts[2] : "/");
        });

        api.MapGet("/me/github", (HttpContext ctx) =>
            Results.Ok(new { login = github.LinkedLogin(ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? ""), available = github.Configured }))
            .RequireAuthorization();

        api.MapDelete("/me/github", (HttpContext ctx) =>
        {
            github.Unlink(ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "");
            return Results.NoContent();
        }).RequireAuthorization();
    }

    private static IResult Fail(string message) => Results.Redirect("/login?ssoError=" + Uri.EscapeDataString(message));

    public record GitHubSettingsRequest(bool? LoginEnabled = null, int? PollMinutes = null);
}
