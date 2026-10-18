using AspireUI.Server.Models;
using AspireUI.Server.Services;

// A compose file in which nothing declares a port imports as a running but unreachable stack, so the
// importer falls back to the Dockerfile's EXPOSE, or to a port the user typed in the wizard. A file
// that already reaches its app through something else (a reverse proxy) keeps its other services
// internal.
public class ComposePortFallbackTests
{
    private const string BehindProxy = """
        services:
          app:
            build: .
            environment:
              NODE_ENV: production
          caddy:
            image: caddy:2
            ports:
              - "80:80"
        """;

    private const string NothingPublished = """
        services:
          app:
            build: .
          worker:
            image: acme/worker:1
        """;

    private static string TempDirWithDockerfile(string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "aspireui-portfallback-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Dockerfile"), content);
        return dir;
    }

    private static List<string> Endpoints(StackModel stack, string resource) =>
        stack.Nodes.Single(n => n.ResourceName == resource).WithCalls
            .Where(w => w.Method == "WithHttpEndpoint").SelectMany(w => w.Args).ToList();

    [Fact]
    public void Without_a_source_dir_a_portless_service_stays_portless()
    {
        var (stack, error) = new ComposeImporter().Import("s1", "demo", BehindProxy);
        Assert.Null(error);
        Assert.Empty(Endpoints(stack!, "app"));
    }

    [Fact]
    public void The_dockerfiles_expose_becomes_the_endpoint_when_nothing_else_is_reachable()
    {
        var dir = TempDirWithDockerfile("FROM node:22\nEXPOSE 3000\nCMD [\"node\", \"server.js\"]\n");
        try
        {
            var (stack, error) = new ComposeImporter().Import("s1", "demo", NothingPublished, srcDir: dir);
            Assert.Null(error);
            Assert.Contains("targetPort: 3000", Endpoints(stack!, "app"));
            Assert.Empty(Endpoints(stack!, "worker"));                       // no Dockerfile to read
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Behind_a_reverse_proxy_the_app_stays_internal()
    {
        var dir = TempDirWithDockerfile("FROM node:22\nEXPOSE 3000\n");
        try
        {
            var (stack, error) = new ComposeImporter().Import("s1", "demo", BehindProxy, srcDir: dir);
            Assert.Null(error);
            Assert.Empty(Endpoints(stack!, "app"));
            Assert.Contains("targetPort: 80", Endpoints(stack!, "caddy"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_port_from_the_wizard_wins_over_the_dockerfile()
    {
        var dir = TempDirWithDockerfile("FROM node:22\nEXPOSE 3000\n");
        try
        {
            var (stack, _) = new ComposeImporter().Import("s1", "demo", BehindProxy, srcDir: dir,
                servicePorts: new Dictionary<string, int> { ["app"] = 8080 });
            Assert.Contains("targetPort: 8080", Endpoints(stack!, "app"));
            Assert.DoesNotContain("targetPort: 3000", Endpoints(stack!, "app"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_declared_port_is_never_overridden()
    {
        const string yaml = """
            services:
              app:
                image: acme/app:1
                expose:
                  - "3000"
            """;
        var (stack, _) = new ComposeImporter().Import("s1", "demo", yaml,
            servicePorts: new Dictionary<string, int> { ["app"] = 9999 });
        Assert.Contains("targetPort: 3000", Endpoints(stack!, "app"));
        Assert.DoesNotContain("targetPort: 9999", Endpoints(stack!, "app"));
    }

    [Theory]
    [InlineData("FROM x\nEXPOSE 8080\nEXPOSE 9090\n", 8080)]     // first EXPOSE wins
    [InlineData("FROM x\n  expose 5000  \n", 5000)]              // case and whitespace
    [InlineData("FROM x\n# EXPOSE 1234\n", null)]                // comments are not instructions
    [InlineData("FROM x\n", null)]
    public void DockerfilePort_readsTheFirstExpose(string dockerfile, int? expected)
    {
        var dir = TempDirWithDockerfile(dockerfile);
        try { Assert.Equal(expected, ComposeImporter.DockerfilePort(dir, ".", null)); }
        finally { Directory.Delete(dir, true); }
    }
}
