using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AspireUI.Server.Services;

/// <summary>
/// An imported AppHost runs as written, so nothing in it says how to publish. `aspire publish` on an
/// AppHost without a compute environment exits 0 and writes nothing, and Hosting reported a failed
/// publish for every imported app. Its projects are worse off still: the compose file names them
/// `image: ${WEB_IMAGE}` and `${WEB_PORT}`, which nothing fills. These helpers patch the publish copy
/// only — the stored source stays what the user imported.
/// </summary>
public static class AppHostPublish
{
    private static readonly string[] SkipDirs = ["bin", "obj", ".git", "node_modules"];

    /// <summary>The port a project listens on inside its container: the ASP.NET image default.</summary>
    public const int ProjectPort = 8080;

    /// <summary>
    /// Adds the publish environment (`builder.AddDockerComposeEnvironment(...)` and its package) to the
    /// AppHost at <paramref name="csprojPath"/> unless it already has one. Returns what it did, for the log.
    /// </summary>
    public static string EnsureEnvironment(string csprojPath, CodeGenService.PublishEnv env, string srcRoot)
    {
        var notes = new List<string>();
        var dir = Path.GetDirectoryName(csprojPath)!;
        var entry = SourceFiles(dir).FirstOrDefault(f => File.ReadAllText(f).Contains("DistributedApplication.CreateBuilder"));
        var method = Regex.Match(env.Statement, @"\.(\w+)\(").Groups[1].Value;
        if (entry is not null && !SourceFiles(dir).Any(f => File.ReadAllText(f).Contains("." + method + "(")))
        {
            var text = File.ReadAllText(entry);
            var m = Regex.Match(text, @"(\w+)\s*=\s*DistributedApplication\.CreateBuilder\([^;]*\);");
            if (m.Success)
            {
                var statement = env.Statement.Replace("builder.", m.Groups[1].Value + ".");
                File.WriteAllText(entry, text.Insert(m.Index + m.Length, "\n" + statement + "\n"));
                notes.Add($"added {statement} to {Path.GetFileName(entry)}");
            }
            else notes.Add($"could not find the builder in {Path.GetFileName(entry)}; publish may produce nothing");
        }

        var csproj = File.ReadAllText(csprojPath);
        if (!Regex.IsMatch(csproj, $@"Include\s*=\s*""{Regex.Escape(env.PackageId)}""", RegexOptions.IgnoreCase))
        {
            var version = AspireVersionOf(csprojPath, srcRoot) is { } v && Major(v) >= 13 ? v : env.PackageVersion;
            var props = CentralPackages(dir, srcRoot);
            string reference;
            if (props is not null)
            {
                var p = File.ReadAllText(props);
                if (!Regex.IsMatch(p, $@"Include\s*=\s*""{Regex.Escape(env.PackageId)}""", RegexOptions.IgnoreCase))
                    File.WriteAllText(props, InsertItem(p, $"<PackageVersion Include=\"{env.PackageId}\" Version=\"{version}\" />"));
                reference = $"<PackageReference Include=\"{env.PackageId}\" />";
            }
            else reference = $"<PackageReference Include=\"{env.PackageId}\" Version=\"{version}\" />";
            File.WriteAllText(csprojPath, InsertItem(csproj, reference));
            notes.Add($"added package {env.PackageId} {version}");
        }
        return string.Join("\n", notes);
    }

