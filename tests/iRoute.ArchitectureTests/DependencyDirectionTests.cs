using System.Xml.Linq;

namespace iRoute.ArchitectureTests;

public sealed class DependencyDirectionTests
{
    private static readonly string[] RuntimeProjects = ["iRoute.Core", "iRoute.Runtime"];
    private static readonly string[] ProviderTokens =
    [
        "api.openai.com",
        "Anthropic",
        "Azure.AI.OpenAI",
        "/chat/completions",
        "/responses"
    ];

    [Fact]
    public void ContractsProjectHasNoProjectReferences()
    {
        var root = FindRepositoryRoot();
        var project = XDocument.Load(Path.Combine(root, "src", "iRoute.Contracts", "iRoute.Contracts.csproj"));
        Assert.Empty(project.Descendants("ProjectReference"));
    }

    [Fact]
    public void CoreDoesNotReferenceInfrastructureOrHosts()
    {
        var root = FindRepositoryRoot();
        var project = File.ReadAllText(Path.Combine(root, "src", "iRoute.Core", "iRoute.Core.csproj"));
        Assert.DoesNotContain("Infrastructure", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("iRoute.Api", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("iRoute.Worker", project, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlanningAndRuntimeContainNoProviderSpecificGatewayProtocol()
    {
        var root = FindRepositoryRoot();
        var files = RuntimeProjects
            .SelectMany(project => Directory.EnumerateFiles(
                Path.Combine(root, "src", project),
                "*.cs",
                SearchOption.TopDirectoryOnly));
        var source = string.Join('\n', files.Select(File.ReadAllText));
        foreach (var providerToken in ProviderTokens)
        {
            Assert.DoesNotContain(providerToken, source, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "iRoute.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
