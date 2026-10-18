using AspireUI.Server.Services;

// An imported AppHost is written to run, not to publish. Hosting has to add the compose environment
// to the copy it publishes from, and build an image for each of its projects.
public class AppHostPublishTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aspireui-apphost-" + Guid.NewGuid().ToString("n")[..8]);
    private static readonly CodeGenService.PublishEnv Compose = PublishService.EnvFor("compose")!;

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string Write(string rel, string content)
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    private string AppHost(string csproj, string program = """
        var builder = DistributedApplication.CreateBuilder(args);
        builder.AddProject<Projects.My_Web>("web");
        builder.Build().Run();
        """)
    {
        Write("AppHost/AppHost.cs", program);
        return Write("AppHost/AppHost.csproj", csproj);
    }

    [Fact]
    public void The_compose_environment_and_its_package_are_added_once()
    {
        var csproj = AppHost("""<Project Sdk="Aspire.AppHost.Sdk/13.4.6"><ItemGroup /></Project>""");

        AppHostPublish.EnsureEnvironment(csproj, Compose, _root);
        AppHostPublish.EnsureEnvironment(csproj, Compose, _root);

        var program = File.ReadAllText(Path.Combine(_root, "AppHost/AppHost.cs"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(program, "AddDockerComposeEnvironment"));
        Assert.True(program.IndexOf("AddDockerComposeEnvironment") < program.IndexOf("AddProject"));
        var proj = File.ReadAllText(csproj);
        // The publisher moves with the AppHost's own Aspire version, not this app's.
        Assert.Contains("""<PackageReference Include="Aspire.Hosting.Docker" Version="13.4.6" />""", proj);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(proj, "Aspire.Hosting.Docker"));
    }

    [Fact]
    public void An_AppHost_that_already_publishes_is_left_alone()
    {
        const string program = """
            var app = DistributedApplication.CreateBuilder(args);
            app.AddDockerComposeEnvironment("prod");
            app.Build().Run();
            """;
        const string project = """<Project Sdk="Aspire.AppHost.Sdk/13.5.2"><ItemGroup><PackageReference Include="Aspire.Hosting.Docker" Version="13.5.2" /></ItemGroup></Project>""";
        var csproj = AppHost(project, program);

        Assert.Equal("", AppHostPublish.EnsureEnvironment(csproj, Compose, _root));
        Assert.Equal(program, File.ReadAllText(Path.Combine(_root, "AppHost/AppHost.cs")));
        Assert.Equal(project, File.ReadAllText(csproj));
    }

    [Fact]
    public void Central_package_management_gets_the_version_in_the_props_file()
    {
        Write("Directory.Packages.props", """
            <Project>
              <PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>
              <ItemGroup><PackageVersion Include="Aspire.Hosting.AppHost" Version="13.5.0" /></ItemGroup>
            </Project>
            """);
        var csproj = AppHost("""<Project Sdk="Microsoft.NET.Sdk"><Sdk Name="Aspire.AppHost.Sdk" Version="13.5.0" /></Project>""");

        AppHostPublish.EnsureEnvironment(csproj, Compose, _root);

        Assert.Contains("""<PackageReference Include="Aspire.Hosting.Docker" />""", File.ReadAllText(csproj));
        Assert.Contains("""<PackageVersion Include="Aspire.Hosting.Docker" Version="13.5.0" />""", File.ReadAllText(Path.Combine(_root, "Directory.Packages.props")));
    }

    [Fact]
    public void Projects_resolve_through_references_and_paths()
    {
        Write("src/My.Web/My.Web.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />");
        Write("src/Worker/Worker.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Worker\" />");
        var csproj = AppHost("""
            <Project Sdk="Aspire.AppHost.Sdk/13.5.2">
              <ItemGroup><ProjectReference Include="..\src\My.Web\My.Web.csproj" /></ItemGroup>
            </Project>
            """, """
            var builder = DistributedApplication.CreateBuilder(args);
            builder.AddProject<Projects.My_Web>("web");
            builder.AddProject("jobs", "../src/Worker/Worker.csproj");
            builder.AddProject<Projects.Missing>("gone");
            builder.Build().Run();
            """);

        var map = AppHostPublish.Projects(csproj);

        Assert.Equal(Path.Combine(_root, "src/My.Web/My.Web.csproj"), map["web"]);
        Assert.Equal(Path.Combine(_root, "src/Worker/Worker.csproj"), map["jobs"]);
        Assert.False(map.ContainsKey("gone"));
    }

    [Fact]
    public void A_project_service_gets_a_build_and_a_pool_port()
    {
        var web = Write("src/Web/Web.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup><TargetFramework>net9.0</TargetFramework><AssemblyName>Acme.Web</AssemblyName></PropertyGroup>
            </Project>
            """);
        const string yaml = """
            services:
              web:
                image: "${WEB_IMAGE}"
                environment:
                  HTTP_PORTS: "${WEB_PORT}"
                ports:
                  - "${WEB_PORT}"
                networks:
                  - "aspire"
              cache:
                image: "docker.io/library/redis:8.6"
            """;

        var (result, built) = AppHostPublish.InjectProjectBuilds(yaml, new Dictionary<string, string> { ["web"] = web }, _root);

        Assert.True(built);
        Assert.DoesNotContain("${WEB_", result);
        Assert.DoesNotContain("WEB_IMAGE", result);
        Assert.Contains("      dockerfile: \".aspireui/web.Dockerfile\"", result);
        Assert.Contains("    expose:\n      - \"8080\"", result);
        Assert.DoesNotContain("ports:", result);
        Assert.Equal([8080], HostingService.ExposedAppPorts(result));
        var dockerfile = File.ReadAllText(Path.Combine(_root, ".aspireui/web.Dockerfile"));
        Assert.Contains("FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build", dockerfile);
        Assert.Contains("dotnet publish \"src/Web/Web.csproj\"", dockerfile);
        Assert.Contains("ENTRYPOINT [\"dotnet\", \"Acme.Web.dll\"]", dockerfile);
        Assert.Contains("**/obj/", File.ReadAllText(Path.Combine(_root, ".aspireui/web.Dockerfile.dockerignore")));
    }

    [Fact]
    public void A_fixed_host_port_stays_where_it_was_asked_for()
    {
        var web = Write("src/Web/Web.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />");
        const string yaml = """
            services:
              web:
                image: "${WEB_IMAGE}"
                ports:
                  - "8000:${WEB_PORT}"
            """;

        var (result, _) = AppHostPublish.InjectProjectBuilds(yaml, new Dictionary<string, string> { ["web"] = web }, _root);

        Assert.Contains("    ports:\n      - \"8000:8080\"", result);
        Assert.DoesNotContain("expose:", result);
    }
}