    /// <summary>
    /// The projects the AppHost runs, by resource name: `AddProject&lt;Projects.Web&gt;("web")` resolved
    /// through the AppHost's project references, and `AddProject("web", "../Web/Web.csproj")`.
    /// </summary>
    public static Dictionary<string, string> Projects(string csprojPath)
    {
        var dir = Path.GetDirectoryName(csprojPath)!;
        var refs = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var r in XDocument.Load(csprojPath).Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
            {
                if (r.Attribute("Include")?.Value is not { Length: > 0 } inc) continue;
                var path = Path.GetFullPath(Path.Combine(dir, inc.Replace('\\', '/')));
                refs[MetadataName(Path.GetFileNameWithoutExtension(path))] = path;
            }
        }
        catch { }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in SourceFiles(dir))
        {
            var text = File.ReadAllText(f);
            foreach (Match m in Regex.Matches(text, @"AddProject<\s*(?:global::)?Projects\.(\w+)\s*>\(\s*""([^""]+)"""))
                if (refs.TryGetValue(m.Groups[1].Value, out var p)) result[m.Groups[2].Value] = p;
            foreach (Match m in Regex.Matches(text, @"AddProject\(\s*""([^""]+)""\s*,\s*@?""([^""]+\.csproj)"""))
                result[m.Groups[1].Value] = Path.GetFullPath(Path.Combine(dir, m.Groups[2].Value.Replace('\\', '/')));
        }
        return result.Where(kv => File.Exists(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    /// <summary>
    /// Gives every project service in the published compose file a build: a Dockerfile written next to
    /// the source that publishes the project and runs it, with the repository root as the context so
    /// project references and Directory.Build.props come along. Its `${NAME_PORT}` becomes
    /// <see cref="ProjectPort"/> so the port can be published like any other. Returns the new file and
    /// whether anything has to be built.
    /// </summary>
    public static (string Yaml, bool Built) InjectProjectBuilds(string yaml, IReadOnlyDictionary<string, string> projects, string srcDir)
    {
        if (projects.Count == 0) return (yaml, false);
        var root = Path.GetFullPath(srcDir);
        var byKey = projects.ToDictionary(kv => EnvKey(kv.Key), kv => (Name: kv.Key, Path: kv.Value));
        var lines = yaml.Replace("\r\n", "\n").Split('\n').ToList();
        var built = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var m = Regex.Match(lines[i], @"^    image:\s*""?\$\{([A-Za-z0-9_]+)_IMAGE\}""?\s*$");
            if (!m.Success || !byKey.TryGetValue(m.Groups[1].Value, out var p)) continue;
            if (!p.Path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            var dockerfile = WriteDockerfile(root, p.Name, p.Path);
            // No image name: compose names the build after the project, so two apps that both have a
            // "web" never tag over each other's image.
            lines.RemoveAt(i);
            lines.InsertRange(i, new[]
            {
                "    build:",
                $"      context: {JsonSerializer.Serialize(root.Replace('\\', '/'))}",
                $"      dockerfile: {JsonSerializer.Serialize(dockerfile)}",
            });
            ExposeInsteadOfPublish(lines, i, m.Groups[1].Value);
            built = true;
        }
        var text = string.Join("\n", lines);
        foreach (var key in byKey.Keys)
            text = text.Replace("${" + key + "_PORT}", ProjectPort.ToString());
        return (text, built);
    }

    /// <summary>A JavaScript app the AppHost runs: its folder and the Add* method that declared it.</summary>
    public sealed record JsApp(string Dir, string Kind);

    /// <summary>
    /// The JavaScript apps the AppHost runs, by resource name: `AddViteApp("web", "../web")` and its
    /// siblings `AddJavaScriptApp`, `AddNodeApp` and `AddNextJsApp`.
    /// </summary>
    public static Dictionary<string, JsApp> JavaScriptApps(string csprojPath)
    {
        var dir = Path.GetDirectoryName(csprojPath)!;
        var result = new Dictionary<string, JsApp>(StringComparer.Ordinal);
        foreach (var f in SourceFiles(dir))
            foreach (Match m in Regex.Matches(File.ReadAllText(f), @"Add(ViteApp|JavaScriptApp|NodeApp|NextJsApp)\(\s*""([^""]+)""\s*,\s*@?""([^""]+)"""))
                result[m.Groups[2].Value] = new JsApp(Path.GetFullPath(Path.Combine(dir, m.Groups[3].Value.Replace('\\', '/'))), m.Groups[1].Value);
        return result.Where(kv => Directory.Exists(kv.Value.Dir)).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    /// <summary>
    /// Gives every JavaScript app in the published compose file a build, as
    /// <see cref="InjectProjectBuilds"/> does for projects. An app folder with its own Dockerfile builds
    /// from that; otherwise a Vite app becomes a static build served by nginx and any other app runs
    /// `npm start` on Node. The published port is pointed at the port the image listens on.
    /// </summary>
    public static (string Yaml, bool Built) InjectJavaScriptBuilds(string yaml, IReadOnlyDictionary<string, JsApp> apps, string srcDir)
    {
        if (apps.Count == 0) return (yaml, false);
        var root = Path.GetFullPath(srcDir);
        var byKey = apps.ToDictionary(kv => EnvKey(kv.Key), kv => (Name: kv.Key, App: kv.Value));
        var lines = yaml.Replace("\r\n", "\n").Split('\n').ToList();
        var built = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var m = Regex.Match(lines[i], @"^    image:\s*""?\$\{([A-Za-z0-9_]+)_IMAGE\}""?\s*$");
            if (!m.Success || !byKey.TryGetValue(m.Groups[1].Value, out var a)) continue;
            var appDir = a.App.Dir;
            if (appDir != root && !appDir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            var (dockerfile, port) = JsDockerfile(appDir, a.Name, a.App.Kind);
            lines.RemoveAt(i);
            lines.InsertRange(i, new[]
            {
                "    build:",
                $"      context: {JsonSerializer.Serialize(appDir.Replace('\\', '/'))}",
                $"      dockerfile: {JsonSerializer.Serialize(dockerfile)}",
            });
            if (port is { } p) TargetPort(lines, i, m.Groups[1].Value, p);
            ExposeInsteadOfPublish(lines, i, m.Groups[1].Value);
            built = true;
        }
        return (string.Join("\n", lines), built);
    }

    // The app's own Dockerfile and the port it EXPOSEs, or a generated one. A null port leaves the
    // compose ports as Aspire wrote them: the generated Node image listens on the PORT Aspire sets.
    private static (string Dockerfile, int? Port) JsDockerfile(string appDir, string name, string kind)
    {
        var own = Path.Combine(appDir, "Dockerfile");
        if (File.Exists(own))
        {
            var expose = Regex.Matches(File.ReadAllText(own), @"^\s*EXPOSE\s+(\d+)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            return ("Dockerfile", expose.Count > 0 ? int.Parse(expose[^1].Groups[1].Value) : null);
        }
        var dir = Path.Combine(appDir, ".aspireui");
        Directory.CreateDirectory(dir);
        var file = $"{EnvKey(name).ToLowerInvariant()}.Dockerfile";
        var install = File.Exists(Path.Combine(appDir, "package-lock.json")) ? "npm ci" : "npm install";
        var vite = kind == "ViteApp";
        File.WriteAllText(Path.Combine(dir, file), vite
            ? $"""
                FROM node:22-alpine AS build
                WORKDIR /app
                COPY . .
                RUN {install} && npm run build
                FROM nginx:1.27-alpine
                COPY --from=build /app/dist /usr/share/nginx/html

                """
            : $"""
                FROM node:22-alpine
                WORKDIR /app
                COPY . .
                RUN {install} && npm run build --if-present
                ENV NODE_ENV=production
                CMD ["npm", "start"]

                """);
        File.WriteAllText(Path.Combine(dir, file + ".dockerignore"), "**/node_modules/\n**/.git/\n.aspireui/\n");
        return (".aspireui/" + file, vite ? 80 : null);
    }

    // Points each published port of the service at the port its image listens on:
    // `"5173:5173"` → `"5173:80"`, a lone `"5173"` or `"${WEB_PORT}"` → `"80"`.
    private static void TargetPort(List<string> lines, int inService, string key, int port)
    {
        var start = inService;
        while (start > 0 && !Regex.IsMatch(lines[start], @"^  \S")) start--;
        var end = inService;
        while (end + 1 < lines.Count && !Regex.IsMatch(lines[end + 1], @"^ {0,2}\S")) end++;
        var inPorts = false;
        for (var j = start + 1; j <= end; j++)
        {
            if (Regex.IsMatch(lines[j], @"^    \S")) { inPorts = Regex.IsMatch(lines[j], @"^    ports:\s*$"); continue; }
            if (!inPorts) continue;
            var pm = Regex.Match(lines[j], @"^      -\s*""?(?:(?<host>\$\{[A-Za-z0-9_]+\}|\d+):)?(?:\$\{[A-Za-z0-9_]+\}|\d+)""?\s*$");
            if (!pm.Success) continue;
            lines[j] = pm.Groups["host"].Success ? $"      - \"{pm.Groups["host"].Value}:{port}\"" : $"      - \"{port}\"";
        }
        for (var j = start + 1; j <= end; j++)
            lines[j] = lines[j].Replace("${" + key + "_PORT}", port.ToString());
    }

    // `ports: - "${WEB_PORT}"` publishes on a random host port that changes with every redeploy.
    // Moved under `expose:` it gets a host port from the pool like every other app, kept across deploys.
    private static void ExposeInsteadOfPublish(List<string> lines, int inService, string key)
    {
        var start = inService;
        while (start > 0 && !Regex.IsMatch(lines[start], @"^  \S")) start--;
        var end = inService;
        while (end + 1 < lines.Count && !Regex.IsMatch(lines[end + 1], @"^ {0,2}\S")) end++;
        var portsAt = -1; var exposeAt = -1;
        for (var j = start + 1; j <= end; j++)
        {
            if (Regex.IsMatch(lines[j], @"^    ports:\s*$")) portsAt = j;
            if (Regex.IsMatch(lines[j], @"^    expose:\s*$")) exposeAt = j;
        }
        if (portsAt < 0) return;
        var items = new List<string>();
        var k = portsAt + 1;
        while (k <= end && Regex.IsMatch(lines[k], @"^      -")) items.Add(lines[k++]);
        // Only a port the container picks on its own; a fixed host port was asked for and stays.
        if (items.Count == 0 || !items.All(it => Regex.IsMatch(it, $@"^      -\s*""?(\$\{{{key}_PORT\}}|\d+)""?\s*$"))) return;
        lines.RemoveRange(portsAt, items.Count + 1);
        if (exposeAt < 0) { lines.Insert(portsAt, "    expose:"); lines.InsertRange(portsAt + 1, items); return; }
        if (exposeAt > portsAt) exposeAt -= items.Count + 1;
        var after = exposeAt + 1;
        while (after < lines.Count && Regex.IsMatch(lines[after], @"^      -")) after++;
        lines.InsertRange(after, items);
    }

    // Dockerfile and its own .dockerignore (BuildKit reads `<name>.dockerignore` next to it), so the
    // bin/ and obj/ the AppHost publish just produced on this machine stay out of the image.
    private static string WriteDockerfile(string root, string name, string csproj)
    {
        var rel = Path.GetRelativePath(root, csproj).Replace('\\', '/');
        var tfm = TargetFramework(csproj) ?? "net10.0";
        var tag = Regex.Match(tfm, @"^net(\d+\.\d+)").Groups[1].Value is { Length: > 0 } t ? t : "10.0";
        var assembly = AssemblyName(csproj);
        var dir = Path.Combine(root, ".aspireui");
        Directory.CreateDirectory(dir);
        var file = $"{EnvKey(name).ToLowerInvariant()}.Dockerfile";
        File.WriteAllText(Path.Combine(dir, file), $"""
            FROM mcr.microsoft.com/dotnet/sdk:{tag} AS build
            WORKDIR /src
            COPY . .
            RUN dotnet publish "{rel}" -c Release -f {tfm} -o /app /p:UseAppHost=false
            FROM mcr.microsoft.com/dotnet/aspnet:{tag}
            WORKDIR /app
            COPY --from=build /app .
            ENTRYPOINT ["dotnet", "{assembly}.dll"]

            """);
        File.WriteAllText(Path.Combine(dir, file + ".dockerignore"), "**/bin/\n**/obj/\n**/.git/\n**/node_modules/\n.aspireui/\n");
        return ".aspireui/" + file;
    }

    /// <summary>The key compose uses for a resource: `my-api` → `MY_API`.</summary>
    public static string EnvKey(string resource) => Regex.Replace(resource.ToUpperInvariant(), "[^A-Z0-9]", "_");

    // The class Aspire generates under `Projects.` for a referenced project.
    private static string MetadataName(string fileName) => Regex.Replace(fileName, @"[^A-Za-z0-9_]", "_");

    private static string? TargetFramework(string csproj)
    {
        var text = File.ReadAllText(csproj);
        var m = Regex.Match(text, @"<TargetFrameworks?>\s*([^<;]+)");
        if (m.Success) return m.Groups[1].Value.Trim();
        // Set once for the repository in Directory.Build.props.
        for (var d = Path.GetDirectoryName(csproj); d is not null; d = Path.GetDirectoryName(d))
        {
            var props = Path.Combine(d, "Directory.Build.props");
            if (!File.Exists(props)) continue;
            var pm = Regex.Match(File.ReadAllText(props), @"<TargetFrameworks?>\s*([^<;$]+)");
            if (pm.Success) return pm.Groups[1].Value.Trim();
        }
        return null;
    }

    private static string AssemblyName(string csproj) =>
        Regex.Match(File.ReadAllText(csproj), @"<AssemblyName>\s*([^<]+?)\s*</AssemblyName>") is { Success: true } m && !m.Groups[1].Value.Contains('$')
            ? m.Groups[1].Value : Path.GetFileNameWithoutExtension(csproj);

    /// <summary>The Aspire version the AppHost is on, so the publisher package matches it.</summary>
    public static string? AspireVersionOf(string csprojPath, string srcRoot)
    {
        var text = File.ReadAllText(csprojPath);
        var sdk = Regex.Match(text, @"Aspire\.AppHost\.Sdk/([0-9][^""\s]*)");
        if (sdk.Success) return sdk.Groups[1].Value;
        var sdkEl = Regex.Match(text, @"<Sdk\s+Name=""Aspire\.AppHost\.Sdk""\s+Version=""([^""]+)""");
        if (sdkEl.Success) return sdkEl.Groups[1].Value;
        var pkg = Regex.Match(text, @"Include=""Aspire\.Hosting\.AppHost""\s+Version=""([^""]+)""");
        if (pkg.Success) return pkg.Groups[1].Value;
        if (CentralPackages(Path.GetDirectoryName(csprojPath)!, srcRoot) is { } props)
        {
            var cp = Regex.Match(File.ReadAllText(props), @"Include=""Aspire\.Hosting\.AppHost""\s+Version=""([^""]+)""");
            if (cp.Success) return cp.Groups[1].Value;
        }
        var global = Path.Combine(srcRoot, "global.json");
        if (File.Exists(global))
        {
            var gm = Regex.Match(File.ReadAllText(global), @"""Aspire\.AppHost\.Sdk""\s*:\s*""([^""]+)""");
            if (gm.Success) return gm.Groups[1].Value;
        }
        return null;
    }

    private static int Major(string version) => int.TryParse(version.Split('.')[0], out var n) ? n : 0;

    // The Directory.Packages.props that governs the AppHost, when it turns on central package management.
    private static string? CentralPackages(string dir, string srcRoot)
    {
        var stop = Path.GetFullPath(srcRoot);
        for (var d = Path.GetFullPath(dir); d is not null && d.StartsWith(stop, StringComparison.Ordinal); d = Path.GetDirectoryName(d))
        {
            var props = Path.Combine(d, "Directory.Packages.props");
            if (!File.Exists(props)) continue;
            return Regex.IsMatch(File.ReadAllText(props), @"<ManagePackageVersionsCentrally>\s*true", RegexOptions.IgnoreCase) ? props : null;
        }
        return null;
    }

    private static string InsertItem(string project, string item)
    {
        var close = project.LastIndexOf("</Project>", StringComparison.Ordinal);
        return close < 0 ? project : project.Insert(close, $"  <ItemGroup>\n    {item}\n  </ItemGroup>\n");
    }

    private static IEnumerable<string> SourceFiles(string dir) =>
        Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(dir, f).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(SkipDirs.Contains));
}
