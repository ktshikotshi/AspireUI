using AspireUI.Server.Services;

// A Dockerfile that names its project or version through an ARG builds nothing useful without the
// compose file's `build.args`. They have to reach the build section of the compose file that runs.
public class ComposeBuildArgsTests
{
    [Theory]
    [InlineData("""
        services:
          api:
            build:
              context: .
              dockerfile: deploy/dotnet.Dockerfile
              args:
                PROJECT: src/Api/Api.csproj
                VERSION: "1.2"
        """)]
    [InlineData("""
        services:
          api:
            build:
              context: .
              dockerfile: deploy/dotnet.Dockerfile
              args:
                - PROJECT=src/Api/Api.csproj
                - VERSION=1.2
        """)]
    public void Build_args_reach_the_published_build_section(string yaml)
    {
        var (stack, error) = new ComposeImporter().Import("s1", "demo", yaml);
        Assert.Null(error);
        var api = stack!.Nodes.Single();
        Assert.Contains(api.WithCalls, w => w.Method == "WithBuildArg" && w.Args.SequenceEqual(["\"PROJECT\"", "\"src/Api/Api.csproj\""]));

        const string published = """
            services:
              api:
                image: "${API_IMAGE}"
            """;
        var injected = HostingService.InjectDockerfileBuilds(published, stack, "/src");

        Assert.Contains("      args:", injected);
        Assert.Contains("        PROJECT: \"src/Api/Api.csproj\"", injected);
        Assert.Contains("        VERSION: \"1.2\"", injected);
    }

    [Fact]
    public void A_build_without_args_writes_no_args_key()
    {
        var (stack, _) = new ComposeImporter().Import("s1", "demo", """
            services:
              api:
                build: .
            """);
        var injected = HostingService.InjectDockerfileBuilds("services:\n  api:\n    image: \"${API_IMAGE}\"\n", stack!, "/src");
        Assert.Contains("    build:", injected);
        Assert.DoesNotContain("args:", injected);
    }
}
